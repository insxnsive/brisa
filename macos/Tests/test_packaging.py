"""Native packaging contracts exercised with disposable files only."""
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts' / 'package.py'

class BuildObserved(Exception):
    pass

class PackagingTests(unittest.TestCase):
    def test_native_architectures_select_go_architecture(self):
        for machine, expected in [('arm64', 'arm64'), ('x86_64', 'amd64')]:
            with self.subTest(machine=machine), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                script = root / 'macos/scripts/package.py'
                script.parent.mkdir(parents=True)
                script.write_text(SCRIPT.read_text(encoding='utf-8'), encoding='utf-8')
                executable = root / 'macos/.build/release/Brisa'
                executable.parent.mkdir(parents=True)
                executable.write_bytes(b'fixture-not-executable')
                observed = {}
                def run(command, **kwargs):
                    self.assertEqual(command[:2], ['go', 'build'])
                    observed.update(kwargs['env'])
                    raise BuildObserved()
                with patch('sys.platform', 'darwin'), patch('platform.machine', return_value=machine), patch('subprocess.run', side_effect=run):
                    with self.assertRaises(BuildObserved):
                        runpy.run_path(str(script), run_name='__main__')
                self.assertEqual(observed['GOARCH'], expected)
                self.assertEqual(observed['CGO_ENABLED'], '1')

if __name__ == '__main__':
    unittest.main()
