"""Validate Brisa release assets and publish them only after upload verification."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = 'insxnsive/brisa'
VERSION = re.compile(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:alpha|beta|rc)\.(0|[1-9]\d*))?')


def check_tag(tag, version):
    if not VERSION.fullmatch(version) or tag != 'v' + version:
        raise ValueError('Tag must exactly match the project version')
    return '-' in version


def verify_checksums(root):
    root = Path(root)
    entries = {}
    for line in (root / 'SHA256SUMS').read_text(encoding='utf-8').splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  ([^/\\:]+)', line)
        if not match:
            raise ValueError('Invalid checksum entry')
        digest, name = match.groups()
        if name in ('.', '..', 'SHA256SUMS') or name in entries or (root / name).is_symlink():
            raise ValueError('Unsafe or duplicate checksum entry')
        entries[name] = digest
    actual = {p.name for p in root.iterdir() if p.is_file() and p.name != 'SHA256SUMS'}
    if not entries or actual != set(entries):
        raise ValueError('Checksums do not cover the exact release assets')
    for name, expected in entries.items():
        if hashlib.sha256((root / name).read_bytes()).hexdigest() != expected:
            raise ValueError('Checksum mismatch: ' + name)


def verify_feed(root, version):
    assets = json.loads((root / 'releases.win.json').read_text(encoding='utf-8'))['Assets']
    name = f'Brisa-{version}-full.nupkg'
    if len(assets) != 1:
        raise ValueError('Expected exactly one full update package for this release')
    asset = assets[0]
    if (asset.get('PackageId'), asset.get('Version'), asset.get('Type'), asset.get('FileName')) != ('Brisa', version, 'Full', name):
        raise ValueError('Feed identity/version does not match this Brisa release')
    package = root / name
    if asset.get('Size') != package.stat().st_size or asset.get('SHA256', '').lower() != hashlib.sha256(package.read_bytes()).hexdigest():
        raise ValueError('Feed hash/size does not match the full update package')


def verify_release(root, version):
    root = Path(root)
    verify_checksums(root)
    verify_feed(root, version)
    source = root / f'Brisa-{version}-source.zip'
    for name in ('Brisa-win-Setup.exe', 'Brisa-win-Portable.zip', source.name):
        if not (root / name).is_file():
            raise ValueError('Missing release asset: ' + name)
    with zipfile.ZipFile(root / f'Brisa-{version}-full.nupkg') as package:
        manifest = json.loads(package.read('lib/app/manifest.sha256.json'))
        for name, expected in manifest.items():
            if 'lib/app/' + name not in package.namelist():
                raise ValueError('Package manifest names a missing file: ' + name)
            data = package.read('lib/app/' + name)
            if len(data) != expected['bytes'] or hashlib.sha256(data).hexdigest() != expected['sha256']:
                raise ValueError('Package manifest hash/size mismatch: ' + name)
        if hashlib.sha256(package.read('lib/app/source.zip')).digest() != hashlib.sha256(source.read_bytes()).digest():
            raise ValueError('Distributed source must match the archive bundled in the update')
        info = json.loads(package.read('lib/app/build-info.json'))
        if info.get('version') != version or info.get('repository') != 'https://github.com/' + REPOSITORY:
            raise ValueError('Package build information does not match this release')
    with zipfile.ZipFile(source) as archive:
        required = {'src/Brisa/Brisa.csproj', 'backend/package-lock.json', 'tools/proton-confgen/go.mod',
                    'tools/proton-confgen/vendor/modules.txt', 'packaging/package.py', 'LICENSE', 'README.md'}
        if not required.issubset(archive.namelist()):
            raise ValueError('Release source is incomplete')
    print('Verified package feed, checksums and matching source.')


def gh(*args):
    return subprocess.run(['gh', *map(str, args)], check=True, capture_output=True, text=True).stdout


def verify_uploaded(root, tag, prerelease, draft):
    endpoint = f'repos/{REPOSITORY}/releases'
    if draft:
        recent = json.loads(gh('api', endpoint + '?per_page=100'))
        matches = [item for item in recent if item['tag_name'] == tag and item['draft']]
        if len(matches) != 1:
            raise ValueError('Expected one matching draft release')
        endpoint += '/' + str(int(matches[0]['id']))
    else:
        endpoint += '/tags/' + tag
    release = json.loads(gh('api', endpoint))
    if release['tag_name'] != tag or release['prerelease'] != prerelease or release['draft'] != draft:
        raise ValueError('GitHub release state does not match the requested version/channel')
    expected = {p.name: p for p in root.iterdir() if p.is_file()}
    assets = {a['name']: a for a in release['assets']}
    if set(assets) != set(expected) or len(assets) != len(release['assets']):
        raise ValueError('GitHub release asset inventory does not match the local build')
    for name, path in expected.items():
        digest = 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest()
        if assets[name]['size'] != path.stat().st_size or assets[name].get('digest') != digest:
            raise ValueError('Uploaded asset hash/size mismatch: ' + name)
    return release['html_url']


def publish(root, tag, version):
    prerelease = check_tag(tag, version)
    verify_release(root, version)
    files = [str(p) for p in sorted(root.iterdir()) if p.is_file()]
    # Never clobber a published version. Failed validation leaves a draft to inspect.
    gh('release', 'create', tag, *files, '--repo', REPOSITORY, '--verify-tag', '--draft',
       '--prerelease=' + str(prerelease).lower(), '--latest=false', '--title', 'Brisa ' + version,
       '--notes-file', ROOT / 'CHANGELOG.md')
    verify_uploaded(root, tag, prerelease, draft=True)
    gh('release', 'edit', tag, '--repo', REPOSITORY, '--draft=false',
       '--prerelease=' + str(prerelease).lower(), '--latest=' + str(not prerelease).lower())
    print(verify_uploaded(root, tag, prerelease, draft=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['check-tag', 'verify', 'publish'])
    parser.add_argument('assets', nargs='?', type=Path, default=ROOT / 'artifacts/releases')
    args = parser.parse_args()
    version = ET.parse(ROOT / 'src/Brisa/Brisa.csproj').findtext('.//Version')
    if args.command == 'check-tag':
        print('prerelease=' + str(check_tag(os.environ['RELEASE_TAG'], version)).lower())
    elif args.command == 'verify':
        verify_release(args.assets, version)
    else:
        publish(args.assets, os.environ['RELEASE_TAG'], version)
