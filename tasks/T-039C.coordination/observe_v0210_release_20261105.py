"""Retain anonymous, uncached GitHub observations for the approved v0.2.10 release."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
BASE = "https://api.github.com/repos/Tmcity/Iris"
SOURCE = "227b3d88938543766a27d86a94ade52f9e5b89b7"
parser = argparse.ArgumentParser()
parser.add_argument("resource", choices=("runs", "run", "jobs", "artifacts", "release"))
parser.add_argument("--run-id", type=int)
args = parser.parse_args()
stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
suffix = "iris_observation=v0210-" + stamp
if args.resource == "runs":
    url = BASE + "/actions/workflows/desktop-release.yml/runs?event=push&branch=v0.2.10&per_page=20&" + suffix
elif args.resource == "release":
    url = BASE + "/releases/tags/v0.2.10?" + suffix
else:
    assert args.run_id is not None
    extra = {"run": "", "jobs": "/jobs", "artifacts": "/artifacts"}[args.resource]
    url = BASE + "/actions/runs/" + str(args.run_id) + extra + "?per_page=100&" + suffix
folder = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1/observations" / (stamp + "-" + args.resource)
folder.mkdir(parents=True, exist_ok=False)
command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "40", "--retry", "0",
           "-H", "Accept: application/vnd.github+json", "-H", "Cache-Control: no-cache",
           "-A", "Iris-public-release-verification", "-D", str(folder / "response.headers"),
           "-o", str(folder / "response.body"), "--write-out", "%{http_code}", url]
result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
(folder / "stderr.log").write_bytes(result.stderr)
body = (folder / "response.body").read_bytes() if (folder / "response.body").exists() else b""
status = result.stdout.decode("ascii", errors="replace").strip()
metadata = {"Url": url, "ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "HttpStatus": status, "ExitCode": result.returncode, "AuthorizationSent": False,
    "BodyBytes": len(body), "BodySha256": hashlib.sha256(body).hexdigest()}
(folder / "observation.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n")
summary = {"Resource": args.resource, "HttpStatus": status, "Evidence": str(folder)}
if status == "200":
    data = json.loads(body)
    if args.resource == "runs":
        matches = [run for run in data["workflow_runs"] if run["head_sha"] == SOURCE and run["head_branch"] == "v0.2.10"]
        summary["Runs"] = [{key: run[key] for key in ("id", "head_sha", "status", "conclusion", "run_attempt", "html_url")} for run in matches]
    elif args.resource == "run":
        assert data["head_sha"] == SOURCE
        summary["Run"] = {key: data[key] for key in ("id", "head_sha", "status", "conclusion", "run_attempt", "html_url")}
    elif args.resource == "jobs":
        summary["Jobs"] = [{"id": job["id"], "name": job["name"], "status": job["status"], "conclusion": job["conclusion"],
            "steps": [{"name": step["name"], "status": step["status"], "conclusion": step["conclusion"]} for step in job["steps"]]} for job in data["jobs"]]
    elif args.resource == "artifacts":
        summary["Artifacts"] = [{key: item[key] for key in ("id", "name", "size_in_bytes", "digest", "expired")} for item in data["artifacts"]]
    else:
        summary["Release"] = {"id": data["id"], "tag_name": data["tag_name"], "draft": data["draft"], "prerelease": data["prerelease"], "html_url": data["html_url"],
            "assets": [{key: item[key] for key in ("id", "name", "size", "digest", "browser_download_url")} for item in data["assets"]]}
print(json.dumps(summary, indent=2))
