"""Exercise the shipped backend from its real app directory with disposable state.

Unlike --ui-test, this starts the production coordinator and its real storage
initializer. Only read-only requests and an invalid login schema are sent: no
account credentials, sign-in, tunnel control, driver installation or updates.
"""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import argparse
import json
import os
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
REQUESTS = (
    {'id': 'startup-snapshot', 'command': 'snapshot', 'payload': {}},
    {'id': 'startup-diagnostics', 'command': 'diagnostics', 'payload': {}},
    # Validation must reject this before account or native operations begin.
    {'id': 'startup-account-schema', 'command': 'login', 'payload': {}},
)


def verify_responses(stdout):
    frames = [json.loads(line) for line in stdout.splitlines() if line.strip()]
    expected = {request['id'] for request in REQUESTS}
    if len(frames) != len(expected) or {frame.get('id') for frame in frames} != expected:
        raise AssertionError('Production startup did not respond to every fixture request')
    by_id = {frame['id']: frame for frame in frames}
    snapshot = by_id['startup-snapshot']
    if snapshot.get('ok') is not True:
        raise AssertionError('Production snapshot failed')
    state = snapshot.get('result', {})
    if state.get('signedIn') is not False or state.get('connected') is not False or state.get('tunnelActive') is not False:
        raise AssertionError('Disposable startup must not contain an account or owned tunnel')
    diagnostics = by_id['startup-diagnostics']
    if diagnostics.get('ok') is not True or 'Packaged Proton helper: available.' not in diagnostics.get('result', {}).get('text', ''):
        raise AssertionError('Packaged account helper is unavailable')
    rejected = by_id['startup-account-schema']
    if rejected.get('ok') is not False or rejected.get('error') != 'Payload schema is invalid.':
        raise AssertionError('Account request did not reach the production schema validator')


def check(app_directory):
    app = Path(app_directory).resolve()
    required = ('Brisa.exe', 'System.dll', 'runtime/node.exe', 'backend/backend.cjs',
                'resources/extra/proton-confgen/proton-confgen.exe')
    if any(not (app / name).is_file() for name in required):
        raise ValueError('Expected a complete self-contained Brisa payload, not a fixture backend')
    scratch = os.environ.get('TMPDIR') or tempfile.gettempdir()
    with tempfile.TemporaryDirectory(prefix='brisa-production-startup-', dir=scratch) as temporary:
        base = Path(temporary) / 'Brisa'
        base.mkdir()
        env = dict(os.environ)
        env.pop('ELECTRON_RUN_AS_NODE', None)
        env['BRISA_DATA_DIR'] = str(base)
        env['BRISA_RESOURCE_DIR'] = str(app / 'resources')
        request_text = ''.join(json.dumps(request) + '\n' for request in REQUESTS)
        for phase in ('fresh', 'restart'):
            process = subprocess.Popen(
                [str(app / 'runtime/node.exe'), str(app / 'backend/backend.cjs')],
                cwd=app, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, text=True, encoding='utf-8',
            )
            # The real host owns the backend lifetime. Elevated inspection can
            # leave an idle worker alive after replies; EOF is not a stop API.
            with ThreadPoolExecutor(max_workers=1) as reader:
                try:
                    response = reader.submit(lambda: ''.join(
                        process.stdout.readline(1024 * 1024 + 1) for _ in REQUESTS
                    ))
                    process.stdin.write(request_text)
                    process.stdin.flush()
                    verify_responses(response.result(timeout=75))
                    if process.poll() not in (None, 0):
                        raise AssertionError(f'Production backend {phase} exited {process.returncode}')
                finally:
                    # Only the exact Node child this probe created is stopped.
                    # No tunnel or service control command is ever sent.
                    if process.poll() is None:
                        process.kill()
                    process.wait(timeout=10)
                    process.stdin.close()
                    process.stdout.close()
            if not (base / 'native-data').is_dir():
                raise AssertionError('Production private storage was not initialized')
            if any((base / 'native-data' / name).exists() for name in (
                'proton-session.json', 'native-wiresock.conf', 'wireguard.conf', 'imported.conf',
            )):
                raise AssertionError('Startup probe unexpectedly created account or tunnel state')
            print(f'PASS packaged production {phase}: private storage, snapshot, helper and account schema', flush=True)
    print('PASS disposable production startup; no sign-in or tunnel changes', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('app_directory', nargs='?', type=Path, default=ROOT / 'artifacts/staging')
    check(parser.parse_args().app_directory)
