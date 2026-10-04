"""The whole turn on this PC: from the end of your speech to the voice's first audio, for three flows side by side.

cascade  speech-to-text, then the transcript to the Thinking model (Martlet's default)
hearing  speech-to-text, then the transcript and the recording (Martlet's 'Let Thinking hear my voice')
omni     only the recording to a model that hears: no speech-to-text before the reply

Each turn's first speakable piece goes to the voice the moment it is complete, while the model keeps writing, as Martlet does."""

from __future__ import annotations

import threading
from typing import Any

import numpy as np

from . import stt as stt_module
from .common import GpuMemory, Results, load_clips, log, ms, normalize, now, stats, table, wer, write_wav
from .think import ChatClient, LocalServer, messages_for, parse_target
from .tts import make_engine, voice

FLOWS = {"cascade": "text", "hearing": "audio+text", "omni": "audio"}


def benchmark(args: Any) -> Results:
    clips = load_clips(args.clips, args.limit)
    flows = [f.strip() for f in args.modes.split(",")]
    for flow in flows:
        if flow not in FLOWS:
            raise SystemExit(f"Unknown flow '{flow}' ({', '.join(FLOWS)})")
    needs_stt = any(f != "omni" for f in flows)
    target = parse_target(args.think)
    if target.kind == "cloud" and not args.allow_cloud:
        raise SystemExit(f"{args.think} is a paid cloud target; pass --allow-cloud with its key to include it.")
    tts = None if args.tts_engine == "none" else make_engine(args)
    chosen = voice(args.voice)
    results = Results("pipeline", {"clips": args.clips, "count": len(clips), "stt": args.stt if needs_stt else None,
                                   "think": args.think, "flows": flows, "tts": args.tts_engine, "tts_url": args.tts_url,
                                   "tts_assumed_ms": None if tts else args.tts_assumed_ms, "voice": chosen.name,
                                   "endpoint_ms": args.endpoint_ms, "repeat": args.repeat, "max_tokens": args.max_tokens})
    engine = None
    if needs_stt:
        engine = stt_module.create(args.stt, args.beam)
        log(f"Loading speech-to-text {args.stt}")
        engine.load()
    folder = results.folder if tts else None
    turns: list[dict[str, Any]] = []
    with GpuMemory() as gpu, LocalServer(target, pull=args.pull, context=args.context, bits=args.bits):
        client = ChatClient(target)

        def turn(clip: Any, flow: str, save: bool) -> dict[str, Any]:
            wav = clip.wav
            started = now()
            transcript = stt_ms = None
            if flow != "omni":
                transcript = engine.transcribe(clip.samples)
                stt_ms = ms(now() - started)
            spoken: dict[str, Any] = {}

            def speak(piece: str, at: float) -> None:
                def run() -> None:
                    spoken["speech"] = tts.synthesize(piece, chosen)

                spoken["at"] = at
                if tts is not None:
                    spoken["thread"] = threading.Thread(target=run, daemon=True)
                    spoken["thread"].start()

            think_started = now()
            reply = client.stream(messages_for(FLOWS[flow], transcript, wav), args.max_tokens, on_piece=speak)
            if "thread" in spoken:
                spoken["thread"].join(120)
            speech = spoken.get("speech")
            row = {"clip": clip.id, "flow": flow, "seconds": round(clip.seconds, 2), "stt_ms": stt_ms,
                   "think_first_token_ms": reply.first_token_ms, "think_first_piece_ms": reply.first_piece_ms,
                   "tts_first_audio_ms": speech.first_audio_ms if speech else (None if tts else args.tts_assumed_ms),
                   "transcript": transcript, "reference": clip.text, "piece": reply.piece, "reply": reply.text,
                   "error": reply.error or (speech.error if speech else None)}
            if reply.first_piece_ms is not None and row["tts_first_audio_ms"] is not None and "at" in spoken:
                row["total_ms"] = ms(spoken["at"] - started) + row["tts_first_audio_ms"]
            else:
                row["total_ms"] = None
            row["think_wait_ms"] = ms(think_started - started)
            if save and speech is not None and not speech.error and folder is not None:
                write_wav(folder / f"{clip.id}-{flow}.wav", speech.samples(), speech.rate)
            return row

        for flow in flows:
            warm = turn(clips[0], flow, save=False)
            if warm["error"]:
                log(f"   warm-up {flow}: {warm['error']}")
        for attempt in range(args.repeat):
            for clip in clips:
                for flow in flows:
                    row = turn(clip, flow, save=attempt == 0)
                    turns.append(row)
                    log(f"  {clip.id} {flow:8} stt {row['stt_ms']} | piece {row['think_first_piece_ms']} | voice "
                        f"{row['tts_first_audio_ms']} | first audio {row['total_ms']} ms"
                        + (f"  ERROR {row['error']}" if row["error"] else f"  {(row['piece'] or '')[:50]!r}"))
    if engine is not None:
        engine.close()
    for flow in flows:
        rows = [t for t in turns if t["flow"] == flow and not t["error"]]
        total = stats(t["total_ms"] for t in rows)
        results.rows.append({
            "flow": flow, "stt": stats(t["stt_ms"] for t in rows), "first_piece": stats(t["think_first_piece_ms"] for t in rows),
            "first_token": stats(t["think_first_token_ms"] for t in rows), "voice": stats(t["tts_first_audio_ms"] for t in rows),
            "total": total, "after_you_stop_median": None if total["median"] is None else round(total["median"] + args.endpoint_ms),
            "stt_wer": wer([t["reference"] for t in rows], [t["transcript"] for t in rows]) if flow != "omni" else None,
            "errors": len([t for t in turns if t["flow"] == flow]) - len(rows), "gpu_added_mb": gpu.added_mb})
    results.details = turns
    table_rows = [[r["flow"], r["stt"]["median"], r["first_token"]["median"], r["first_piece"]["median"], r["voice"]["median"],
                   r["total"]["median"], r["total"]["p90"], r["after_you_stop_median"], r["stt_wer"], r["errors"]] for r in results.rows]
    voice_note = (f"the voice is {args.tts_engine} at {args.tts_url} ({chosen.name})" if tts else
                  f"the voice is NOT RUN: its first audio is assumed to take {args.tts_assumed_ms} ms")
    results.markdown = (
        f"`{args.think}`" + (f" with speech-to-text `{args.stt}`" if needs_stt else "") + f"; {voice_note}. Clip set "
        f"`{args.clips}` ({len(clips)} turns x {args.repeat}). *First audio* runs from the end of the recording (speech-to-text "
        f"starts) to the voice's first audio; *after you stop* adds the {args.endpoint_ms} ms end-of-speech pause. "
        "The Thinking times count from the request.\n\n" +
        table(["Flow", "Speech-to-text ms", "First word ms", "First piece ms", "Voice first audio ms", "First audio ms", "p90",
               "After you stop ms", "STT WER %", "Errors"], table_rows))
    print("\n" + results.markdown)
    log(f"\nSaved {results.save()}")
    return results
