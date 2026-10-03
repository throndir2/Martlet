"""Reference WAV parsing and 24 kHz PCM16 framing (standard library only)."""

from __future__ import annotations

import io
import wave

OUTPUT_RATE = 24_000
FRAME_SAMPLES = 4_800  # 200 ms, the martlet.f5.worker frame bound the gateway checks
MAX_REFERENCE_BYTES = 4 * 1024 * 1024
REFERENCE_RATES = {16_000, 22_050, 24_000, 44_100, 48_000}


class AudioError(ValueError):
    pass


def parse_reference(data: bytes) -> tuple[bytes, int]:
    """The PCM16 little-endian samples and sample rate of a mono 16-bit PCM WAV reference (1-30 s, as Martlet's voice
    store accepts)."""
    if not 44 <= len(data) <= MAX_REFERENCE_BYTES:
        raise AudioError("reference size")
    try:
        with wave.open(io.BytesIO(data), "rb") as reader:
            channels, width, rate, count = reader.getnchannels(), reader.getsampwidth(), reader.getframerate(), reader.getnframes()
            if channels != 1 or width != 2 or rate not in REFERENCE_RATES:
                raise AudioError("reference format")
            pcm = reader.readframes(count)
    except (wave.Error, EOFError) as error:
        raise AudioError("reference wav") from error
    seconds = len(pcm) / 2 / rate
    if not 1.0 <= seconds <= 30.0:
        raise AudioError("reference duration")
    return pcm, rate


def frames(pcm: bytes) -> list[bytes]:
    """Splits PCM16 bytes into frames of at most FRAME_SAMPLES samples."""
    step = FRAME_SAMPLES * 2
    return [pcm[offset : offset + step] for offset in range(0, len(pcm) - len(pcm) % 2, step)]
