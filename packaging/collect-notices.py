#!/usr/bin/env python3
"""Collects Martlet's LICENSE and the third-party notices for the Linux and macOS packages.

Reads every *.deps.json in the given publish folders and writes into --dest:
  LICENSE.txt                    Martlet's binary-use terms (the repository LICENSE)
  README.txt                     what the folder holds and the Live2D terms (packaging/NOTICES-README.txt)
  <runtime pack>/LICENSE.txt     and THIRD-PARTY-NOTICES.txt of each bundled .NET runtime pack
  THIRD-PARTY-NOTICES.txt        every NuGet package: id, version, copyright and its complete license text(s)
  Martlet.<project>-*            Martlet's own notice files for the assemblies that are in the package
It stops with an error when a license or notice cannot be found, so a package never ships without them.

  python3 packaging/collect-notices.py --dest <notices> --publish <publish folder> [--publish <folder>]
      [--packages <NuGet global packages folder>] [--dotnet-root <.NET install folder>]
"""
import argparse
import json
import os
import re
import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LICENSES = ROOT / "packaging" / "licenses"
NOTICE_NAME = re.compile(r"(licen[cs]e|notice|copying)", re.IGNORECASE)
NOTICE_EXT = {"", ".txt", ".md", ".rst"}
# Martlet's own notice files, copied when the assembly that needs them is in the package.
AUTHORED = {
    "Martlet.Avatar.Audio2Face.dll": ["src/Martlet.Avatar.Audio2Face/THIRD-PARTY-NOTICES.md",
                                      "src/Martlet.Avatar.Audio2Face/Protos/LICENSE-2.0.txt"],
    "Martlet.F5.dll": ["src/Martlet.F5/BundledVoices/NOTICES.txt"],
    "Martlet.Sherpa.dll": ["src/Martlet.Sherpa/VOICE-RECOGNITION-NOTICES.txt"],
    "Martlet.Discord.dll": ["src/Martlet.Discord/DISCORD-VOICE-NOTICES.txt"],
}


def fail(message):
    print(f"collect-notices: {message}", file=sys.stderr)
    sys.exit(1)


def read(path):
    return Path(path).read_text(encoding="utf-8-sig", errors="replace").strip() + "\n"


def notice_files(folder):
    return sorted(f for f in folder.iterdir() if f.is_file() and NOTICE_NAME.search(f.name)
                  and f.suffix.lower() in NOTICE_EXT)


def nuspec(folder):
    specs = list(folder.glob("*.nuspec"))
    if len(specs) != 1:
        fail(f"expected one .nuspec in {folder}")
    metadata = ET.parse(specs[0]).getroot().find("{*}metadata")
    value = lambda name: (metadata.findtext("{*}" + name) or "").strip()
    license = metadata.find("{*}license")
    return {"authors": value("authors"), "copyright": value("copyright"), "projectUrl": value("projectUrl"),
            "licenseUrl": value("licenseUrl"), "licenseType": license.get("type") if license is not None else "",
            "license": (license.text or "").strip() if license is not None else ""}


def expression_texts(expression, copyright):
    if not re.match(r"(copyright|\(c\)|©)", copyright, re.IGNORECASE):
        copyright = "Copyright (c) " + (copyright or "the copyright holders")
    texts = []
    for spdx in sorted(set(re.findall(r"[A-Za-z0-9.+-]+", expression)) - {"AND", "OR", "WITH"}):
        template = LICENSES / f"{spdx}.txt"
        if not template.is_file():
            fail(f"no license text for SPDX '{spdx}'; add packaging/licenses/{spdx}.txt")
        texts.append(f"--- {spdx} ---\n" + read(template).replace("{copyright}", copyright))
    return texts


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dest", required=True)
    parser.add_argument("--publish", action="append", required=True)
    parser.add_argument("--packages", default=os.environ.get("NUGET_PACKAGES") or str(Path.home() / ".nuget" / "packages"))
    parser.add_argument("--dotnet-root", default="")
    args = parser.parse_args()
    dest = Path(args.dest)
    packages = Path(args.packages)
    dest.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(ROOT / "LICENSE", dest / "LICENSE.txt")
    shutil.copyfile(ROOT / "packaging" / "NOTICES-README.txt", dest / "README.txt")

    libraries, assemblies = {}, set()
    for publish in map(Path, args.publish):
        deps = list(publish.glob("*.deps.json"))
        if not deps:
            fail(f"no .deps.json in {publish}")
        for file in deps:
            libraries.update(json.loads(file.read_text(encoding="utf-8"))["libraries"])
        assemblies.update(f.name for f in publish.glob("*.dll"))

    entries = []
    for key in sorted(libraries, key=str.lower):
        name, version = key.split("/", 1)
        kind = libraries[key]["type"]
        if kind == "runtimepack":
            pack = name.removeprefix("runtimepack.")
            candidates = [packages / pack.lower() / version]
            if args.dotnet_root:
                candidates.append(Path(args.dotnet_root) / "packs" / pack / version)
            folder = next((c for c in candidates if c.is_dir()), None)
            if folder is None:
                fail(f"runtime pack {pack} {version} not found in {', '.join(map(str, candidates))}")
            files = notice_files(folder)
            if not any("licen" in f.name.lower() for f in files) or not any("notice" in f.name.lower() for f in files):
                fail(f"runtime pack {pack} {version} has no LICENSE and THIRD-PARTY-NOTICES file")
            (dest / pack).mkdir(exist_ok=True)
            for f in files:
                target = "LICENSE.txt" if "licen" in f.name.lower() else "THIRD-PARTY-NOTICES.txt"
                shutil.copyfile(f, dest / pack / target)
        elif kind == "package":
            folder = packages / libraries[key].get("path", f"{name.lower()}/{version.lower()}")
            if not folder.is_dir():
                fail(f"NuGet package {name} {version} not found at {folder}")
            spec = nuspec(folder)
            texts = []
            if spec["licenseType"] == "file":
                texts.append(f"--- {spec['license']} ---\n" + read(folder / spec["license"]))
            for f in notice_files(folder):
                if spec["licenseType"] == "file" and f.name == Path(spec["license"]).name:
                    continue
                texts.append(f"--- {f.name} ---\n" + read(f))
            if spec["licenseType"] == "expression" and not any("licen" in t.split("\n", 1)[0].lower() for t in texts):
                texts += expression_texts(spec["license"], spec["copyright"])
            if not texts:
                fail(f"NuGet package {name} {version} has no license file or license expression")
            header = [f"{name} {version}", f"License: {spec['license'] or spec['licenseUrl']}"]
            header += [f"{label}: {spec[field]}" for label, field in
                       (("Copyright", "copyright"), ("Authors", "authors"), ("Project", "projectUrl")) if spec[field]]
            entries.append("\n".join(header) + "\n\n" + "\n".join(texts))

    with open(dest / "THIRD-PARTY-NOTICES.txt", "w", encoding="utf-8") as out:
        out.write("Third-party components in this Martlet package\n\n"
                  "Each NuGet package keeps its own license. The .NET runtime notices are in the\n"
                  "Microsoft.*.Runtime.* folders next to this file. The character's notices are in the\n"
                  "character folder of the program (THIRD-PARTY-NOTICES.txt and live2d/LIVE2D-NOTICES.txt).\n\n")
        out.write(("\n" + "=" * 78 + "\n\n").join(entries))
    for assembly, sources in AUTHORED.items():
        if assembly in assemblies:
            for source in sources:
                path = ROOT / source
                if not path.is_file():
                    fail(f"notice file missing: {source}")
                shutil.copyfile(path, dest / source.removeprefix("src/").replace("/", "-"))
    print(f"collect-notices: {len(entries)} packages, notices in {dest}")


if __name__ == "__main__":
    main()
