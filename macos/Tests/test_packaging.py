"""Native packaging contracts exercised with disposable files only."""
from pathlib import Path
import runpy
import struct
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
                vendored = []
                def run(command, **kwargs):
                    if command == ['go', 'mod', 'vendor']:
                        vendored.append(True)
                        return
                    self.assertEqual(command[:2], ['go', 'build'])
                    self.assertTrue(vendored, 'dependency source must be vendored before building the distributed helper')
                    self.assertIn('-mod=vendor', command)
                    observed.update(kwargs['env'])
                    raise BuildObserved()
                with patch('sys.platform', 'darwin'), patch('platform.machine', return_value=machine), patch('subprocess.run', side_effect=run):
                    with self.assertRaises(BuildObserved):
                        runpy.run_path(str(script), run_name='__main__')
                self.assertEqual(observed['GOARCH'], expected)
                self.assertEqual(observed['CGO_ENABLED'], '1')
                self.assertEqual(observed['MACOSX_DEPLOYMENT_TARGET'], '13.0')
                for flag in ('CGO_CFLAGS', 'CGO_LDFLAGS'):
                    self.assertTrue(observed[flag].endswith('-mmacosx-version-min=13.0'), observed[flag])

    def test_binary_minimum_is_checked_instead_of_trusting_plist(self):
        check = runpy.run_path(str(SCRIPT.with_name('verify_macos_target.py')))['minimum_macos']
        def binary(major, platform=1):
            command = struct.pack('<IIIIII', 0x32, 24, platform, major << 16, 0, 0)
            return struct.pack('<IiiIIIII', 0xfeedfacf, 0x0100000c, 0, 2, 1, len(command), 0, 0) + command
        for major in (12, 13):
            self.assertEqual(check(binary(major)), [(major, 0, 0)])
        for data in (binary(14), binary(15), binary(13, platform=2), binary(13)[:-1], b'not-a-binary'):
            with self.subTest(data=data), self.assertRaises(ValueError):
                check(data)

if __name__ == '__main__':
    unittest.main()
