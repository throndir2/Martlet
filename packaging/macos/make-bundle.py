#!/usr/bin/env python3
"""Writes Martlet.app's Info.plist and icon, then checks the plist (plutil -lint is macOS-only)."""
import argparse
import plistlib
import struct
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True)
parser.add_argument('--executable', required=True)
parser.add_argument('--version', required=True)
parser.add_argument('--icon', required=True, help='256x256 PNG')
args = parser.parse_args()

contents = Path(args.app) / 'Contents'
info = {
    'CFBundleDevelopmentRegion': 'en',
    'CFBundleDisplayName': 'Martlet',
    'CFBundleExecutable': args.executable,
    'CFBundleIconFile': 'Martlet',
    'CFBundleIdentifier': 'io.github.throndir2.martlet',
    'CFBundleInfoDictionaryVersion': '6.0',
    'CFBundleName': 'Martlet',
    'CFBundlePackageType': 'APPL',
    'CFBundleShortVersionString': args.version,
    'CFBundleVersion': args.version,
    'LSApplicationCategoryType': 'public.app-category.productivity',
    'LSMinimumSystemVersion': '14.0',
    'NSHighResolutionCapable': True,
    'NSMicrophoneUsageDescription': 'Martlet listens to your voice when you talk to your companion.',
    'NSLocalNetworkUsageDescription': 'Martlet connects to your own computers on this network that run listening, thinking or speaking for it.',
}
plist = contents / 'Info.plist'
with plist.open('wb') as f:
    plistlib.dump(info, f, fmt=plistlib.FMT_XML)

# A minimal .icns: one 256x256 PNG entry ('ic08'), which macOS scales as needed.
png = Path(args.icon).read_bytes()
if png[:8] != b'\x89PNG\r\n\x1a\n' or struct.unpack('>II', png[16:24]) != (256, 256):
    raise SystemExit(f'{args.icon} must be a 256x256 PNG.')
entry = b'ic08' + struct.pack('>I', len(png) + 8) + png
(contents / 'Resources' / 'Martlet.icns').write_bytes(b'icns' + struct.pack('>I', len(entry) + 8) + entry)

with plist.open('rb') as f:
    loaded = plistlib.load(f)
if loaded != info or not (contents / 'MacOS' / args.executable).is_file():
    raise SystemExit('Info.plist does not match the bundle.')
print(f'{plist}: OK')
