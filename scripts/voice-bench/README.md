# voicebench: measure the voice pipeline before building it into Martlet

Scripts that time each stage of a spoken turn on this PC (speech-to-text, the
Thinking model, the cloned voice) and the whole turn. They compare options
before they are wired into Martlet. Everything runs locally; cloud targets
only run with `--allow-cloud` and your own key in the environment. Results
are this PC's numbers, not qualification.

Everything lives outside the repository and Martlet's data, in
`%LOCALAPPDATA%\MartletBench` (or `$env:MARTLET_BENCH_HOME`): the Python
environments, models, clip sets and results. Delete that folder to remove it
all.

## Install

```powershell
scripts\voice-bench\Install-VoiceBench.ps1 -Torch -LlamaCpp
# Optional PyTorch runners for models llama.cpp can't give audio:
scripts\voice-bench\Install-VoiceBench.ps1 -HfModels phi-4-multimodal, minicpm-o-2.6
```

Needs Python 3.12 (`py -3.12`) and an NVIDIA GPU for the GPU engines. Ollama
and Docker Desktop are used when present. `-LlamaCpp` downloads a pinned
llama.cpp CUDA build (for the models that hear). Models download on first use,
or ahead of time with `voicebench models --fetch NAME`.

Run everything through `scripts\voice-bench\Invoke-VoiceBench.ps1` (below:
`vb`):

```powershell
Set-Alias vb scripts\voice-bench\Invoke-VoiceBench.ps1
vb env       # what this PC has
vb models    # every engine and model the bench knows
```

## Clip sets

| Set | Command | What it is |
| --- | --- | --- |
| `librispeech` | `vb clips fetch` | 73 real read-speech utterances (LibriSpeech dev-clean), 2-30 s, with references: the accuracy set |
| `prompts-sapi` | `vb clips synth` | The 16 companion prompts in `data/prompts.json`, said by Windows' voices |
| `prompts-voice` | `vb clips synth --engine voice` | The same prompts said by a Martlet voice worker (Chatterbox Turbo) in the starter voices |
| `prompts-mine` | `vb clips record` | You saying each prompt into the default microphone (the most realistic; used first when present) |
| `ami-headset` | `vb clips fetch ami-headset` | 32 real spontaneous meeting turns (2-6.5 s, 4+ words, about a dozen native and non-native English speakers) from the AMI Meeting Corpus test split, each speaker's headset microphone. CC BY 4.0, via [edinburghcstr/ami](https://huggingface.co/datasets/edinburghcstr/ami); only the chosen clips download |
| `ami-room` | `vb clips fetch ami-room` | The same kind of turns from AMI's single distant microphone in the meeting room (reverberant, overlapping talk) |
| `prompts-voice-deskmic` | `vb clips degrade prompts-voice --name prompts-voice-deskmic` | The companion prompts as if said into a desk microphone across a room: synthetic reverb (RT60 0.25-0.6 s), fan, hum and room noise at 10-20 dB SNR, a 120 Hz-7 kHz microphone band, levels from -14 to +2 dB and 0.3-1 s of room tone around the words (deterministic per `--seed`) |
| any | `vb clips import FOLDER --name SET` | Your own WAV/FLAC/MP3 files, each with a same-name `.txt` reference |

Downloads and generated audio are staged in the bench folder's `sources`
and imported from there; each set keeps a `SOURCE.txt` naming its origin and
licence. Nothing is written to the repository.

## Benchmarks

**Speech-to-text** (each engine in its own process, one warm-up, then every
clip; word error rate with Whisper's English normalizer):

```powershell
vb stt --clips librispeech        # the main comparison
vb stt --engines parakeet-v3 fw-large-v3-turbo omni:ollama:gemma4:e2b
```

Engines include Martlet's Parakeet v3 (CPU, sherpa-onnx), Parakeet v2,
Moonshine, Qwen3-ASR, faster-whisper (small.en up to large-v3, GPU or CPU),
Whisper large-v3 in PyTorch, whisper.cpp (Martlet's host role) and OpenAI
(cloud). `omni:TARGET` asks a model that hears to transcribe.

**Thinking** (time to the first word and to the first piece the voice can
say, Martlet's segmenter rule; Thinking steps Off):

```powershell
vb think --targets ollama:gemma4:e2b llamacpp:qwen2.5-omni-7b hf:phi-4-multimodal --modes text,audio,audio+text
```

| Mode | What is sent |
| --- | --- |
| `text` | The transcript (the cascade: speech-to-text first) |
| `audio` | Only the recording: an omni model, no speech-to-text |
| `audio+text` | Both (Martlet's *Let Thinking hear my voice*) |

Targets: `ollama:TAG`, `llamacpp:NAME` (Qwen2.5-Omni 3B/7B, Qwen3-Omni
30B-A3B, Voxtral Mini, Gemma 4 E2B/E4B, Qwen2-Audio 7B), `hf:NAME`
(Phi-4-multimodal, MiniCPM-o 2.6, through `voicebench.hfserve`),
`openrouter:MODEL`, `openai:MODEL` or `URL#MODEL` for any Chat Completions
server. The bench starts and stops each local server itself and unloads other
Ollama models first. Every target gets the same system prompt
(`data/system.txt`, Martlet's spoken-reply and Chatterbox tag instructions)
and Martlet's own per-message notes.

**Voice** (time to first audio and real-time factor; the audio is saved for
listening):

```powershell
vb relay start                    # reach the Martlet host roles running on this PC
vb tts --voice librivox-annie     # or a WAV path over 5 s
```

`vb relay start` exposes the Chatterbox/F5/Dia and whisper.cpp roles already
running on this PC (they listen only on the host's internal loopback) on
`127.0.0.1`, so the bench measures the installed services without loading a
second copy of a model. It prints each role's address: Chatterbox Turbo is on
port 50093 (the default `--tts-url`), Chatterbox Nano on 50098 and Chatterbox
Original on 50099 (for example `vb tts --tts-url http://127.0.0.1:50098`).
`vb relay stop` removes it. `vb chatterbox start` runs
this checkout's Chatterbox service instead (only when no Chatterbox role is
running: two copies don't fit beside each other on most cards).
`--tts-engine openai-speech --tts-url URL` drives any `/v1/audio/speech`
server.

**The whole turn** (from the end of the recording to the voice's first
audio, with the first piece handed to the voice the moment it is complete,
as Martlet does):

```powershell
vb pipeline --think ollama:gemma4:e2b --stt fw-large-v3-turbo --modes cascade,hearing,omni
```

`cascade` is speech-to-text then text, `hearing` sends the transcript and
recording, and `omni` sends only the recording. *After you stop* adds the
end-of-speech pause (`--endpoint-ms`, 800 by default). `--tts-engine none`
assumes the voice's first audio instead of calling it.

Each run prints a Markdown table and saves `results\<time>-<command>.md`
and `.json` (every clip's timings and texts) under the bench folder;
`vb results` lists the newest.

## Caveats

- The graphics card is shared. With Martlet's host roles running (voice,
  speech-to-text, lip-sync), a model that doesn't fit beside them spills into
  Windows' shared memory and slows down several times. The tables report the
  memory each run added; compare like with like.
- Generated speech (Windows or Chatterbox) is cleaner than a real microphone;
  record `prompts-mine` before trusting accuracy differences between close
  engines.
- Cloud targets (OpenRouter, OpenAI) are paid and are skipped unless
  `--allow-cloud` is passed with a key in the environment.
