"""Preserve the single 504 response; reuse verified bytes and retry only RELEASES in a new directory."""
from pathlib import Path
import datetime
import hashlib
import importlib.util
import json
import subprocess

ROOT = Path("F:/Iris")
BEFORE = ROOT / "artifacts/t039c/public-0.2.10-download-20261105-1"
AFTER = ROOT / "artifacts/t039c/public-0.2.10-download-20261105-2"
PUB = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1"
LOGS = PUB / "public-download-20261105-2"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


original_failure = read(PUB / "public-download-20261105-1/RELEASES/download.json")
assert original_failure["HttpStatus"] == "504" and original_failure["Bytes"] == 92
manifest = read(BEFORE / "SHA256.json")
assert len(manifest) == 19 and len({item["Name"] for item in manifest}) == 19
AFTER.mkdir(exist_ok=False)
LOGS.mkdir(exist_ok=False)
(AFTER / "SHA256.json").open("xb").write((BEFORE / "SHA256.json").read_bytes())
for item in manifest:
    if item["Name"] != "RELEASES":
        source = BEFORE / item["Name"]
        record = read(PUB / "public-download-20261105-1" / item["Name"] / "download.json")
        assert record["HttpStatus"] == "200" and record["ExitCode"] == 0
        assert source.stat().st_size == item["Bytes"] and sha(source) == item["Sha256"].lower()
        (AFTER / item["Name"]).open("xb").write(source.read_bytes())
item = next(row for row in manifest if row["Name"] == "RELEASES")
stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
url = "https://github.com/Tmcity/Iris/releases/download/v0.2.10/RELEASES?iris_public_read=" + stamp
command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "90", "--retry", "0",
    "--proto", "=https", "--proto-redir", "=https", "-H", "Cache-Control: no-cache", "-A", "Iris-public-asset-verification",
    "-D", str(LOGS / "response.headers"), "-o", str(AFTER / "RELEASES"), "--write-out", "%{http_code}", url]
result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
(LOGS / "stderr.log").open("xb").write(result.stderr)
record = {"Url": url, "HttpStatus": result.stdout.decode().strip(), "ExitCode": result.returncode,
    "AuthorizationSent": False, "Bytes": (AFTER / "RELEASES").stat().st_size, "Sha256": sha(AFTER / "RELEASES")}
(LOGS / "download.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(record, indent=2) + "\n")
assert record["HttpStatus"] == "200" and record["ExitCode"] == 0
assert record["Bytes"] == item["Bytes"] and record["Sha256"] == item["Sha256"].lower()
spec = importlib.util.spec_from_file_location("public_manifest", ROOT / "build/release_manifest.py")
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
verified = module.verify_directory(AFTER, "0.2.10")
assert verified["SourceCommit"] == "227b3d88938543766a27d86a94ade52f9e5b89b7" and verified["SourceGitTree"] == "69d4faf228d9d0b98d77ecb4a8dac582ad33421b"
assert verified["PublicAssets"] == 20 and verified["PayloadEntries"] == 97
provenance = read(AFTER / "release-provenance.json")
assert provenance["Build"]["GitHubRunId"] == "42042550153" and provenance["Build"]["GitHubRunAttempt"] == "1"
summary = read(AFTER / "validation-summary.json")
assert summary["SourceCommit"] == verified["SourceCommit"] and summary["FullTests"] == {"Total":3771,"Passed":3771,"Failed":0,"Skipped":0}
review = {**verified, "FullTests": summary["FullTests"], "Directory": str(AFTER), "Original504Retained": True,
    "OnlyFailedAssetRedownloaded": True, "All20AssetsValidated": True, "AnonymousPublicReadback": True}
(LOGS / "review.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(review, indent=2) + "\n")
print(json.dumps(review, indent=2))
