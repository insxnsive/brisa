from pathlib import Path
import importlib.util
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('brisa_update_smoke', ROOT / 'packaging/update_smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class UpdateSmokeTests(unittest.TestCase):
    def test_candidate_release_path_does_not_use_previous_release(self):
        class StopBeforeAnyExternalAction(Exception):
            pass
        candidate = ROOT / 'artifacts/isolated-candidate/releases'
        with patch.object(smoke, 'run', side_effect=StopBeforeAnyExternalAction) as run:
            with self.assertRaises(StopBeforeAnyExternalAction):
                smoke.main(release_dir=candidate, evidence_path=ROOT / 'artifacts/isolated-candidate/update-smoke.json')
        self.assertEqual(Path(run.call_args.args[0][-1]), candidate)


if __name__ == '__main__':
    unittest.main()
