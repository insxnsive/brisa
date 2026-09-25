from pathlib import Path
import json
import hashlib
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def fixture_environment(folder):
    # A pwsh parent can pass its incompatible module path through Python to
    # Windows PowerShell. Let the selected shell rebuild its own built-in paths.
    env = {key: value for key, value in os.environ.items() if key.upper() != 'PSMODULEPATH'}
    env.update(TEMP=str(folder), TMP=str(folder), TMPDIR=str(folder))
    return env


class QuickInstallTests(unittest.TestCase):
    def test_fixture_environment_does_not_inherit_foreign_module_paths(self):
        with patch.dict(os.environ, {'PSModulePath': 'foreign-powershell-modules'}):
            env = fixture_environment('isolated-scratch')
        self.assertFalse(any(key.upper() == 'PSMODULEPATH' for key in env))
        for key in ('TEMP', 'TMP', 'TMPDIR'):
            self.assertEqual(env[key], 'isolated-scratch')
        self.assertEqual(env.get('PATH'), os.environ.get('PATH'))

    def test_readme_is_bilingual_with_clickable_section_indexes(self):
        readme = (ROOT / 'README.md').read_text(encoding='utf-8')
        self.assertIn('## Português (Brasil)', readme)
        self.assertIn('## English', readme)
        self.assertRegex(readme, r'\[[^]]+\]\(#instalar\)')
        self.assertRegex(readme, r'\[[^]]+\]\(#quick-install\)')
        self.assertIn('scripts/install.ps1', readme)
        headings = re.findall(r'^#{1,6} (.+)$', readme, re.MULTILINE)
        slugs = {re.sub(r'[^\w\- ]', '', heading.lower()).strip().replace(' ', '-') for heading in headings}
        toc_links = re.findall(r'^- \[[^]]+\]\(#([^)]*)\)$', readme, re.MULTILINE)
        self.assertTrue(toc_links)
        for anchor in toc_links:
            self.assertIn(anchor, slugs, f'broken README section link: #{anchor}')

    def test_readme_bootstrap_verifies_an_immutable_script_before_execution(self):
        readme = (ROOT / 'README.md').read_text(encoding='utf-8')
        blocks = re.findall(r'```powershell\n(.*?)\n```', readme, re.DOTALL)
        self.assertEqual(len(blocks), 2)
        self.assertEqual(blocks[0], blocks[1])
        snippet = blocks[0]
        self.assertRegex(snippet, r'https://raw[.]githubusercontent[.]com/insxnsive/brisa/[0-9a-f]{40}/scripts/install[.]ps1')
        self.assertIn(hashlib.sha256((ROOT / 'scripts/install.ps1').read_bytes()).hexdigest(), snippet)
        self.assertLess(snippet.index('Get-FileHash'), snippet.index('& powershell.exe'))
        self.assertNotRegex(snippet, r'(?i)\biex\b|Invoke-Expression')
        powershell = shutil.which('powershell') or shutil.which('pwsh')
        if not powershell:
            self.skipTest('PowerShell is needed for bootstrap behavior')
        scratch = Path(os.environ.get('TMPDIR') or ROOT / 'artifacts/test-temp')
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='brisa-bootstrap-', dir=scratch) as folder:
            bootstrap = Path(folder) / 'bootstrap.ps1'
            bootstrap.write_text(snippet, encoding='utf-8')
            result = Path(folder) / 'result.json'
            for scenario in ('matching', 'tampered', 'download-failed'):
                with self.subTest(scenario=scenario):
                    process = subprocess.run([powershell, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                        '-File', str(ROOT / 'tests/packaging/bootstrap_fixture.ps1'), '-Bootstrap', str(bootstrap),
                        '-Source', str(ROOT / 'scripts/install.ps1'), '-Scenario', scenario, '-Evidence', str(result)],
                        capture_output=True, text=True, timeout=15,
                        env=fixture_environment(folder))
                    self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
                    data = json.loads(result.read_text(encoding='utf-8-sig'))
                    self.assertEqual(data['invoked'], scenario == 'matching', data)
                    self.assertEqual(data['error'] is None, scenario == 'matching', data)
                    self.assertEqual(data['leftovers'], 0, data)

    def test_powershell7_direct_handoff_preserves_real_hash_validation(self):
        pwsh = shutil.which('pwsh')
        if os.name != 'nt' or not pwsh:
            self.skipTest('The direct handoff needs PowerShell 7 on Windows')
        scratch = Path(os.environ.get('TMPDIR') or ROOT / 'artifacts/test-temp')
        scratch.mkdir(parents=True, exist_ok=True)
        quote = lambda value: "'" + str(value).replace("'", "''") + "'"
        for scenario in ('present', 'hash-mismatch'):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory(prefix='brisa-pwsh-direct-', dir=scratch) as folder:
                result = Path(folder) / 'result.json'
                command = ("& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "
                    + quote(ROOT / 'tests/packaging/quick_install_fixture.ps1')
                    + ' -ScriptPath ' + quote(ROOT / 'scripts/install.ps1')
                    + ' -Scenario ' + quote(scenario) + ' -EvidencePath ' + quote(result) + '; exit $LASTEXITCODE')
                # Deliberately do not sanitize PSModulePath: pwsh owns this direct handoff.
                process = subprocess.run([pwsh, '-NoProfile', '-NonInteractive', '-Command', command],
                    env={**os.environ, 'TEMP': folder, 'TMP': folder, 'TMPDIR': folder},
                    capture_output=True, text=True, timeout=25)
                self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
                data = json.loads(result.read_text(encoding='utf-8-sig'))
                self.assertEqual(data['leftovers'], 0, data)
                if scenario == 'present':
                    self.assertIsNone(data['error'], data)
                    self.assertEqual(data['launches'], ['Brisa-win-Setup.exe'])
                else:
                    self.assertIn('SHA-256 does not match', data['error'])
                    self.assertEqual(data['launches'], [])

    def test_installer_uses_official_release_digest_and_visible_installers(self):
        script = (ROOT / 'scripts' / 'install.ps1').read_text(encoding='utf-8')
        self.assertIn('api.github.com/repos/insxnsive/brisa/releases', script)
        self.assertIn('Brisa-win-Setup.exe', script)
        self.assertIn('Get-FileHash', script)
        self.assertIn('Get-AuthenticodeSignature', script)
        self.assertIn('https://go.microsoft.com/fwlink/p/?LinkId=2124703', script)
        self.assertNotRegex(script, r"/quiet|/silent|Invoke-Expression")
        self.assertIn('Start-Process', script)

    def run_installer_fixture(self, scenario):
        powershell = shutil.which('powershell') or shutil.which('pwsh')
        if not powershell:
            self.skipTest('PowerShell is needed for the real-script fixture')
        scratch = Path(os.environ.get('TMPDIR') or ROOT / 'artifacts/test-temp')
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='quick-install-fixture-', dir=scratch) as folder:
            result = Path(folder) / 'result.json'
            env = fixture_environment(folder)
            process = subprocess.run([powershell, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                '-File', str(ROOT / 'tests/packaging/quick_install_fixture.ps1'),
                '-ScriptPath', str(ROOT / 'scripts/install.ps1'), '-Scenario', scenario,
                '-EvidencePath', str(result)], env=env, capture_output=True, text=True, timeout=25)
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            data = json.loads(result.read_text(encoding='utf-8-sig'))
            self.assertEqual(data['leftovers'], 0, data)
            return data, process.stdout

    def test_installer_rejects_asset_from_a_different_release(self):
        result, _ = self.run_installer_fixture('wrong-tag-url')
        self.assertIsNotNone(result['error'], result)
        self.assertEqual(result['launches'], [])

    def test_present_runtime_launches_only_brisa(self):
        result, _ = self.run_installer_fixture('present')
        self.assertIsNone(result['error'], result)
        self.assertEqual(result['launches'], ['Brisa-win-Setup.exe'])

    def test_absent_runtime_verifies_and_runs_visible_prerequisite(self):
        result, _ = self.run_installer_fixture('webview-absent')
        self.assertIsNone(result['error'], result)
        self.assertEqual(result['launches'], ['MicrosoftEdgeWebView2Setup.exe', 'Brisa-win-Setup.exe'])

    def test_integrity_and_download_failures_never_launch_setup(self):
        for scenario in ('foreign-url', 'query-url', 'hash-mismatch', 'missing-digest', 'download-failed', 'webview-bad-signature'):
            with self.subTest(scenario=scenario):
                result, _ = self.run_installer_fixture(scenario)
                self.assertIsNotNone(result['error'], result)
                self.assertEqual(result['launches'], [])

    def test_missing_runtime_after_install_does_not_continue(self):
        result, _ = self.run_installer_fixture('webview-still-missing')
        self.assertIn('still missing', result['error'])
        self.assertEqual(result['launches'], ['MicrosoftEdgeWebView2Setup.exe'])

    def test_cancellation_is_actionable_and_a_fresh_retry_works(self):
        for scenario in ('setup-cancel', 'webview-cancel'):
            with self.subTest(scenario=scenario):
                result, _ = self.run_installer_fixture(scenario)
                self.assertIn('cancelled', result['error'].lower())
                self.assertIn('again', result['error'].lower())
        result, _ = self.run_installer_fixture('webview-absent')
        self.assertIsNone(result['error'], result)

    def test_reboot_required_does_not_claim_ready_to_connect(self):
        result, output = self.run_installer_fixture('reboot-required')
        self.assertIsNone(result['error'], result)
        self.assertIn('Restart Windows', output)

    def test_duplicate_installers_are_rejected(self):
        result, _ = self.run_installer_fixture('duplicate-asset')
        self.assertIsNotNone(result['error'], result)
        self.assertEqual(result['launches'], [])

    def test_readme_explains_prerequisite_prompts_and_windows_scope(self):
        readme = (ROOT / 'README.md').read_text(encoding='utf-8')
        for phrase in ('x64', 'SHA-256', 'WebView2', 'WireSock', 'license', 'licença'):
            self.assertIn(phrase.lower(), readme.lower())


if __name__ == '__main__':
    unittest.main()
