"""Bound native Swift tests and collect owned-process stacks on a hang."""
from pathlib import Path
import os
import platform
import re
import signal
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT.parent / 'artifacts/macos' / platform.machine() / 'test-diagnostics'
OUT.mkdir(parents=True, exist_ok=True)
subprocess.run(['swift', 'build', '--build-tests'], cwd=ROOT, check=True, timeout=150)
names = []
for file in sorted((ROOT / 'Tests/BrisaCoreTests').glob('*.swift')):
    source = file.read_text()
    suite = re.search(r'class (\w+): XCTestCase', source)
    if suite:
        names.extend(suite[1] + '/' + method for method in re.findall(r'func (test\w+)\(', source))
assert names and len(names) == len(set(names)), names

def descendants(parent):
    rows = subprocess.check_output(['ps', '-axo', 'pid=,ppid=,comm='], text=True).splitlines()
    records = [(int(p), int(pp), command) for p, pp, command in (line.strip().split(None, 2) for line in rows)]
    owned = {parent}
    for _ in range(10):
        added = {pid for pid, ppid, _ in records if ppid in owned}
        if added <= owned:
            break
        owned |= added
    return [(pid, command) for pid, _, command in records if pid in owned]

for name in [*names, 'full-suite']:
    command = ['swift', 'test', '--skip-build']
    if name != 'full-suite':
        command += ['--filter', name]
    print('RUN', name, flush=True)
    log = OUT / (name.replace('/', '-') + '.log')
    with log.open('wb') as stream:
        process = subprocess.Popen(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT,
                                   env=dict(os.environ, NSUnbufferedIO='YES'), start_new_session=True)
        try:
            result = process.wait(timeout=25)
        except subprocess.TimeoutExpired:
            owned = descendants(process.pid)
            for pid, executable in owned:
                if 'BrisaPackageTests' in executable or Path(executable).name == 'xctest':
                    subprocess.run(['sample', str(pid), '1', '1', '-file', str(OUT / (name.replace('/', '-') + '.sample.txt'))],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
            for pid, _ in reversed(owned):
                if pid > 1:
                    try:
                        os.kill(pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
            process.wait(timeout=5)
            print(log.read_text(errors='replace'), flush=True)
            raise SystemExit('Owned native test timed out: ' + name)
    text = log.read_text(errors='replace')
    print(text, flush=True)
    if result != 0:
        raise SystemExit(result)
    expected = len(names) if name == 'full-suite' else 1
    assert re.search(r'Executed ' + str(expected) + r' tests?, with 0 failures', text), 'XCTest did not execute expected cases'
print('Passed', len(names), 'individual XCTest cases and the complete suite', flush=True)
