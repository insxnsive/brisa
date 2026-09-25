#!/usr/bin/env python3
"""Create a native, ad-hoc-signed development app and matching source archive."""
import hashlib
import os
from pathlib import Path
import platform
import plistlib
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[2]
ARCH = platform.machine()
if sys.platform != "darwin" or ARCH not in ("arm64", "x86_64"):
    raise SystemExit("Packaging requires a native arm64 or x86_64 macOS runner")

OUT = ROOT / "artifacts" / "macos" / ARCH
APP = OUT / "Brisa.app"
CONTENTS = APP / "Contents"
MACOS = CONTENTS / "MacOS"
HELPERS = CONTENTS / "Helpers"
RESOURCES = CONTENTS / "Resources"
OUT.mkdir(parents=True, exist_ok=True)
if APP.exists():
    shutil.rmtree(APP)
for directory in (MACOS, HELPERS, RESOURCES):
    directory.mkdir(parents=True)

swift_binary = ROOT / "macos" / ".build" / "release" / "Brisa"
if not swift_binary.is_file():
    raise SystemExit("Build the release Swift executable before packaging")
shutil.copy2(swift_binary, MACOS / "Brisa")
helper = HELPERS / "protonvpn-wg"
env = dict(os.environ, GOOS="darwin", GOARCH={"arm64": "arm64", "x86_64": "amd64"}[ARCH], CGO_ENABLED="1")
subprocess.run(["go", "mod", "vendor"], cwd=ROOT / "tools" / "proton-confgen", env=env, check=True)
subprocess.run(["go", "build", "-mod=vendor", "-trimpath", "-o", str(helper), "./cmd/protonvpn-wg"],
               cwd=ROOT / "tools" / "proton-confgen", env=env, check=True)
shutil.copy2(ROOT / "LICENSE", RESOURCES / "LICENSE")
with (CONTENTS / "Info.plist").open("wb") as stream:
    plistlib.dump({
        "CFBundleName": "Brisa", "CFBundleDisplayName": "Brisa",
        "CFBundleIdentifier": "dev.brisa.macos", "CFBundleExecutable": "Brisa",
        "CFBundlePackageType": "APPL", "CFBundleShortVersionString": "0.0.0",
        "CFBundleVersion": "1", "LSMinimumSystemVersion": "13.0",
        "NSPrincipalClass": "NSApplication",
        "NSHighResolutionCapable": True,
    }, stream)

for binary in (MACOS / "Brisa", helper):
    architectures = subprocess.check_output(["lipo", "-archs", str(binary)], text=True).split()
    if architectures != [ARCH]:
        raise SystemExit(f"Wrong architecture in {binary}: {architectures}")
    subprocess.run(["codesign", "--force", "--sign", "-", str(binary)], check=True)
subprocess.run(["codesign", "--force", "--sign", "-", str(APP)], check=True)
subprocess.run(["codesign", "--verify", "--deep", "--strict", str(APP)], check=True)

app_zip = OUT / f"Brisa-macOS-{ARCH}-development.zip"
subprocess.run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", str(APP), str(app_zip)], check=True)
source_zip = OUT / f"Brisa-macOS-{ARCH}-source.zip"
tracked = subprocess.check_output([
    "git", "ls-files", "-z", "--", "macos", "tools/proton-confgen",
    "LICENSE", "docs/macos.md", ".github/workflows/macos.yml",
], cwd=ROOT).split(b"\0")
source_files = [ROOT / os.fsdecode(relative) for relative in tracked if relative]
if not (ROOT / "macos/Package.swift") in source_files or not all(path.is_file() for path in source_files):
    raise SystemExit("Matching source files must be tracked and present in this checkout")
vendor = ROOT / "tools/proton-confgen/vendor"
source_files.extend(path for path in vendor.rglob("*") if path.is_file())
if not (vendor / "modules.txt").is_file():
    raise SystemExit("Matching dependency source is required")
with zipfile.ZipFile(source_zip, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(set(source_files)):
        archive.write(path, path.relative_to(ROOT).as_posix())
with (OUT / "SHA256SUMS").open("w", encoding="utf-8") as checksums:
    for path in (app_zip, source_zip):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        checksums.write(f"{digest}  {path.name}\n")
print(OUT)
