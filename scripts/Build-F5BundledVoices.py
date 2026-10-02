"""Builds the reference voices Martlet bundles for F5 (src/Martlet.F5/BundledVoices) from their pinned upstream recordings.

Every source is public domain (LJ Speech, LibriVox) or CMU ARCTIC (free for any use; its notice is kept in
src/Martlet.F5/BundledVoices/NOTICES.txt). Download the sources into one directory with their upstream layout:

  LJSpeech-1.1/wavs/LJ001-0009.wav           https://data.keithito.com/data/speech/LJSpeech-1.1.tar.bz2
  librivox/frankenstein_01_shelley.mp3        https://archive.org/download/frankenstein_cs_librivox/
  librivox/secondsight_01_leverson.mp3        https://archive.org/download/lovesecondsight_1604_librivox/
  cmu_us_<speaker>_arctic/wav/arctic_a00NN.wav http://festvox.org/cmu_arctic/cmu_arctic/cmu_us_<speaker>_arctic/wav/

then run (needs numpy and miniaudio):

  python scripts/Build-F5BundledVoices.py <sources directory>

Modifications (the same for every clip, marked here as the CMU ARCTIC terms require): the spoken passage is cut out at
its silences (short pauses kept, ARCTIC's two sentences joined by 0.3 s of silence), DC offset and rumble below 50 Hz are
removed, the level is set to -20 dBFS speech RMS (peaks at most -1 dBFS), 10 ms fades are applied at each cut,
LibriVox MP3s are decoded and resampled to 24 kHz, and the result is written as mono 16-bit PCM WAV.
"""

import hashlib
import pathlib
import sys
import wave

import miniaudio
import numpy as np

ARCTIC_SENTENCES = [("arctic_a0058", "I came for information more out of curiosity than anything else."),
                    ("arctic_a0039", "The ship should be in within a week or ten days.")]
ARCTIC_SOURCES = {
    "slt": ("08f5ad6bfdbcc9327bc9b558b0dd753d110273ccb6d367499ef9ae24c2257498", "4d2df5a499114499467289b26898d8e7e6ea12f5d3acc99592e194afa27b80fa"),
    "clb": ("9570562f6f0c8130a57c4a9274c2d3ff9c9d05fb4d9311a84a251c097012b61f", "d59f629e47146a8d4e5e1135fbb449a76e567f3c44765876a0259ba72bf44673"),
    "bdl": ("cedcfe39c544f6facf42d791c0d5bc72fff6e5b912c9365a0fdd21f88c71b7d2", "6c2d5d014826053e4dfbfd3760725214809ab181245f1a8b2609f6326c4e857b"),
    "rms": ("e7dbe8f7688bc6242cbeb0044ac6bbfa04322656fb94f254fd9a93b4178ed74f", "bdaf4c3d81972c3ac0c49a2fe2f3b897b98de542a9901f03857e0a637fc0c9cd"),
    "awb": ("ee2545275996be9154a222ad16188fe2462b9575b99c17fd6bfb117472e96c94", "2efa2819b0ec3e48d31067f68693c3c6d4c269bd5a179ac2e925e5ca2dc20bad"),
    "jmk": ("5b9a18e71097597582c223fd1c2abed63b40568b6ac36c5ea9e04847fd883c44", "031cde96d8eeab53ccc213f945f9a19d3d2deefb23712a59b80a30233b24376d"),
    "ksp": ("04790d7ee04e8873aec54a156b52e9831d1437894f20fabefd858b91173d280f", "e94c2905772c8625239b8868943766a63a2c55e51ca17258bf283f72dfaa2d0c"),
}

# key: [(relative source path, SHA-256, start s, end s or None for the whole file)], output sample rate
VOICES = {
    "lj-speech": ([("LJSpeech-1.1/wavs/LJ001-0009.wav", "d003373f9c769b17995cfff2a99818e720a56f3376678b27fc6c9c27539c3f75", 0, None)], None),
    # "I have thus endeavoured to preserve the truth of the elementary principles of human nature, while I have not
    # scrupled to innovate upon their combinations." (Preface; the passage sits between 105.24 s and 113.87 s.)
    "librivox-cori-samuel": ([("librivox/frankenstein_01_shelley.mp3", "96068a62c2549a698a35e986c8425368d0fbd4ad757bad9a5b5690c4e2b4b951", 105.24, 113.87)], 24_000),
    # "She was a slim, fair, pretty woman, with more vividness and character than usually goes with her type." (Chapter I)
    "librivox-helen-taylor": ([("librivox/secondsight_01_leverson.mp3", "b49193791e6090b0623caeac494c6f57596a9ef0c1d9d4f621de97286480898a", 110.88, 117.93)], 24_000),
}
for speaker, digests in ARCTIC_SOURCES.items():
    VOICES[f"arctic-{speaker}"] = ([(f"cmu_us_{speaker}_arctic/wav/{name}.wav", digest, 0, None)
                                    for (name, _), digest in zip(ARCTIC_SENTENCES, digests)], None)

FRAME_SECONDS = 0.01
PRE_PAD, POST_PAD, GAP, LEAD, TAIL = 0.06, 0.15, 0.30, 0.05, 0.25
TARGET_RMS_DB, PEAK_LIMIT_DB = -20.0, -1.0


def load(path: pathlib.Path, digest: str):
    data = path.read_bytes()
    actual = hashlib.sha256(data).hexdigest()
    if actual != digest:
        raise SystemExit(f"{path}: SHA-256 {actual} is not the pinned {digest}")
    if path.suffix == ".wav":
        with wave.open(str(path), "rb") as reader:
            if reader.getnchannels() != 1 or reader.getsampwidth() != 2:
                raise SystemExit(f"{path}: expected mono 16-bit PCM")
            return np.frombuffer(reader.readframes(reader.getnframes()), dtype="<i2").astype(np.float64) / 32768, reader.getframerate()
    decoded = miniaudio.decode(data, output_format=miniaudio.SampleFormat.FLOAT32, nchannels=1)
    return np.asarray(decoded.samples, dtype=np.float64), decoded.sample_rate


def resample(x, rate, target):
    if target is None or target == rate:
        return x, rate
    count = int(round(len(x) * target / rate))
    spectrum = np.fft.rfft(x)
    kept = np.zeros(count // 2 + 1, dtype=complex)
    bins = min(len(kept), len(spectrum))
    kept[:bins] = spectrum[:bins]
    return np.fft.irfft(kept, count) * (count / len(x)), target


def high_pass(x, rate, cutoff=50.0):
    x = x - x.mean()
    alpha = 1 / (1 + 2 * np.pi * cutoff / rate)
    y = np.empty_like(x)
    previous_in = previous_out = 0.0
    for index, value in enumerate(x):
        previous_out = alpha * (previous_out + value - previous_in)
        previous_in = value
        y[index] = previous_out
    return y


def levels(x, rate):
    frame = int(rate * FRAME_SECONDS)
    count = len(x) // frame
    rms = np.sqrt(np.mean(x[: count * frame].reshape(count, frame) ** 2, axis=1) + 1e-12)
    return 20 * np.log10(rms), frame


def speech(x, rate):
    """The spoken part with short natural pads, cut at its silences, and its active frames' mask."""
    db, frame = levels(x, rate)
    threshold = max(np.percentile(db, 10) + 12, db.max() - 40)
    active = np.flatnonzero(db > threshold)
    start = max(0, active[0] * frame - int(PRE_PAD * rate))
    end = min(len(x), (active[-1] + 1) * frame + int(POST_PAD * rate))
    piece = x[start:end].copy()
    fade = int(0.01 * rate)
    ramp = 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, fade))
    piece[:fade] *= ramp
    piece[-fade:] *= ramp[::-1]
    return piece, threshold


def build(sources: pathlib.Path, key: str):
    parts, target = VOICES[key]
    pieces, rate, thresholds = [], None, []
    for relative, digest, start, end in parts:
        x, source_rate = load(sources / relative, digest)
        x = x[int(start * source_rate): None if end is None else int(end * source_rate)]
        x, piece_rate = resample(high_pass(x, source_rate), source_rate, target)
        if rate not in (None, piece_rate):
            raise SystemExit(f"{key}: sources differ in sample rate")
        rate = piece_rate
        piece, threshold = speech(x, rate)
        pieces.append(piece)
        thresholds.append(threshold)
    silence = lambda seconds: np.zeros(int(seconds * rate))
    joined = [silence(LEAD)]
    for index, piece in enumerate(pieces):
        if index:
            joined.append(silence(GAP))
        joined.append(piece)
    joined.append(silence(TAIL))
    audio = np.concatenate(joined)
    db, frame = levels(audio, rate)
    active = np.repeat(db > min(thresholds), frame)
    speech_rms = np.sqrt(np.mean(audio[: len(active)][active] ** 2))
    gain = min(10 ** (TARGET_RMS_DB / 20) / speech_rms, 10 ** (PEAK_LIMIT_DB / 20) / np.abs(audio).max())
    samples = np.clip(np.round(audio * gain * 32768), -32768, 32767).astype("<i2")
    return samples, rate


def main():
    if len(sys.argv) not in (2, 3):
        raise SystemExit(__doc__)
    sources = pathlib.Path(sys.argv[1])
    output = pathlib.Path(sys.argv[2]) if len(sys.argv) == 3 else pathlib.Path(__file__).resolve().parents[1] / "src" / "Martlet.F5" / "BundledVoices"
    output.mkdir(parents=True, exist_ok=True)
    for key in VOICES:
        samples, rate = build(sources, key)
        path = output / f"{key}.wav"
        with wave.open(str(path), "wb") as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(rate)
            writer.writeframes(samples.tobytes())
        print(f"{key}: {rate} Hz, {len(samples) / rate:.2f} s, SHA-256 {hashlib.sha256(path.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
