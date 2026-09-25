"""Build Brisa's self-contained Windows installer, portable app and source archive."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

REPO = Path(__file__).resolve().parents[1]
PROJECT = REPO / 'src/Brisa/Brisa.csproj'
PRIVATE_NAMES = {'.env', 'proton-session.json', 'session.json', 'state.json', 'settings.json', 'wireguard.conf', 'native-wiresock.conf', 'imported.conf'}
GENERATED = {'bin', 'obj', 'artifacts', 'dist', 'node_modules', '__pycache__', '.git', '.tools', '.hermes', 'build', 'vendor'}
PRIVATE_SUFFIXES = {'.conf', '.key', '.pem', '.pfx', '.p12'}
SOURCE_ROOTS = ('src', 'backend', 'tools/proton-confgen', 'tests', 'packaging', 'scripts', 'docs', '.github', '.config')
SOURCE_FILES = ('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'AGENTS.md', '.gitignore', '.gitattributes')
VERSION_PATTERN = re.compile(r'^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:alpha|beta|rc)\.(0|[1-9]\d*))?$')


def is_private(path):
    return path.name.lower() in PRIVATE_NAMES or path.name.lower().startswith('.env') or path.suffix.lower() in PRIVATE_SUFFIXES


def source_files(root):
    return sorted(p for p in root.rglob('*') if p.is_file() and not p.is_symlink()
                  and not (set(p.relative_to(root).parts) & GENERATED)
                  and not is_private(p)
                  and p.suffix.lower() not in {'.exe', '.dll', '.zip', '.pdb', '.pyc', '.nupkg'})


def file_manifest(root):
    return {p.relative_to(root).as_posix(): {'bytes': p.stat().st_size,
            'sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
            for p in sorted(root.rglob('*')) if p.is_file()
            and p.suffix.lower() != '.pdb' and p.name.lower() != 'createdump.exe'}


def validate_publish(root):
    if not (root / 'Brisa.exe').is_file():
        raise ValueError('Brisa executable is missing from publish output')
    for p in root.rglob('*'):
        if p.is_file() and (is_private(p) or p.is_symlink()):
            raise ValueError('Private state must never enter a distribution: ' + p.name)


def write_checksums(root):
    manifest = {k: v for k, v in file_manifest(root).items() if k != 'SHA256SUMS'}
    (root / 'SHA256SUMS').write_text(''.join(f"{v['sha256']}  {k}\n" for k, v in manifest.items()), encoding='utf-8')


def project_version():
    version = ET.parse(PROJECT).findtext('.//Version')
    if not version or not VERSION_PATTERN.fullmatch(version):
        raise ValueError('Project Version must be a stable or numbered prerelease version')
    return version


def run(args, cwd=REPO, **kwargs):
    return subprocess.run([str(x) for x in args], cwd=cwd, check=True, **kwargs)


def tool(name):
    found = shutil.which(name)
    if not found:
        raise RuntimeError(f'{name} is required to build Brisa')
    return Path(found)


def copy(source, target):
    if not source.is_file():
        raise FileNotFoundError(source)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def collect_licenses(publish, dest, vendor):
    copy(REPO / 'LICENSE', dest / 'LICENSE')
    copy(REPO / 'THIRD-PARTY-NOTICES.md', dest / 'THIRD-PARTY-NOTICES.md')
    copy(REPO / 'tools/proton-confgen/LICENSE', dest / 'licenses/Proton-confgen-LICENSE.txt')
    node = tool('node')
    candidates = (node.parent / 'LICENSE', node.parent / 'node_modules/npm/LICENSE', node.parent / 'LICENSE.txt')
    # npm's license is not Node's license: never silently substitute it.
    node_license = next((p for p in (candidates[0], candidates[2]) if p.is_file()), None)
    if not node_license:
        raise RuntimeError('Place the Node distribution LICENSE beside node.exe before packaging')
    copy(node_license, dest / 'licenses/Node-LICENSE.txt')
    assets = json.loads((PROJECT.parent / 'obj/project.assets.json').read_text(encoding='utf-8'))
    folders = list(assets.get('packageFolders', {}))
    packages = {name: info['path'] for name, info in assets.get('libraries', {}).items() if info.get('type') == 'package'}
    runtime = json.loads((publish / 'Brisa.runtimeconfig.json').read_text(encoding='utf-8'))
    for framework in runtime.get('runtimeOptions', {}).get('includedFrameworks', []):
        package_id = framework['name'].lower() + '.runtime.win-x64'
        packages[package_id + '/' + framework['version']] = package_id + '/' + framework['version']
    inventory = []
    for name, relative in packages.items():
        package = next((Path(folder) / relative for folder in folders if (Path(folder) / relative).is_dir()), None)
        if package is None:
            raise RuntimeError('Missing package notice source: ' + name)
        notices = [p for p in package.rglob('*') if p.is_file() and p.suffix.lower() not in {'.dll', '.pdb', '.xml'}
                   and any(s in p.name.lower() for s in ('license', 'notice', 'copying'))]
        for notice in notices:
            copy(notice, dest / 'licenses/NuGet' / name / notice.relative_to(package))
        nuspec = next(package.glob('*.nuspec'), None)
        if nuspec:
            copy(nuspec, dest / 'licenses/NuGet' / name / nuspec.name)
        inventory.append({'package': name, 'notices': [str(p.relative_to(package)).replace('\\', '/') for p in notices]})
    for p in vendor.rglob('*'):
        if p.is_file() and any(word in p.name.lower() for word in ('license', 'copying', 'notice')):
            copy(p, dest / 'licenses/Go' / p.relative_to(vendor))
    (dest / 'licenses/packages.json').write_text(json.dumps(inventory, indent=2), encoding='utf-8')


def source_commit(root=REPO):
    if (root / '.git').exists():
        commit = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=root, check=True, capture_output=True, text=True).stdout.strip()
    else:
        commit = json.loads((root / 'SOURCE-INFO.json').read_text(encoding='utf-8'))['sourceCommit']
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid source commit metadata')
    return commit


def make_source_archive(archive, vendor, build_info):
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('SOURCE-INFO.json', json.dumps(build_info, indent=2))
        for top in SOURCE_ROOTS:
            root = REPO / top
            if root.is_dir():
                for p in source_files(root):
                    z.write(p, p.relative_to(REPO).as_posix())
        for name in SOURCE_FILES:
            p = REPO / name
            if p.is_file():
                z.write(p, name)
        for p in sorted(vendor.rglob('*')):
            if p.is_file():
                z.write(p, 'tools/proton-confgen/vendor/' + p.relative_to(vendor).as_posix())


def package(skip_build=False, output=None, version=None):
    actual_version = project_version()
    version = version or actual_version
    if not VERSION_PATTERN.fullmatch(version):
        raise ValueError('Invalid version')
    if version != actual_version and output is None:
        raise ValueError('Test versions require a separate --output directory')
    artifacts = Path(output).resolve() if output else REPO / 'artifacts'
    artifacts.mkdir(parents=True, exist_ok=True)
    publish = artifacts / 'publish'
    dest = artifacts / 'staging'
    release = artifacts / 'releases'
    if not skip_build:
        run([tool('node'), 'build.mjs'], cwd=REPO / 'backend')
        run([tool('go'), 'build', '-trimpath', '-ldflags=-s -w', '-o', 'build/proton-confgen.exe', './cmd/protonvpn-wg'], cwd=REPO / 'tools/proton-confgen')
        run([tool('dotnet'), 'publish', PROJECT, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
             '-p:PublishSingleFile=false', '-p:Version=' + version, '-o', publish])
    validate_publish(publish)
    if dest.exists():
        shutil.rmtree(dest)
    shutil.copytree(publish, dest)
    copy(tool('node'), dest / 'runtime/node.exe')
    copy(REPO / 'backend/dist/backend.cjs', dest / 'backend/backend.cjs')
    copy(REPO / 'tools/proton-confgen/build/proton-confgen.exe', dest / 'resources/extra/proton-confgen/proton-confgen.exe')
    # Fixture UI cannot prove production initialization. Exercise the real
    # coordinator from the .NET-filled application working directory first.
    run([sys.executable, REPO / 'packaging/production_startup_smoke.py', dest], timeout=180)
    vendor = artifacts / 'go-vendor'
    run([tool('go'), 'mod', 'vendor', '-o', vendor], cwd=REPO / 'tools/proton-confgen')
    collect_licenses(publish, dest, vendor)
    copy(REPO / 'README.md', dest / 'README.md')
    for p in (REPO / 'docs').glob('*.md'):
        copy(p, dest / 'docs' / p.name)
    commit = source_commit()
    build_info = {'product': 'Brisa', 'version': version, 'sourceCommit': commit,
                  'repository': 'https://github.com/insxnsive/brisa', 'license': 'GPL-3.0-or-later'}
    make_source_archive(dest / 'source.zip', vendor, build_info)
    (dest / 'build-info.json').write_text(json.dumps(build_info, indent=2), encoding='utf-8')
    validate_publish(dest)
    (dest / 'manifest.sha256.json').write_text(json.dumps(file_manifest(dest), indent=2), encoding='utf-8')
    if release.exists():
        shutil.rmtree(release)
    run([tool('dotnet'), 'tool', 'restore'])
    run([tool('dotnet'), 'tool', 'run', 'vpk', 'pack', '--packId', 'Brisa', '--packVersion', version,
         '--packTitle', 'Brisa', '--packAuthors', 'insxnsive; GoLiveBypass contributors', '--packDir', dest,
         '--mainExe', 'Brisa.exe', '--channel', 'win', '--runtime', 'win-x64', '--delta', 'None',
         '--icon', REPO / 'src/Brisa/Assets/Brisa.ico', '--shortcuts', 'StartMenuRoot',
         '--releaseNotes', REPO / 'CHANGELOG.md', '--outputDir', release, '--skip-updates'])
    copy(dest / 'source.zip', release / ('Brisa-' + version + '-source.zip'))
    write_checksums(release)
    result = {'version': version, 'releaseDirectory': str(release), 'sourceCommit': commit, 'files': file_manifest(release)}
    (artifacts / 'delivery.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--skip-build', action='store_true')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--version')
    args = parser.parse_args()
    package(args.skip_build, args.output, args.version)
