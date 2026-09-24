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
