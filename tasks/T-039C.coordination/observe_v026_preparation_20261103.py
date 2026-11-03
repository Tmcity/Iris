"""Read public GitHub state without credentials, retaining uncached responses."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
parser = argparse.ArgumentParser()
parser.add_argument("resource", choices=("run", "jobs", "releases"))
args = parser.parse_args()
base = "https://api.github.com/repos/Tmcity/Iris"
urls = {"run": base + "/actions/runs/41461057191?iris_observation=v026-preparation-20261103-1",
        "jobs": base + "/actions/runs/41461057191/jobs?per_page=100&filter=all&iris_observation=v026-preparation-20261103-1",
        "releases": base + "/releases?per_page=10&iris_observation=v026-preparation-20261103-1"}
report = ROOT / "tasks/T-039C.coordination/v026-prepublication-review-20261103-1"
report.mkdir(parents=True, exist_ok=True)
prefix = report / ("public-" + args.resource)
assert not prefix.with_suffix(".body").exists()
started = datetime.datetime.now(datetime.timezone.utc).isoformat()
command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "40", "--retry", "0",
           "-H", "Accept: application/vnd.github+json", "-H", "Cache-Control: no-cache",
           "-A", "Iris-public-release-verification", "-D", str(prefix.with_suffix(".headers")),
           "-o", str(prefix.with_suffix(".body")), "--write-out", "%{http_code}", urls[args.resource]]
result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
prefix.with_suffix(".stderr.log").write_bytes(result.stderr)
body = prefix.with_suffix(".body").read_bytes() if prefix.with_suffix(".body").exists() else b""
status = result.stdout.decode("ascii", errors="replace").strip()
record = {"Url": urls[args.resource], "StartedUtc": started,
          "FinishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "ExitCode": result.returncode,
          "HttpStatus": status, "AuthorizationSent": False, "CacheControl": "no-cache",
          "BodyBytes": len(body), "BodySha256": hashlib.sha256(body).hexdigest()}
prefix.with_suffix(".json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
if status == "200":
    data = json.loads(body)
    if args.resource == "run":
        assert data["id"] == 41461057191 and data["head_sha"] == "ee2d7118597d07b891066256349098877e65f794"
        record["Observed"] = {key: data[key] for key in ("id", "head_sha", "status", "conclusion", "run_attempt", "html_url")}
    elif args.resource == "jobs":
        record["Observed"] = [{"id": job["id"], "name": job["name"], "conclusion": job["conclusion"],
            "steps": [{"name": step["name"], "conclusion": step["conclusion"]} for step in job["steps"]]}
            for job in data["jobs"]]
    else:
        record["Observed"] = [{"id": item["id"], "tag_name": item["tag_name"], "draft": item["draft"],
            "prerelease": item["prerelease"], "assets": len(item["assets"])} for item in data]
print(json.dumps(record, indent=2))
