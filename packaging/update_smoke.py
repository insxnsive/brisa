"""Exercise real Velopack updates in a disposable portable folder, never a live install."""
from pathlib import Path
import argparse
import json
import os
import subprocess
import sys
import tempfile
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def run(args, cwd=ROOT, timeout=240):
    print('>', ' '.join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), cwd=cwd, check=True, timeout=timeout)


def info(root):
    return json.loads((root / 'current/build-info.json').read_text(encoding='utf-8'))


def main(release_dir=None, evidence_path=None):
    release = Path(release_dir) if release_dir is not None else ROOT / 'artifacts/releases'
    version = ET.parse(ROOT / 'src/Brisa/Brisa.csproj').findtext('.//Version')
    run([sys.executable, ROOT / 'packaging/release.py', 'verify', release])
    # Only help is exercised for Setup.exe: no install, registry or shortcuts.
    run([release / 'Brisa-win-Setup.exe', '--help'], timeout=15)
    temp_root = Path(os.environ.get('TMPDIR') or ROOT / 'artifacts/test-temp')
    temp_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='brisa-update-', dir=temp_root) as temp:
        scratch = Path(temp)
        older = scratch / 'older'
        portable = scratch / 'portable'
        cache = scratch / 'cache'
        screenshots = scratch / 'screenshots'
        screenshots.mkdir()
        old_version = '0.0.1'
        run([sys.executable, ROOT / 'packaging/package.py', '--version', old_version, '--output', older])
        with zipfile.ZipFile(older / 'releases/Brisa-win-Portable.zip') as archive:
            archive.extractall(portable)
        if not (portable / '.portable').is_file() or info(portable)['version'] != old_version:
            raise RuntimeError('The old portable package was not extracted correctly')
        sentinel = portable / 'keep-test-data.txt'
        sentinel.write_text('isolated test data', encoding='utf-8')
        run([portable / 'current/Brisa.exe', '--smoke-test', screenshots / 'before.png', '--theme=Dark'], timeout=25)
        if not (screenshots / 'before.png').is_file():
            raise RuntimeError('Old portable app did not render')
        run(['dotnet', 'run', '--project', ROOT / 'tests/Brisa.Update.Integration.Tests', '-c', 'Release', '--',
             release, old_version, version, cache], timeout=90)
        if info(portable)['version'] != old_version:
            raise RuntimeError('Staging must not replace the running version')
        update = cache / f'Brisa-{version}-full.nupkg'
        run([portable / 'Update.exe', 'apply', '--rootDir', portable, '--packageDir', cache,
             '--package', update, '--silent', '--norestart', '--log', scratch / 'apply.log'], timeout=90)
        if info(portable)['version'] != version or sentinel.read_text(encoding='utf-8') != 'isolated test data':
            raise RuntimeError('Update did not preserve the portable root or apply the new version')
        run([portable / 'current/Brisa.exe', '--smoke-test', screenshots / 'after.png', '--theme=Light'], timeout=25)
        if not (screenshots / 'after.png').is_file():
            raise RuntimeError('Updated portable app did not render')
        print(f'PASS actual portable update {old_version} -> {version}; both app versions rendered; root data preserved.')
        evidence = Path(evidence_path) if evidence_path is not None else ROOT / 'artifacts/update-smoke.json'
        evidence.parent.mkdir(parents=True, exist_ok=True)
        evidence.write_text(json.dumps({'from': old_version, 'to': version, 'portableApplied': True,
            'beforeAndAfterRendered': True, 'rootDataPreserved': True, 'setupHelpOnly': True,
            'liveInstallTested': False, 'liveAccountOrTunnelTested': False}, indent=2), encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-dir', type=Path)
    parser.add_argument('--evidence', type=Path)
    args = parser.parse_args()
    main(args.release_dir, args.evidence)
