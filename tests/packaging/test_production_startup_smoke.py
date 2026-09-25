"""The startup probe must not mistake a live service for a startup hang."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('production_startup_smoke', ROOT / 'packaging/production_startup_smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


@unittest.skipUnless(os.name == 'nt', 'Windows packaged runtime fixture')
class StartupProbeTests(unittest.TestCase):
    def test_answers_are_sufficient_for_a_persistent_owned_backend(self):
        node = os.environ.get('NATIVE_TEST_NODE') or shutil.which('node')
        self.assertIsNotNone(node)
        with tempfile.TemporaryDirectory(prefix='brisa-persistent-peer-', dir=os.environ.get('TMPDIR')) as temporary:
            app = Path(temporary)
            for name in ('Brisa.exe', 'System.dll', 'resources/extra/proton-confgen/proton-confgen.exe'):
                target = app / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b'fixture marker, never executed')
            (app / 'runtime').mkdir()
            shutil.copy2(node, app / 'runtime/node.exe')
            (app / 'backend').mkdir()
            (app / 'backend/backend.cjs').write_text(r'''
const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');
fs.mkdirSync(path.join(process.env.BRISA_DATA_DIR, 'native-data'), {recursive:true});
readline.createInterface({input: process.stdin}).on('line', line => {
  const request = JSON.parse(line);
  let response;
  if (request.command === 'snapshot') response = {ok: true, result: {signedIn: false, connected: false, tunnelActive: false}};
  else if (request.command === 'diagnostics') response = {ok: true, result: {text: 'Packaged Proton helper: available.'}};
  else response = {ok: false, result: {}, error: 'Payload schema is invalid.'};
  process.stdout.write(JSON.stringify({id: request.id, ...response}) + '\n');
});
// Like an inspection worker, an idle service can outlive stdin EOF.
setInterval(() => {}, 60000);
''', encoding='utf-8')
            original_run = subprocess.run

            def bounded_run(*args, **kwargs):
                # Keep the pre-fix communicate()/exit assumption red in seconds.
                kwargs['timeout'] = min(kwargs.get('timeout', 3), 3)
                return original_run(*args, **kwargs)

            with patch.object(smoke.subprocess, 'run', side_effect=bounded_run):
                smoke.check(app)


if __name__ == '__main__':
    unittest.main()
