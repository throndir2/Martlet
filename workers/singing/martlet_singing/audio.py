"""Song audio helpers that need only the standard library: reference WAV parsing, PCM16 files and the lyric sections."""

from __future__ import annotations

import io
import re
import wave

OUTPUT_RATE = 48_000
MAX_REFERENCE_BYTES = 4 * 1024 * 1024
REFERENCE_RATES = {16_000, 22_050, 24_000, 32_000, 44_100, 48_000}
TRACKS = {"mix": 2, "vocals": 1, "backing": 2}


class AudioError(ValueError):
    pass


def parse_reference(data: bytes) -> tuple[bytes, int]:
    """The PCM16 little-endian samples and rate of a mono 16-bit PCM WAV voice recording (1-30 s, as Martlet's shared
    voice list accepts)."""
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
    if not 1.0 <= len(pcm) / 2 / rate <= 30.0:
        raise AudioError("reference duration")
    return pcm, rate


def write_wav(path, pcm: bytes, rate: int, channels: int = 1) -> None:
    with wave.open(str(path), "wb") as writer:
        writer.setnchannels(channels)
        writer.setsampwidth(2)
        writer.setframerate(rate)
        writer.writeframes(pcm)


_TAG = re.compile(r"\A\[(.*)\]\Z")


def lyric_lines(lyrics: str) -> list[tuple[str, str]]:
    """(section, text) of every sung line, as Martlet.Core.Singing.SongLyrics.Parse reads them: the tag's name in lower
    case, numbered from its second occurrence ("verse 2") unless it already ends with a digit; "" before the first tag."""
    seen: dict[str, int] = {}
    section = ""
    lines: list[tuple[str, str]] = []
    for raw in lyrics.split("\n"):
        line = raw.strip()
        if not line:
            continue
        tag = _TAG.match(line)
        if tag:
            name = " ".join(tag.group(1).strip().lower().split())
            if not name:
                continue
            if name[-1].isdigit():
                section = name
            else:
                seen[name] = seen.get(name, 0) + 1
                section = name if seen[name] == 1 else f"{name} {seen[name]}"
            continue
        lines.append((section, line))
    return lines
