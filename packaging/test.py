"""Run Brisa's offline regression suites. Never starts the production backend."""
from pathlib import Path
import os
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
env = dict(os.environ)
node = shutil.which('node')
if not node:
    raise SystemExit('Node 22 is required')
env['NATIVE_TEST_NODE'] = str(Path(node).resolve())
# Keep all fixture state in the caller's scratch directory when provided.
if os.environ.get('TMPDIR'):
    env['TEMP'] = env['TMP'] = os.environ['TMPDIR']


def run(args, cwd=ROOT, timeout=240):
    print('>', ' '.join(map(str, args)), flush=True)
    subprocess.run([str(a) for a in args], cwd=cwd, env=env, check=True, timeout=timeout)


run([sys.executable, '-m', 'unittest', 'discover', '-s', 'tests/packaging', '-v'])
run([node, 'build.mjs', '--fixture'], ROOT/'backend')
run([node, '--test', *sorted(str(p.relative_to(ROOT/'backend')) for p in (ROOT/'backend/tests').glob('*.test.mjs'))], ROOT/'backend')
run(['go', 'test', './...'], ROOT/'tools/proton-confgen')
for name in ('Brisa.Core.Tests', 'Brisa.Navigation.Tests', 'Brisa.Appearance.Tests', 'Brisa.Lifecycle.Tests', 'Brisa.Integration.Tests'):
    project = ROOT/'tests'/name
    if not project.is_dir():
        raise SystemExit(f'Missing required suite: {name}')
    run(['dotnet', 'run', '--project', project, '-c', 'Release'])
print('All offline regression suites passed.', flush=True)
