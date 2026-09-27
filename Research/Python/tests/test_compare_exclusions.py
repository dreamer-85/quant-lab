"""Contract tests for the local/cloud parity comparison.

``quantlab compare`` is how a local run is proved equal to a cloud run, so a
false mismatch is a real cost: it sends someone hunting a bug that does not
exist. The exclusions below are therefore pinned by test, because the files
involved are produced by the engine, not by this package, and nothing else would
notice the exclusion being dropped.
"""

import json
import tempfile
import unittest
from pathlib import Path

from quantlab.cloud import compare_local_cloud


def _write_run(root: Path, job_id: str, output_root: str, elapsed: float) -> Path:
    """Build a minimal but realistic run output folder."""
    job_dir = root / job_id
    (job_dir / "BTCUSDT").mkdir(parents=True)
    (job_dir / "BTCUSDT" / "crypto.csv").write_text("timestamp,close\n2024-01-01T00:00:00Z,1.0\n")

    (job_dir / "run_metadata.json").write_text(json.dumps({
        "schemaVersion": 1,
        "outputRoot": output_root,
        "stats": {"outputFiles": [f"{output_root}/{job_id}/BTCUSDT/crypto.csv"]},
        "timing": {"elapsedSeconds": elapsed},
    }, indent=2))
    (job_dir / "manifest.json").write_text(json.dumps({
        "jobId": job_id,
        "succeeded": True,
        "error": "",
        "symbolsProcessed": 1,
        "symbolsReused": 0,
        "eventsProcessed": 240,
        "observationsWritten": 61,
        "outputFiles": [f"{output_root}/{job_id}/BTCUSDT/crypto.csv"],
        "experimentName": "hypothesis",
        "metrics": {"observation_count": 61},
    }, indent=2))
    return job_dir


class CompareExclusions(unittest.TestCase):
    def test_run_metadata_is_excluded_from_byte_comparison(self):
        # Two runs of the same job differ only in their run-local audit record: different
        # output roots and different elapsed times. Comparing it byte-for-byte would report a
        # content mismatch on every single local-vs-cloud comparison.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            a = _write_run(root / "a", "job", "/tmp/a", 1.0)
            b = _write_run(root / "b", "job", "/tmp/b", 2.5)

            self.assertNotEqual(
                (a / "run_metadata.json").read_bytes(),
                (b / "run_metadata.json").read_bytes(),
                "the fixture must actually differ for this test to mean anything",
            )

            result = compare_local_cloud(a, b, compare_files=True)
            self.assertTrue(result.identical, f"unexpected mismatches: {result.mismatches}")

    def test_real_output_files_are_still_compared(self):
        # The exclusion must not be so broad that a genuine content difference passes.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            a = _write_run(root / "a", "job", "/tmp/a", 1.0)
            b = _write_run(root / "b", "job", "/tmp/b", 2.5)
            (b / "BTCUSDT" / "crypto.csv").write_text("timestamp,close\n2024-01-01T00:00:00Z,2.0\n")

            result = compare_local_cloud(a, b, compare_files=True)
            self.assertFalse(result.identical)
            self.assertTrue(any("crypto.csv" in m for m in result.mismatches), result.mismatches)

    def test_manifest_fields_are_compared_regardless_of_file_exclusion(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            a = _write_run(root / "a", "job", "/tmp/a", 1.0)
            b = _write_run(root / "b", "job", "/tmp/b", 2.5)
            manifest = json.loads((b / "manifest.json").read_text())
            manifest["eventsProcessed"] = 999
            (b / "manifest.json").write_text(json.dumps(manifest, indent=2))

            result = compare_local_cloud(a, b, compare_files=True)
            self.assertFalse(result.identical)
            self.assertTrue(any("eventsProcessed" in m for m in result.mismatches), result.mismatches)


if __name__ == "__main__":
    unittest.main()
