"""Converts Resemblyzer's pretrained GE2E voice encoder into Martlet's flat Voice ID weight file.

Source: https://github.com/resemble-ai/Resemblyzer (Apache-2.0), commit
15d828edebe06bc72b9cabc8ef8ca5ab2cb457ce, resemblyzer/pretrained.pt
(SHA-256 39373b86598fa3da9fcddee6142382efe09777e8d37dc9c0561f41f0070f134e).

Output layout (little-endian): ASCII "MVID", int32 version=1, int32 input=40, int32 hidden=256,
int32 layers=3, int32 embedding=256, then per LSTM layer float32 W_ih[4H, in], W_hh[4H, H] and
the pre-summed bias b_ih + b_hh[4H] (PyTorch gate order i, f, g, o), then linear W[E, H] and b[E].
The format change and bias pre-summing are the only modifications; weights are otherwise unchanged.

Usage: python scripts/Convert-SpeakerEncoder.py <pretrained.pt> src/Martlet.Desktop/Assets/SpeakerEncoder.bin
Requires PyTorch only to read the checkpoint.
"""
import struct
import sys

import torch


def main(source: str, target: str) -> None:
    state = torch.load(source, map_location="cpu", weights_only=False)["model_state"]
    with open(target, "wb") as out:
        out.write(b"MVID")
        out.write(struct.pack("<5i", 1, 40, 256, 3, 256))
        for layer in range(3):
            tensors = [
                state[f"lstm.weight_ih_l{layer}"],
                state[f"lstm.weight_hh_l{layer}"],
                state[f"lstm.bias_ih_l{layer}"] + state[f"lstm.bias_hh_l{layer}"],
            ]
            for tensor in tensors:
                out.write(tensor.contiguous().to(torch.float32).numpy().astype("<f4").tobytes())
        for name in ("linear.weight", "linear.bias"):
            out.write(state[name].contiguous().to(torch.float32).numpy().astype("<f4").tobytes())


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
