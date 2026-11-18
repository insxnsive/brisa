import hashlib
import importlib.util
from pathlib import Path
import re
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'packaging/package.py'

# SHA-256 digests of private account identifiers (32 hex characters each). The identifiers themselves
# never appear in this repository, not even split into pieces: any 32 hex character token found in a
# file is hashed and compared with these.
FORBIDDEN_ID_DIGESTS = frozenset({
    'fa8c378234cfe590a96faeefe4cc20374bb9c00a27b0f0f96404f90a9541e950',
    '8b5560044dd58e54026a737526f8c39005f725a13d5d37daddf9c4238f3744ff',
    '29dd58dbf10ccc56c719a5954bc402be619a978bc5796284bd5030b978809424',
})


def forbidden_id_tokens(data, digests=FORBIDDEN_ID_DIGESTS):
    """Hex tokens of 32 characters in `data` whose SHA-256 digest is in `digests`."""
    return [t for t in re.findall(rb'[0-9a-fA-F]{32}', data)
            if hashlib.sha256(t.lower()).hexdigest() in digests]


class PackagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        assert SCRIPT.is_file(), 'Native packaging implementation is missing'
        spec = importlib.util.spec_from_file_location('native_package', SCRIPT)
        cls.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.package)

    def test_packaging_notes_exclude_older_versions_and_keep_source_changelog(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            text = '# Changes\n\n## 0.1.0-beta.8\n\n- Startup fix.\n\n## 0.1.0-beta.7\n\n- Older fixes.\n'
            (root / 'CHANGELOG.md').write_text(text, encoding='utf-8')
            with patch.object(self.package, 'REPO', root):
                notes = self.package.prepare_release_notes(root, '0.1.0-beta.8')
            self.assertEqual(notes.read_text(encoding='utf-8'), '## 0.1.0-beta.8\n\n- Startup fix.\n')
            self.assertEqual((root / 'CHANGELOG.md').read_text(encoding='utf-8'), text)
            self.assertIn("'--releaseNotes', notes_path", SCRIPT.read_text(encoding='utf-8'))

    def test_brisa_is_published_ready_to_run(self):
        self.assertTrue("'-p:PublishReadyToRun=true'" in SCRIPT.read_text(encoding='utf-8'), 'package.py must publish Brisa ReadyToRun')

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

    def test_notification_state_is_private_and_never_shipped(self):
        self.assertIn('notifications-state.json', self.package.PRIVATE_NAMES)
        self.assertTrue(self.package.is_private(Path('Brisa/notifications-state.json')))
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ['notifications-state.json', 'main.cs']:
                (root / name).write_text('synthetic fixture')
            self.assertEqual([p.name for p in self.package.source_files(root)], ['main.cs'])
            (root / 'Brisa.exe').write_bytes(b'test native executable')
            (root / 'resources').mkdir()
            (root / 'resources' / 'notifications-state.json').write_text('{}')
            with self.assertRaisesRegex(ValueError, 'Private'):
                self.package.validate_publish(root)

    def test_public_repository_holds_no_cloudflare_ids_keys_or_vault_files(self):
        # The pieces are joined here so this file does not match its own scan.
        forbidden = [
            'api.cloud' + 'flare.com',
            'BEGIN PRIV' + 'ATE KEY',
            'BEGIN EC PRIV' + 'ATE KEY',
            '.dp' + 'api',
        ]
        repo = SCRIPT.parents[1]
        scanned = 0
        offenders = []
        for path in self.package.source_files(repo):
            data = path.read_bytes()
            scanned += 1
            for needle in forbidden:
                if needle.encode() in data:
                    offenders.append(path.relative_to(repo).as_posix() + ' contains ' + needle[:6] + '...')
            if forbidden_id_tokens(data):
                offenders.append(path.relative_to(repo).as_posix() + ' contains a private account identifier')
        self.assertGreater(scanned, 50, 'the scan must actually walk the repository')
        self.assertEqual(offenders, [])

    def test_identifier_check_flags_a_matching_token_and_ignores_everything_else(self):
        # A synthetic stand-in: the check is by digest, so the real identifiers are never needed here.
        stand_in = b'deadbeef' * 4
        digests = frozenset({hashlib.sha256(stand_in).hexdigest()})
        self.assertEqual(forbidden_id_tokens(b'id = ' + stand_in + b' # note', digests), [stand_in])
        self.assertEqual(forbidden_id_tokens(b'id = ' + stand_in.upper() + b' end', digests), [stand_in.upper()])
        self.assertEqual(forbidden_id_tokens(b'x' + stand_in + b'y', digests), [stand_in])
        self.assertEqual(forbidden_id_tokens(b'feedbeef' * 4, digests), [])
        self.assertEqual(forbidden_id_tokens(b'no tokens here, only words and 12345', digests), [])
        # The configured digests are three well formed SHA-256 values, and no file holds a matching token.
        self.assertEqual(len(FORBIDDEN_ID_DIGESTS), 3)
        self.assertTrue(all(re.fullmatch(r'[0-9a-f]{64}', d) for d in FORBIDDEN_ID_DIGESTS))


if __name__ == '__main__':
    unittest.main()
