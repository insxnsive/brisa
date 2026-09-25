"""Exercise the real packaged window, without credentials or network access."""
from pathlib import Path
import json
import os
import signal
import subprocess
import sys

app = Path(sys.argv[1]).resolve()
base = app.parent / 'ui-evidence'
for mode in ('direct', 'launch-services'):
    evidence = base / mode
    evidence.mkdir(parents=True, exist_ok=True)
    arguments = ['--smoke-no-network', str(evidence)]
    command = [str(app / 'Contents/MacOS/Brisa'), *arguments] if mode == 'direct' else ['open', '-W', '-n', str(app), '--args', *arguments]
    process = subprocess.Popen(command, start_new_session=True)
    try:
        assert process.wait(timeout=45) == 0, f'{mode}: app startup failed'
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
    report = evidence / 'results.json'
    assert report.is_file(), f'{mode}: no report from a real rendered window'
    data = json.loads(report.read_text())
    assert data['passed'] and data['windowCount'] == 1, data
    assert data['screens'] == ['light-home', 'light-account', 'light-settings', 'dark-home', 'dark-account', 'dark-settings'], data
    assert data['navigationViaNativeControls'] and data['secretsClearedOnBack'], data
    for name in data['screens']:
        image = (evidence / (name + '.png')).read_bytes()
        assert image.startswith(b'\x89PNG\r\n\x1a\n') and len(image) > 1000, name
    print(f'{mode}: six rendered states, native button navigation, secret clearing, one window')
