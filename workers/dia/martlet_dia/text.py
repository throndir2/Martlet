"""Turns a reference transcript and reply text into single-speaker Dia prompts (pure; no torch)."""

from __future__ import annotations

import re

# Dia's text encoder reads at most 1024 bytes (encoder max_position_embeddings) with [S1]/[S2] as one byte each, and
# its decoder at most 3072 audio tokens (about 86 per second, reference included).
MAX_TEXT_BYTES = 1024
MAX_AUDIO_TOKENS = 3072
TOKENS_PER_SECOND = 44_100 / 512
# Conservative speaking rate, so a piece's speech fits the audio tokens the reference leaves; Dia also speeds up
# unnaturally past about 20 seconds per generation.
CHARACTERS_PER_SECOND = 12
MAX_PIECE_CHARACTERS = 220
MIN_PIECE_CHARACTERS = 40

_SPEAKER = re.compile(r"\[\s*S\s*\d+\s*\]", re.IGNORECASE)
_SPACE = re.compile(r"\s+")
_SENTENCE = re.compile(r"(?<=[.!?])\s+")
_CLAUSE = re.compile(r"(?<=[,;:])\s+")


class PromptError(ValueError):
    """The reference transcript leaves no room for reply text."""


def clean(text: str) -> str:
    """Removes speaker tags (Martlet always speaks as one speaker) and collapses whitespace. Nonverbal cues such as
    "(laughs)" pass through unchanged."""
    return _SPACE.sub(" ", _SPEAKER.sub(" ", text)).strip()


def _encoded_length(text: str) -> int:
    return len(text.encode("utf-8").replace(b"[S1]", b"\x01"))


def _split(text: str, limit: int) -> list[str]:
    if len(text) <= limit:
        return [text]
    for pattern in (_SENTENCE, _CLAUSE):
        parts = [part for part in pattern.split(text) if part]
        if len(parts) > 1:
            pieces: list[str] = []
            current = ""
            for part in parts:
                candidate = f"{current} {part}".strip()
                if len(candidate) <= limit:
                    current = candidate
                    continue
                if current:
                    pieces.append(current)
                current = part
            if current:
                pieces.append(current)
            return [piece for part in pieces for piece in _split(part, limit)]
    words = text.split(" ")
    pieces, current = [], ""
    for word in words:
        while len(word) > limit:
            if current:
                pieces.append(current)
                current = ""
            pieces.append(word[:limit])
            word = word[limit:]
        candidate = f"{current} {word}".strip()
        if len(candidate) <= limit:
            current = candidate
        else:
            pieces.append(current)
            current = word
    if current:
        pieces.append(current)
    return pieces


def prompts(transcript: str, text: str, reference_seconds: float) -> list[str]:
    """Dia prompts for one reply chunk: "[S1] <reference transcript> <piece>" for each piece of the reply, so the
    cloned voice (the audio prompt) says only the reply. Dia generates the audio after the reference only."""
    prefix = f"[S1] {clean(transcript)} "
    body = clean(text)
    if not body:
        return []
    free_tokens = MAX_AUDIO_TOKENS - reference_seconds * TOKENS_PER_SECOND - 64
    free_bytes = MAX_TEXT_BYTES - _encoded_length(prefix) - 8
    limit = min(MAX_PIECE_CHARACTERS, int(free_tokens / TOKENS_PER_SECOND * CHARACTERS_PER_SECOND), free_bytes // 4)
    if limit < MIN_PIECE_CHARACTERS:
        raise PromptError("The voice recording or its transcript is too long for Dia; use a 5 to 10 second clip.")
    return [prefix + piece for piece in _split(body, limit)]
