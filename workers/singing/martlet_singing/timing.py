"""Timing a finished song: a straight beat grid over the whole song and each lyric line's sung start.

beat_grid: librosa's beat tracker on the backing gives a tempo that may be an octave off (it often locks onto eighth
notes); it is halved or doubled toward ACE-Step's planned tempo. A straight grid (period and phase) is then fitted to the
backing's onset strength over the whole song as a comb, so off-beats and partial tracking cannot pull it, and extended
over the whole song. The downbeat is the beat of the bar with the most low-frequency onset strength (bass and kick land on
the one).

lyric_times: ACE-Step's own LRC lines are matched to the request's lines in order (by their words) and each start is
snapped to the matched vocals' onset; without usable LRC, the vocals' phrases (energy dips between lines) are assigned
to the lines in order.
"""

from __future__ import annotations

import difflib
import re
from typing import Any

_LRC = re.compile(r"\[(\d+):(\d+(?:\.\d+)?)\](.*)")
_HOP = 256
_RATE = 22_050


def _comb(envelope, frame_rate: float, period: float, phase: float, duration: float) -> float:
    import numpy as np

    times = np.arange(phase, duration, period)
    index = np.clip(np.round(times * frame_rate).astype(int), 0, len(envelope) - 1)
    return float(envelope[index].mean()) if len(index) else 0.0


def beat_grid(backing, rate: int, planned_bpm: float | None, beats_per_bar: int) -> dict[str, Any]:
    import librosa
    import numpy as np

    duration = len(backing) / rate
    y = librosa.resample(np.asarray(backing, dtype=np.float32), orig_sr=rate, target_sr=_RATE)
    frame_rate = _RATE / _HOP
    envelope = librosa.onset.onset_strength(y=y, sr=_RATE, hop_length=_HOP)
    # A little smoothing so a fitted beat a frame or two off still scores.
    envelope = np.convolve(envelope, np.ones(3) / 3, mode="same")
    try:
        tempo = float(np.atleast_1d(librosa.beat.beat_track(onset_envelope=envelope, sr=_RATE, hop_length=_HOP)[0])[0])
    except Exception:  # noqa: BLE001 - a song without a detectable beat still gets the planned grid
        tempo = 0.0
    candidates = []
    if tempo > 0:
        candidates.append(min((tempo * f for f in (0.25, 0.5, 1.0, 2.0, 4.0)),
                              key=lambda b: abs(np.log2(b / planned_bpm))) if planned_bpm else tempo)
    if planned_bpm:
        candidates.append(planned_bpm)
    if not candidates or duration < 2:
        return {"bpm": None, "beats": [], "downbeats": []}
    # Fit the period (within 3 % of each candidate tempo) and phase that put the most onset strength on the grid. ACE-Step
    # wrote the song at its planned tempo, so that one wins unless the tracked one fits clearly better.
    best = (0.0, 60.0 / candidates[0], 0.0)
    for number, bpm in enumerate(candidates):
        weight = 1.15 if planned_bpm and number == len(candidates) - 1 else 1.0
        for period in np.linspace(60.0 / bpm * 0.97, 60.0 / bpm * 1.03, 31):
            for phase in np.arange(0.0, period, 0.01):
                score = _comb(envelope, frame_rate, period, phase, duration) * weight
                if score > best[0]:
                    best = (score, float(period), float(phase))
    _, period, phase = best
    beats = np.arange(phase, duration, period)
    # Low-frequency onset strength (below about 200 Hz) at each beat; the strongest phase in the bar is the one.
    mel = librosa.feature.melspectrogram(y=y, sr=_RATE, hop_length=_HOP, n_mels=64, fmax=_RATE / 2)
    low = librosa.onset.onset_strength(S=librosa.power_to_db(mel[:6]), sr=_RATE, hop_length=_HOP)
    index = np.clip(np.round(beats * frame_rate).astype(int), 0, len(low) - 1)
    window = np.array([low[max(0, i - 2): i + 3].max() for i in index])
    scores = [float(window[p::beats_per_bar].mean()) if len(window[p::beats_per_bar]) else 0.0 for p in range(beats_per_bar)]
    first = int(np.argmax(scores))
    return {"bpm": round(60.0 / period, 3), "beats": [round(float(b), 4) for b in beats],
            "downbeats": [round(float(b), 4) for b in beats[first::beats_per_bar]]}


def parse_lrc(text: str) -> list[tuple[float, str]]:
    entries = []
    for raw in (text or "").splitlines():
        match = _LRC.match(raw.strip())
        if not match:
            continue
        words = match.group(3).strip()
        if not words or (words.startswith("[") and words.endswith("]")):
            continue
        entries.append((int(match.group(1)) * 60 + float(match.group(2)), words))
    return entries


def _words(text: str) -> str:
    return " ".join(re.sub(r"[^\w' ]+", " ", text.lower()).split())


def _envelope(vocals, rate: int):
    """10 ms RMS of the vocals in dB, and the level that counts as singing (25 dB under the loud parts)."""
    import numpy as np

    hop = rate // 100
    frames = len(vocals) // hop
    if frames == 0:
        return np.zeros(0), 0.0
    energy = np.sqrt(np.mean(np.asarray(vocals[: frames * hop], dtype=np.float32).reshape(frames, hop) ** 2, axis=1))
    db = 20 * np.log10(energy + 1e-9)
    return db, float(np.percentile(db, 95)) - 25.0


def phrases(vocals, rate: int) -> list[tuple[float, float]]:
    """(start, end) of each sung phrase: singing split at silences, and long legato stretches split again at their deepest
    dips (at least 8 dB under the half second around them)."""
    import numpy as np

    db, threshold = _envelope(vocals, rate)
    if len(db) == 0:
        return []
    singing = db >= threshold
    spans: list[list[int]] = []
    start = None
    for i, on in enumerate(singing):
        if on and start is None:
            start = i
        elif not on and start is not None:
            spans.append([start, i])
            start = None
    if start is not None:
        spans.append([start, len(db)])
    merged: list[list[int]] = []
    for span in spans:
        if merged and span[0] - merged[-1][1] < 20:
            merged[-1][1] = span[1]
        else:
            merged.append(span)
    smooth = np.convolve(db, np.ones(5) / 5, mode="same")
    result: list[tuple[float, float]] = []
    for a, b in merged:
        cuts = [a]
        for i in range(a + 100, b - 100):
            window = smooth[max(a, i - 25): i + 26]
            if smooth[i] == window.min() and window.max() - smooth[i] >= 8 and i - cuts[-1] >= 100:
                cuts.append(i)
        cuts.append(b)
        for x, y in zip(cuts, cuts[1:]):
            onset = next((j for j in range(x, y) if db[j] >= threshold), x)
            if y - onset >= 30:
                result.append((onset / 100, y / 100))
    return result


def _onset_near(db, threshold: float, time: float, reach: float = 0.6) -> float:
    lo, hi = max(1, int((time - reach) * 100)), min(len(db) - 1, int((time + reach) * 100))
    for i in range(lo, hi):
        if db[i] >= threshold and db[i - 1] < threshold:
            return i / 100
    return time


def lyric_times(lines: list[tuple[str, str]], lrc: str, vocals, rate: int,
                downbeats: list[float] | None = None) -> list[dict[str, Any]]:
    """Each request line as {"start", "end", "text", "section"} (end may be None); lines the song never reached are left
    out. Without usable LRC, lines start at the vocals' phrase onsets, preferring those just before or on a downbeat."""
    if not lines:
        return []
    db, threshold = _envelope(vocals, rate)
    duration = len(vocals) / rate
    entries = parse_lrc(lrc)
    starts: list[float | None] = [None] * len(lines)
    if entries:
        # Match LRC entries to request lines in order: each entry goes to the best-matching line not before the previous.
        position = 0
        for time, text in entries:
            best, best_ratio = None, 0.0
            for i in range(position, min(len(lines), position + 4)):
                ratio = difflib.SequenceMatcher(None, _words(text), _words(lines[i][1])).ratio()
                if ratio > best_ratio:
                    best, best_ratio = i, ratio
            if best is not None and best_ratio >= 0.5 and starts[best] is None:
                starts[best] = _onset_near(db, threshold, time) if len(db) else time
                position = best + 1
    if sum(s is not None for s in starts) < max(1, len(lines) // 2):
        # Too little from the LRC: use the vocals' phrases in order.
        onsets = [a for a, _ in phrases(vocals, rate)]
        if downbeats:
            near = [a for i, a in enumerate(onsets) if i == 0 or any(-0.75 <= a - d <= 0.35 for d in downbeats)]
            if len(near) >= max(1, len(lines) // 2):
                onsets = near
        starts = [onsets[i] if i < len(onsets) else None for i in range(len(lines))]
    result = []
    for i, (section, text) in enumerate(lines):
        start = starts[i]
        if start is None or start >= duration:
            continue
        following = next((s for s in starts[i + 1:] if s is not None and s > start), None)
        end = _phrase_end(db, threshold, start, following if following is not None else duration)
        result.append({"start": round(start, 3), "end": None if end is None else round(end, 3), "text": text,
                       "section": section})
    return result


def _phrase_end(db, threshold: float, start: float, limit: float) -> float | None:
    """The last frame above the threshold before the next line (or the song's end)."""
    lo, hi = int(start * 100), min(len(db), int(limit * 100))
    for i in range(hi - 1, lo, -1):
        if db[i] >= threshold:
            return min(limit, (i + 1) / 100)
    return None


def _vocal_onsets(vocals, rate: int):
    """Onset times (s) in the matched vocals, from librosa's onset detector."""
    import librosa
    import numpy as np

    y = librosa.resample(np.asarray(vocals, dtype=np.float32), orig_sr=rate, target_sr=_RATE)
    return librosa.onset.onset_detect(y=y, sr=_RATE, hop_length=_HOP, units="time", backtrack=True)


def word_times(lines: list[tuple[str, str]], sentences: list[dict[str, Any]], timed_lines: list[dict[str, Any]], vocals,
               rate: int) -> tuple[list[dict[str, Any]], str, dict[str, Any]]:
    """Every sung word as {"start", "end", "text", "line"} (line indexes timed_lines), where the timing came from, and a
    sanity metric: the median distance (ms) from each word start to the nearest vocal onset, before and after snapping.

    ACE-Step's own alignment (sentences: its sentence and token timestamps of the same generation) gives each word's
    tokens; a word starts with its first token and ends with its last. Each start is snapped to a vocal onset within 120 ms.
    Without it, each line's words are spread evenly over the line ("vocal-phrases")."""
    import numpy as np

    onsets = np.asarray(_vocal_onsets(vocals, rate)) if len(vocals) else np.zeros(0)
    words: list[dict[str, Any]] = []
    source = ""
    if sentences:
        used: set[int] = set()
        for sentence in sentences:
            key = _words(sentence.get("text", ""))
            # The timed line with the same words nearest in time.
            candidates = [i for i, line in enumerate(timed_lines) if i not in used and
                          difflib.SequenceMatcher(None, key, _words(line["text"])).ratio() >= 0.6]
            if not candidates:
                continue
            line_index = min(candidates, key=lambda i: abs(timed_lines[i]["start"] - float(sentence.get("start", 0))))
            used.add(line_index)
            current: dict[str, Any] | None = None
            for token in sentence.get("tokens", []):
                text = str(token.get("text", ""))
                if not text.strip():
                    current = None
                    continue
                starts_word = current is None or text[:1].isspace()
                if starts_word:
                    current = {"start": float(token["start"]), "end": float(token["end"]), "text": text.strip(),
                               "line": line_index}
                    words.append(current)
                else:
                    current["end"] = float(token["end"])
                    current["text"] += text.strip()
        words = [w for w in words if any(ch.isalnum() for ch in w["text"])]
        if words:
            source = "ace-step-alignment"
    if not words:
        for i, line in enumerate(timed_lines):
            parts = line["text"].split()
            end = line["end"] if line["end"] is not None else line["start"] + 0.5 * len(parts)
            each = max(0.05, (end - line["start"]) / max(1, len(parts)))
            for n, part in enumerate(parts):
                words.append({"start": line["start"] + n * each, "end": line["start"] + (n + 1) * each - 0.02,
                              "text": part, "line": i})
        source = "vocal-phrases" if words else ""
    raw = []
    snapped = []
    for word in words:
        if len(onsets):
            nearest = float(onsets[np.argmin(np.abs(onsets - word["start"]))])
            raw.append(abs(nearest - word["start"]))
            if abs(nearest - word["start"]) <= 0.12:
                word["start"] = nearest
            snapped.append(abs(nearest - word["start"]))
    words.sort(key=lambda w: (w["start"], w["line"]))
    for i, word in enumerate(words):
        if i + 1 < len(words) and word["end"] > words[i + 1]["start"]:
            word["end"] = max(word["start"] + 0.02, words[i + 1]["start"])
        word["end"] = max(word["end"], word["start"] + 0.02)
        word["start"], word["end"] = round(word["start"], 3), round(word["end"], 3)
    metric = {"words": len(words), "onsets": int(len(onsets)),
              "median_onset_offset_ms_raw": round(float(np.median(raw)) * 1000, 1) if raw else None,
              "median_onset_offset_ms": round(float(np.median(snapped)) * 1000, 1) if snapped else None}
    return words, source, metric


def gate_vocals(converted, source_vocals, rate: int, below_db: float = 35.0):
    """The matched vocals with everything outside the sung phrases silenced (with 20 ms fades): where the separated
    original vocals are more than below_db under their loud parts, any sound left is backing bleed or conversion noise.
    Returns the gated vocals and how loud what was removed was, in dB relative to the sung parts."""
    import numpy as np

    hop = rate // 100
    frames = min(len(converted), len(source_vocals)) // hop
    if frames == 0:
        return converted, None
    source = np.asarray(source_vocals[: frames * hop], dtype=np.float32).reshape(frames, hop)
    level = 20 * np.log10(np.sqrt(np.mean(source**2, axis=1)) + 1e-9)
    singing = level >= np.percentile(level, 95) - below_db
    # Keep 60 ms around each sung frame so consonants and breaths stay.
    keep = np.convolve(singing.astype(np.float32), np.ones(13), mode="same") > 0
    mask = np.repeat(keep.astype(np.float32), hop)
    fade = np.ones(int(0.02 * rate), dtype=np.float32) / int(0.02 * rate)
    mask = np.convolve(mask, fade, mode="same")
    mask = np.concatenate([mask, np.ones(len(converted) - len(mask), dtype=np.float32) * (mask[-1] if len(mask) else 1)])
    gated = converted * mask
    removed = converted * (1 - mask)
    kept_rms = float(np.sqrt(np.mean(gated[mask > 0.5] ** 2))) if np.any(mask > 0.5) else 0.0
    removed_rms = float(np.sqrt(np.mean(removed[mask < 0.5] ** 2))) if np.any(mask < 0.5) else 0.0
    bleed = 20 * np.log10(removed_rms / kept_rms) if kept_rms > 0 and removed_rms > 0 else None
    return gated.astype(np.float32), (round(bleed, 1) if bleed is not None else None)