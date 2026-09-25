import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'packaging/package.py'


class PackagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        assert SCRIPT.is_file(), 'Native packaging implementation is missing'
        spec = importlib.util.spec_from_file_location('native_package', SCRIPT)
        cls.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.package)

    def test_rejects_publish_output_without_native_executable(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(ValueError, 'executable'):
                self.package.validate_publish(Path(folder))

    def test_rejects_any_shipped_session_or_wireguard_secret(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'Brisa.exe').write_bytes(b'test native executable')
            (root / 'resources').mkdir()
            (root / 'resources' / 'proton-session.json').write_text('{}')
            with self.assertRaisesRegex(ValueError, 'Private'):
                self.package.validate_publish(root)

    def test_source_archive_excludes_generated_and_private_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ['MainWindow.xaml', 'backend/main.ts', 'bin/app.exe', 'obj/project.assets.json', 'artifacts/app.zip', '.env', 'proton-session.json', 'wireguard.conf', 'backend/node_modules/module.js']:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('synthetic fixture')
            got = {p.relative_to(root).as_posix() for p in self.package.source_files(root)}
            self.assertEqual(got, {'MainWindow.xaml', 'backend/main.ts'})

    def test_checksums_are_repeatable_and_never_hash_themselves(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'Brisa.exe').write_bytes(b'abc')
            self.package.write_checksums(root)
            first = (root / 'SHA256SUMS').read_text()
            self.package.write_checksums(root)
            self.assertEqual((root / 'SHA256SUMS').read_text(), first)
            self.assertNotIn('SHA256SUMS', first)
            self.assertEqual(len(first.splitlines()), 1)

    def test_source_archive_excludes_private_keys_and_build_tool_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ['.env.local', 'secret.pem', 'identity.pfx', 'state.json', '.tools/vpk.exe', 'build/generated.go', '.hermes/private.md', 'main.cs', 'package-lock.json']:
                p = root / name
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text('fixture')
            self.assertEqual({p.relative_to(root).as_posix() for p in self.package.source_files(root)}, {'main.cs', 'package-lock.json'})

    def test_test_projects_do_not_depend_on_local_package_feeds(self):
        root = SCRIPT.parents[1]
        projects = list((root/'tests').glob('*/*.csproj'))
        self.assertTrue(projects)
        for project in projects:
            self.assertNotIn('<RestoreSources>', project.read_text(), str(project))

    def test_source_walk_does_not_rebundle_existing_go_vendor_tree(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root/'vendor/example').mkdir(parents=True)
            (root/'vendor/example/source.go').write_text('package example')
            (root/'main.go').write_text('package main')
            self.assertEqual([p.relative_to(root).as_posix() for p in self.package.source_files(root)], ['main.go'])

    def test_source_archive_can_recover_build_commit_without_git(self):
        import json
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            commit = 'a' * 40
            (root/'SOURCE-INFO.json').write_text(json.dumps({'sourceCommit': commit}))
            self.assertEqual(self.package.source_commit(root), commit)
            (root/'SOURCE-INFO.json').write_text(json.dumps({'sourceCommit': 'not a commit'}))
            with self.assertRaises(ValueError): self.package.source_commit(root)

    def test_manifest_excludes_debug_files_stripped_by_velopack(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ('Brisa.exe', 'Brisa.pdb', 'createdump.exe'):
                (root/name).write_bytes(b'synthetic fixture')
            self.assertEqual(set(self.package.file_manifest(root)), {'Brisa.exe'})

    def test_source_archive_includes_quick_install_script(self):
        import zipfile
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'scripts').mkdir()
            (root / 'scripts/install.ps1').write_text('# isolated installer fixture')
            (root / 'vendor').mkdir()
            archive = root / 'source.zip'
            with patch.object(self.package, 'REPO', root):
                self.package.make_source_archive(archive, root / 'vendor', {'version': '0.0.1'})
            with zipfile.ZipFile(archive) as source:
                self.assertIn('scripts/install.ps1', source.namelist())

    def test_manifest_hashes_actual_bytes_and_portable_paths(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'runtime').mkdir()
            (root / 'runtime/node.exe').write_bytes(b'abc')
            manifest = self.package.file_manifest(root)
            self.assertEqual(manifest['runtime/node.exe']['sha256'], 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
            self.assertEqual(manifest['runtime/node.exe']['bytes'], 3)


if __name__ == '__main__':
    unittest.main()
