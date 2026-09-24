import importlib.util
from pathlib import Path
import tempfile
import unittest
import hashlib

SCRIPT = Path(__file__).resolve().parents[2] / 'packaging/release.py'

class ReleaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        assert SCRIPT.is_file(), 'Release validation implementation is missing'
        spec = importlib.util.spec_from_file_location('brisa_release', SCRIPT)
        cls.release = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.release)

    def test_tag_is_exact_and_cannot_promote_a_prerelease(self):
        self.assertTrue(self.release.check_tag('v0.1.0-beta.1', '0.1.0-beta.1'))
        self.assertFalse(self.release.check_tag('v1.0.0', '1.0.0'))
        for tag in ('v0.1.0', 'vv0.1.0-beta.1', 'v0.1.0-beta.1 ', 'v0.1.0-beta.01'):
            with self.assertRaises(ValueError):
                self.release.check_tag(tag, '0.1.0-beta.1')

    def test_checksums_cover_exact_files_and_detect_tampering(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root/'asset.zip').write_bytes(b'fixture')
            digest = hashlib.sha256(b'fixture').hexdigest()
            (root/'SHA256SUMS').write_text(digest + '  asset.zip\n')
            self.release.verify_checksums(root)
            (root/'extra.exe').write_bytes(b'unlisted')
            with self.assertRaises(ValueError): self.release.verify_checksums(root)
            (root/'extra.exe').unlink()
            (root/'asset.zip').write_bytes(b'tampered')
            with self.assertRaises(ValueError): self.release.verify_checksums(root)

    def test_feed_matches_package_identity_version_size_and_hash(self):
        import json
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            name = 'Brisa-0.1.0-beta.1-full.nupkg'
            (root/name).write_bytes(b'fixture')
            asset = {'PackageId': 'Brisa', 'Version': '0.1.0-beta.1', 'Type': 'Full', 'FileName': name,
                     'Size': 7, 'SHA256': hashlib.sha256(b'fixture').hexdigest().upper()}
            def save(): (root/'releases.win.json').write_text(json.dumps({'Assets': [asset]}))
            save()
            self.release.verify_feed(root, '0.1.0-beta.1')
            for key, value in [('PackageId', 'Other'), ('Version', '0.1.0'), ('FileName', '../escape'), ('Size', 0), ('SHA256', '0'*64)]:
                old = asset[key]; asset[key] = value; save()
                with self.assertRaises(ValueError): self.release.verify_feed(root, '0.1.0-beta.1')
                asset[key] = old

    def test_release_rejects_manifest_entries_missing_from_package(self):
        import io
        import json
        import zipfile
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            version = '0.1.0-beta.1'
            source_data = io.BytesIO()
            with zipfile.ZipFile(source_data, 'w') as source:
                for name in ('src/Brisa/Brisa.csproj', 'backend/package-lock.json',
                             'tools/proton-confgen/go.mod', 'tools/proton-confgen/vendor/modules.txt',
                             'packaging/package.py', 'LICENSE', 'README.md'):
                    source.writestr(name, 'synthetic test source')
            (root/f'Brisa-{version}-source.zip').write_bytes(source_data.getvalue())
            for name in ('Brisa-win-Setup.exe', 'Brisa-win-Portable.zip'):
                (root/name).write_bytes(b'synthetic test asset')
            package = root/f'Brisa-{version}-full.nupkg'
            with zipfile.ZipFile(package, 'w') as archive:
                archive.writestr('lib/app/source.zip', source_data.getvalue())
                archive.writestr('lib/app/build-info.json', json.dumps({'version': version, 'repository': 'https://github.com/insxnsive/brisa'}))
                archive.writestr('lib/app/manifest.sha256.json', json.dumps({'Brisa.pdb': {'bytes': 1, 'sha256': '0'*64}}))
            data = package.read_bytes()
            (root/'releases.win.json').write_text(json.dumps({'Assets': [{'PackageId': 'Brisa', 'Version': version,
                'Type': 'Full', 'FileName': package.name, 'Size': len(data), 'SHA256': hashlib.sha256(data).hexdigest()}]}))
            (root/'SHA256SUMS').write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in sorted(root.iterdir())))
            with self.assertRaisesRegex(ValueError, 'manifest'):
                self.release.verify_release(root, version)

    def test_checksums_reject_traversal_and_duplicate_entries(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            digest = '0' * 64
            for name in ('../escape', 'C:/outside', '/outside', '..\\outside'):
                (root/'SHA256SUMS').write_text(digest + '  ' + name + '\n')
                with self.assertRaises(ValueError): self.release.verify_checksums(root)
            (root/'asset.zip').write_bytes(b'fixture')
            digest = hashlib.sha256(b'fixture').hexdigest()
            (root/'SHA256SUMS').write_text((digest + '  asset.zip\n') * 2)
            with self.assertRaises(ValueError): self.release.verify_checksums(root)
