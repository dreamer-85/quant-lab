"""Unified QuantLab CLI: run jobs locally or on the cloud from one tool.

Usage:
    python -m quantlab run    local|cloud  <job.json> [options]
    python -m quantlab bundle [build|upload] <bundle.tgz> [options]
    python -m quantlab results download <job-id>
    python -m quantlab compare <local-result> <cloud-result>
    python -m quantlab env [--show]

The same ``deploy/cloud/environment`` file configures both the bash deploy
scripts and this CLI.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import cloud

DEFAULT_ENV = Path(__file__).resolve().parents[3] / "deploy" / "cloud" / "environment"


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="quantlab",
        description="QuantLab research orchestration (local + cloud).",
    )
    parser.add_argument("--env-file", default=None, help="Path to deploy/cloud environment file")
    sub = parser.add_subparsers(dest="command", required=True)

    # ---- run ------------------------------------------------------------
    run_p = sub.add_parser("run", help="Run a job locally or on the VM")
    run_sub = run_p.add_subparsers(dest="where", required=True)

    run_local = run_sub.add_parser("local", help="Run a job through the local Runner DLL")
    run_local.add_argument("job", help="Job JSON file")
    run_local.add_argument("--data-dir", default="", help="Lean data root (default: env QUANTLAB_DATA_ROOT)")
    run_local.add_argument("--output-dir", default="", help="Output root (default: temp)")
    run_local.add_argument("--build", action="store_true", help="Build the Runner DLL if missing")

    run_cloud = run_sub.add_parser("cloud", help="Upload a job to the VM and run it there")
    run_cloud.add_argument("job", help="Job JSON file to copy to the VM")
    run_cloud.add_argument("--vm", default="", help="GCE instance name (default: env QUANTLAB_VM)")
    run_cloud.add_argument("--zone", default="", help="GCE zone (default: env QUANTLAB_ZONE)")
    run_cloud.add_argument("--project", default="", help="GCP project (default: env QUANTLAB_PROJECT)")
    run_cloud.add_argument("--data-dir", default="", help="Override VM data root")
    run_cloud.add_argument("--output-dir", default="", help="Override VM output root")
    run_cloud.add_argument("--repo-dir", default="", help="VM repo dir (default: env QUANTLAB_REPO_DIR)")
    run_cloud.add_argument("--no-scp", action="store_true", help="Job already present on the VM")

    # ---- bundle ----------------------------------------------------------
    bundle_p = sub.add_parser("bundle", help="Build/upload dataset bundles")
    bundle_sub = bundle_p.add_subparsers(dest="bundle_action", required=True)

    bundle_build = bundle_sub.add_parser("build", help="Pack a Lean data root into a tar.gz")
    bundle_build.add_argument("--data-root", required=True, help="Lean data root folder")
    bundle_build.add_argument("--out", required=True, help="Output .tar.gz path")
    bundle_build.add_argument("--extra-dir", action="append", default=[], help="Additional folder to bundle (repeatable)")

    bundle_upload = bundle_sub.add_parser("upload", help="Upload a bundle to GCS")
    bundle_upload.add_argument("bundle", help="Local .tar.gz path")
    bundle_upload.add_argument("--no-check", action="store_true", help="Do not fail on failure")

    # ---- results ---------------------------------------------------------
    results_p = sub.add_parser("results", help="Download job results from GCS")
    results_sub = results_p.add_subparsers(dest="results_action", required=True)
    dl = results_sub.add_parser("download", help="Download a job's output folder")
    dl.add_argument("job_id", help="Job id (the output folder name, e.g. bybit-btcusdt-20221213)")
    dl.add_argument("--dest", default=".", help="Destination directory (default: current dir)")

    # ---- compare ----------------------------------------------------------
    cmp = sub.add_parser("compare", help="Compare a local vs cloud result")
    cmp.add_argument("local", help="Local manifest.json or job output folder")
    cmp.add_argument("cloud", help="Cloud manifest.json or job output folder")
    cmp.add_argument("--no-files", action="store_true", help="Compare manifest fields only")
    cmp.add_argument("--deep", action="store_true", help="Deep parquet comparison via pandas")

    # ---- env -------------------------------------------------------------
    env_p = sub.add_parser("env", help="Show the effective environment")
    env_p.add_argument("--show", action="store_true", help="Print resolved key=value pairs")

    return parser


def _main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    env_file = args.env_file or str(DEFAULT_ENV)
    try:
        if args.command == "run":
            if args.where == "local":
                result = cloud.run_local(args.job, data_dir=args.data_dir, output_dir=args.output_dir, build=args.build)
                print(f"exit={result.exit_code}")
                print(result.stdout.strip())
                if result.stderr.strip():
                    print(f"[stderr]\n{result.stderr.strip()}", file=sys.stderr)
                print(json.dumps(result.manifest, indent=2) if result.manifest else "no manifest")
                return 0 if result.exit_code == 0 else 1
            else:
                cloud.submit_job(
                    args.job,
                    vm=args.vm,
                    zone=args.zone,
                    project=args.project,
                    data_dir=args.data_dir,
                    output_dir=args.output_dir,
                    repo_dir=args.repo_dir,
                    skip_scp=args.no_scp,
                    env_file=env_file,
                )
                return 0

        elif args.command == "bundle":
            if args.bundle_action == "build":
                out = cloud.make_bundle(args.data_root, args.out, extra_dirs=args.extra_dir)
                print(f"bundle written: {out} ({out.stat().st_size / 1024:.1f} KB)")
                return 0
            else:
                dest = cloud.upload_bundle(args.bundle, check=not args.no_check)
                print(f"uploaded: {dest}")
                return 0

        elif args.command == "results":
            if args.results_action == "download":
                out = cloud.download_results(args.job_id, args.dest)
                print(f"downloaded: {out}")
                return 0

        elif args.command == "compare":
            result = cloud.compare_local_cloud(args.local, args.cloud, compare_files=not args.no_files, deep_parquet=args.deep)
            print(cloud.describe(result))
            return 0 if result.identical else 1

        elif args.command == "env":
            env = cloud.environment(env_file)
            if args.show:
                for key in sorted(env):
                    print(f"{key}={env[key]}")
            else:
                print(f"env file: {env_file}")
                print(f"  QUANTLAB_VM         = {env.get('QUANTLAB_VM') or '(unset)'}")
                print(f"  QUANTLAB_GCS_BUCKET = {env.get('QUANTLAB_GCS_BUCKET') or '(unset)'}")
                print(f"  QUANTLAB_DATA_ROOT  = {env.get('QUANTLAB_DATA_ROOT') or '(unset)'}")
                print(f"  QUANTLAB_OUTPUT_ROOT= {env.get('QUANTLAB_OUTPUT_ROOT') or '(unset)'}")
            return 0
    except Exception as exc:  # noqa: BLE001
        print(f"quantlab: {exc}", file=sys.stderr)
        return 1

    return 2


if __name__ == "__main__":
    raise SystemExit(_main())