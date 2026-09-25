from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


class QuickInstallTests(unittest.TestCase):
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

    def test_installer_uses_official_release_digest_and_visible_installers(self):
        script = (ROOT / 'scripts' / 'install.ps1').read_text(encoding='utf-8')
        self.assertIn('api.github.com/repos/insxnsive/brisa/releases', script)
        self.assertIn('Brisa-win-Setup.exe', script)
        self.assertIn('Get-FileHash', script)
        self.assertIn('Get-AuthenticodeSignature', script)
        self.assertIn('https://go.microsoft.com/fwlink/p/?LinkId=2124703', script)
        self.assertNotRegex(script, r"/quiet|/silent|Invoke-Expression")
        self.assertIn('Start-Process', script)

    def test_readme_explains_prerequisite_prompts_and_windows_scope(self):
        readme = (ROOT / 'README.md').read_text(encoding='utf-8')
        for phrase in ('x64', 'SHA-256', 'WebView2', 'WireSock', 'license', 'licença'):
            self.assertIn(phrase.lower(), readme.lower())


if __name__ == '__main__':
    unittest.main()
