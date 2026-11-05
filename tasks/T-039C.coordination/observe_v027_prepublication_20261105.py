"""Retain a fresh anonymous public release inventory before preparing 0.2.7."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
REPORT = ROOT / "tasks/T-039C.coordination/v027-prepublication-review-20261105-1"
REPORT.mkdir(exist_ok=False)
BASE = "https://api.github.com/repos/Tmcity/Iris"
for name, endpoint in (("releases", "/releases?per_page=100"), ("release-v022", "/releases/tags/v0.2.2"),
                       ("release-v027", "/releases/tags/v0.2.7")):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    url = BASE + endpoint + ("&" if "?" in endpoint else "?") + "iris_observation=v027-" + stamp
    command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "40", "--retry", "0",
               "-H", "Accept: application/vnd.github+json", "-H", "Cache-Control: no-cache",
               "-A", "Iris-public-release-verification", "-D", str(REPORT / (name + ".headers")),
               "-o", str(REPORT / (name + ".body")), "--write-out", "%{http_code}", url]
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    (REPORT / (name + ".stderr.log")).write_bytes(result.stderr)
    body_path = REPORT / (name + ".body")
    body = body_path.read_bytes() if body_path.exists() else b""
    status = result.stdout.decode("ascii", errors="replace").strip()
    record = {"Url": url, "ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "HttpStatus": status, "ExitCode": result.returncode, "AuthorizationSent": False,
              "BodyBytes": len(body), "BodySha256": hashlib.sha256(body).hexdigest()}
    (REPORT / (name + ".json")).write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"Resource": name, "HttpStatus": status, "Evidence": str(REPORT)}), flush=True)
    if result.returncode != 0 or status != ("404" if name == "release-v027" else "200"):
        raise RuntimeError("Public observation incomplete: " + name)
