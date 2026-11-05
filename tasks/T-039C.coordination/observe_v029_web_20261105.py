"""Retain official public GitHub pages while the shared REST quota is exhausted."""
from pathlib import Path
import argparse
import datetime
import hashlib
import html
import json
import re
import subprocess

ROOT = Path("F:/Iris")
parser = argparse.ArgumentParser()
parser.add_argument("resource", choices=("actions", "run", "job", "release", "assets"))
parser.add_argument("--run-id", type=int)
parser.add_argument("--job-id", type=int)
args = parser.parse_args()
base = "https://github.com/Tmcity/Iris"
if args.resource == "actions":
    url = base + "/actions/workflows/desktop-release.yml?query=branch%3Av0.2.9"
elif args.resource == "run":
    assert args.run_id
    url = base + "/actions/runs/" + str(args.run_id)
elif args.resource == "job":
    assert args.run_id and args.job_id
    url = base + "/actions/runs/" + str(args.run_id) + "/job/" + str(args.job_id)
elif args.resource == "assets":
    url = base + "/releases/expanded_assets/v0.2.9"
else:
    url = base + "/releases/tag/v0.2.9"
stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
url += ("&" if "?" in url else "?") + "iris_observation=v029-" + stamp
folder = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1/observations" / (stamp + "-web-" + args.resource)
folder.mkdir(parents=True, exist_ok=False)
command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "40", "--retry", "0",
    "-H", "Cache-Control: no-cache", "-A", "Iris-public-release-verification", "-D", str(folder / "response.headers"),
    "-o", str(folder / "response.body"), "--write-out", "%{http_code}", url]
result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
(folder / "stderr.log").write_bytes(result.stderr)
body = (folder / "response.body").read_bytes() if (folder / "response.body").exists() else b""
status = result.stdout.decode("ascii", errors="replace").strip()
record = {"Url": url, "ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
          "HttpStatus": status, "ExitCode": result.returncode, "AuthorizationSent": False,
          "UsesGitHubRestApi": False, "BodyBytes": len(body), "BodySha256": hashlib.sha256(body).hexdigest()}
(folder / "observation.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
page = body.decode("utf-8", errors="replace")
step_records = []
for tag in re.findall(r"<check-step\b[^>]*>", page, flags=re.S):
    attributes = {name: html.unescape(value) for name, value in re.findall(r'([\w-]+)="([^"]*)"', tag)}
    step_records.append({"Name": attributes.get("data-name"), "Conclusion": attributes.get("data-conclusion"),
                         "StartedAt": attributes.get("data-started-at"), "CompletedAt": attributes.get("data-completed-at")})
status_match = re.search(r">Status</span>\s*<span[^>]*>([^<]+)</span>", page)
summary = {"Resource": args.resource, "HttpStatus": status, "Evidence": str(folder),
           "DisplayedStatus": html.unescape(status_match[1]).strip() if status_match else None,
           "SourceCommits": sorted(set(re.findall(r"/Tmcity/Iris/commit/([a-f0-9]{40})", page))),
           "Steps": step_records,
           "RunIds": sorted(set(re.findall(r"/Tmcity/Iris/actions/runs/(\d+)", page))),
           "JobIds": sorted(set(re.findall(r"/job/(\d+)", page))),
           "PublicDownloadNames": sorted(set(html.unescape(value) for value in re.findall(
               r"/Tmcity/Iris/releases/download/v0\.2\.9/([^\"<>?]+)", page)))}
print(json.dumps(summary, indent=2))
