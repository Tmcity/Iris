"""One bounded anonymous observation of the approved tag workflow or public release."""
from pathlib import Path
import datetime
import hashlib
import json
import sys
import urllib.error
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path("F:/Iris/tasks/done/T-039C.evidence/publication-v0.2.3-20261102-1")
kind = sys.argv[1]
run_id = sys.argv[2] if len(sys.argv) > 2 else None
base = "https://api.github.com/repos/Tmcity/Iris/"
urls = {"runs": base + "actions/workflows/desktop-release.yml/runs?event=push&per_page=10", "release": base + "releases/tags/v0.2.3"}
if kind in ("run", "jobs", "artifacts"):
    assert run_id and run_id.isdecimal()
    urls[kind] = base + "actions/runs/" + run_id + ("/jobs?per_page=100" if kind == "jobs" else "/artifacts?per_page=100" if kind == "artifacts" else "")
url = urls[kind]
request = urllib.request.Request(url, headers={"User-Agent": "Iris-T039C-V023-Anonymous-Observation", "Accept": "application/vnd.github+json"})
assert not request.has_header("Authorization")
started = datetime.datetime.now(datetime.timezone.utc)
try:
    response = urllib.request.urlopen(request, timeout=30)
except urllib.error.HTTPError as error:
    response = error
with response:
    body = response.read(4 * 1024 * 1024 + 1)
    assert len(body) <= 4 * 1024 * 1024
    status = response.status
    headers = {key: response.headers.get(key) for key in ("Date", "Content-Type", "ETag", "X-RateLimit-Remaining", "X-RateLimit-Reset", "Retry-After")}
target = ROOT / "observations" / (started.strftime("%Y%m%dT%H%M%S%fZ") + "-" + kind)
target.mkdir(parents=True, exist_ok=False)
(target / "response.body").write_bytes(body)
metadata = {"RequestUrl": url, "StartedUtc": started.isoformat(), "ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "Status": status, "Bytes": len(body), "Sha256": hashlib.sha256(body).hexdigest(), "AuthorizationSent": False, "Headers": headers}
(target / "observation.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n")
result = {"Observation": str(target), "Status": status, "RateLimitRemaining": headers["X-RateLimit-Remaining"]}
if status == 200:
    value = json.loads(body)
    if kind == "runs":
        runs = [run for run in value["workflow_runs"] if run["head_branch"] == "v0.2.3"]
        result["Runs"] = [{key: run.get(key) for key in ("id", "head_sha", "head_branch", "event", "status", "conclusion", "html_url", "run_attempt", "created_at", "updated_at")} for run in runs]
        for run in runs:
            assert run["head_sha"] == "fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11" and run["event"] == "push"
        if runs and not (ROOT / "run-binding.json").exists():
            assert len(runs) == 1
            (ROOT / "run-binding.json").write_text(json.dumps(result["Runs"][0], indent=2) + "\n", encoding="utf-8", newline="\n")
    elif kind == "run":
        result["Run"] = {key: value.get(key) for key in ("id", "head_sha", "head_branch", "status", "conclusion", "html_url", "run_attempt", "updated_at")}
    elif kind == "jobs":
        result["Jobs"] = [{key: job.get(key) for key in ("id", "name", "status", "conclusion", "started_at", "completed_at", "steps")} for job in value["jobs"]]
    elif kind == "release":
        result["Release"] = {key: value.get(key) for key in ("id", "tag_name", "draft", "html_url", "published_at")}
        result["Assets"] = [{key: asset.get(key) for key in ("id", "name", "size", "digest", "browser_download_url")} for asset in value["assets"]]
    else:
        result["Artifacts"] = [{key: artifact.get(key) for key in ("id", "name", "size_in_bytes", "digest", "expired", "archive_download_url")} for artifact in value["artifacts"]]
print(json.dumps(result, ensure_ascii=False, indent=2))
