"""Stand-in for pyworld, which Amphion's modules import but VevoSing's conversion never calls (pyworld has no Linux wheel
for Python 3.11, so the image does not compile it). Any use fails loudly."""


def __getattr__(name: str):
    raise RuntimeError(f"pyworld.{name} is not available in the Martlet singing image.")
