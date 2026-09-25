"""Strict evidence contracts for the disposable, packaged transport check."""
import hashlib
import json
import subprocess
import sys
import tempfile
from unittest.mock import patch
from pathlib import Path
import runpy
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/transport_acceptance.py'
PASS = {'schemaVersion': 2, 'scope': 'loopback-only', 'tcp4': True,
        'udp4': True, 'dnsA': True, 'tcp6': True, 'udp6': True,
        'dnsAAAA': True, 'familyGate': True, 'shutdown': True}


class ReportTests(unittest.TestCase):
    def test_accepts_complete_loopback_report(self):
        module = runpy.run_path(str(SCRIPT)) if SCRIPT.is_file() else {}
        self.assertIn('validate_report', module, 'transport evidence validator is missing')
        self.assertEqual(module['validate_report'](json.dumps(PASS).encode()), PASS)

    def test_rejects_incomplete_ambiguous_or_non_loopback_evidence(self):
        validate = runpy.run_path(str(SCRIPT))['validate_report']
        invalid = [dict(PASS, tcp4=False), dict(PASS, udp4=1), dict(PASS, dnsA=None),
                   dict(PASS, shutdown="true"), dict(PASS, schemaVersion=True),
                   dict(PASS, scope="live"), dict(PASS, privateKey="fixture-secret"),
                   {key: value for key, value in PASS.items() if key != 'dnsAAAA'},
                   [], None]
        invalid += [{'schemaVersion': 1, 'scope': 'loopback-only', 'tcp': True,
                     'udp': True, 'dns': True, 'shutdown': True},
                    {key: value for key, value in PASS.items() if key != 'tcp6'},
                    dict(PASS, tcp6=False), dict(PASS, dnsAAAA=1)]
        raw_invalid = [json.dumps(value).encode() for value in invalid]
        raw_invalid += [b'{"schemaVersion":0,' + json.dumps(PASS).encode()[1:],
                        json.dumps(PASS).encode() + b' {}', b'bad json', b'\xff',
                        b' ' * 4096 + json.dumps(PASS).encode()]
        for raw in raw_invalid:
            with self.subTest(raw=raw[:120]), self.assertRaises(ValueError):
                validate(raw)

    def test_executes_exact_binary_and_records_its_hash(self):
        module = runpy.run_path(str(SCRIPT))
        self.assertIn('accept', module, 'packaged transport runner is missing')
        with tempfile.TemporaryDirectory() as temporary:
            binary = Path(temporary) / 'brisa-tunnel-check'
            output = Path(temporary) / 'evidence/result.json'
            binary.write_bytes(b'fixture-binary')
            completed = subprocess.CompletedProcess([], 0, json.dumps(PASS).encode(), b'')
            with patch('subprocess.run', return_value=completed) as run:
                result = module['accept'](binary, output)
            run.assert_called_once_with([str(binary.resolve()), '--self-test'],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, timeout=35, check=False)
            self.assertEqual(result['status'], 'passed')
            self.assertEqual(result['scope'], 'loopback-only')
            self.assertEqual(result['checks'], PASS)
            self.assertEqual(result['binarySha256'], hashlib.sha256(binary.read_bytes()).hexdigest())
            self.assertEqual(json.loads(output.read_text()), result)

    def test_failure_evidence_replaces_stale_pass_without_leaking_output(self):
        accept = runpy.run_path(str(SCRIPT))['accept']
        cases = [
            ('self-test-failed', subprocess.CompletedProcess([], 1, json.dumps(PASS).encode(), b'fixture-secret')),
            ('timeout', subprocess.TimeoutExpired('fixture', 35, output=b'fixture-secret')),
            ('launch-failed', OSError('fixture-secret')),
            ('invalid-report', subprocess.CompletedProcess([], 0, b'fixture-secret', b'')),
            ('unexpected-stderr', subprocess.CompletedProcess([], 0, json.dumps(PASS).encode(), b'fixture-secret')),
        ]
        for reason, completed in cases:
            with self.subTest(reason=reason), tempfile.TemporaryDirectory() as temporary:
                binary = Path(temporary) / 'brisa-tunnel-check'
                output = Path(temporary) / 'result.json'
                binary.write_bytes(b'fixture-binary')
                output.write_text('{"status":"passed"}')
                options = {'side_effect': completed} if isinstance(completed, Exception) else {'return_value': completed}
                with patch('subprocess.run', **options):
                    result = accept(binary, output)
                self.assertEqual(result['status'], 'failed')
                self.assertEqual(result['reason'], reason)
                self.assertEqual(json.loads(output.read_text()), result)
                self.assertNotIn('fixture-secret', output.read_text())
                self.assertNotIn('checks', result)

    def test_missing_or_changed_binary_cannot_pass(self):
        accept = runpy.run_path(str(SCRIPT))['accept']
        with tempfile.TemporaryDirectory() as temporary:
            binary = Path(temporary) / 'brisa-tunnel-check'
            output = Path(temporary) / 'result.json'
            with patch('subprocess.run') as run:
                result = accept(binary, output)
            run.assert_not_called()
            self.assertEqual(result['status'], 'failed')
            self.assertEqual(result['reason'], 'unavailable-binary')
            binary.write_bytes(b'fixture-before')
            def changed(*args, **kwargs):
                binary.write_bytes(b'fixture-after')
                return subprocess.CompletedProcess([], 0, json.dumps(PASS).encode(), b'')
            with patch('subprocess.run', side_effect=changed):
                result = accept(binary, output)
            self.assertEqual(result['status'], 'failed')
            self.assertEqual(result['reason'], 'binary-changed')
            self.assertNotIn('checks', result)

    def test_cli_failure_exits_nonzero_and_writes_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            binary = Path(temporary) / 'missing'
            output = Path(temporary) / 'result.json'
            completed = subprocess.run([sys.executable, str(SCRIPT), '--binary', str(binary),
                                        '--output', str(output)], capture_output=True, timeout=10)
            self.assertEqual(completed.returncode, 1)
            self.assertEqual(json.loads(output.read_text())['reason'], 'unavailable-binary')
            self.assertIn(b'failed', completed.stdout)


if __name__ == '__main__':
    unittest.main()
