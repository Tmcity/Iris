"""Supplement existing official website proofs only after fresh anonymous API responses succeed."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json

ROOT = Path("F:/Iris")
PUB = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1"
PUBLIC = ROOT / "artifacts/t039c/public-0.2.10-download-20261105-2"
parser = argparse.ArgumentParser()
for name in ("run", "jobs", "artifacts", "release"):
    parser.add_argument("--" + name, type=Path, required=True)
args = parser.parse_args()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


data = {}
for name in ("run", "jobs", "artifacts", "release"):
    path = getattr(args, name).resolve()
    assert path.is_relative_to(PUB / "observations")
    observation = read(path.parent / "observation.json")
    assert observation["HttpStatus"] == "200" and observation["ExitCode"] == 0 and observation["AuthorizationSent"] is False
    assert observation["BodySha256"] == sha(path)
    data[name] = read(path)
run = data["run"]
assert run["id"] == 42042550153 and run["run_attempt"] == 1 and run["head_branch"] == "v0.2.10"
assert run["head_sha"] == "227b3d88938543766a27d86a94ade52f9e5b89b7"
assert run["status"] == "completed" and run["conclusion"] == "success"
jobs = {row["name"]: row for row in data["jobs"]["jobs"]}
assert jobs["package"]["id"] == 127296844225 and jobs["package"]["conclusion"] == "success"
assert jobs["publish"]["id"] == 127301558754 and jobs["publish"]["conclusion"] == "success"
artifacts = {row["name"]: row for row in data["artifacts"]["artifacts"]}
for name, identity, filename, digest in (
    ("iris-validation-42042550153-1", 13423677433, "ci-v0210-original-42042550153-1.zip", "237f8958d4e8ddbf6ae28bddbf38c5c63598673a57d2413dbc6017105ae48e36"),
    ("iris-publication-42042550153-1", 13423731984, "ci-v0210-publication-42042550153-1.zip", "d08fde86b418f15fafbe090e0d62e504350e2f1520fdfcffbbab61ee410ec55d"),
):
    item = artifacts[name]
    archive = ROOT / "artifacts/t039c" / filename
    assert item["id"] == identity and item["size_in_bytes"] == archive.stat().st_size
    assert item["digest"] == "sha256:" + digest and sha(archive) == digest and item["expired"] is False
release = data["release"]
assert release["tag_name"] == "v0.2.10" and release["draft"] is False and release["prerelease"] is False
assert release["html_url"] == "https://github.com/Tmcity/Iris/releases/tag/v0.2.10"
assert len(release["assets"]) == 20 and {item["name"] for item in release["assets"]} == {path.name for path in PUBLIC.iterdir()}
for item in release["assets"]:
    path = PUBLIC / item["name"]
    assert item["size"] == path.stat().st_size and item["digest"] == "sha256:" + sha(path)
    assert item["browser_download_url"] == "https://github.com/Tmcity/Iris/releases/download/v0.2.10/" + item["name"]
REPORT = ROOT / "tasks/done/T-039C.evidence/ci-v0210-api-supplement-20261105-1"
REPORT.mkdir(exist_ok=False)
for name in ("run", "jobs", "artifacts", "release"):
    source = getattr(args, name)
    destination = REPORT / name
    destination.mkdir()
    for path in sorted(source.parent.iterdir()):
        if path.is_file():
            (destination / path.name).open("xb").write(path.read_bytes())
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({
    "VerifiedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "SourceCommit": run["head_sha"],
    "RunId": 42042550153, "RunAttempt": 1, "ReleaseId": release["id"], "OfficialApiArtifactDigestsVerified": True,
    "All20PublicAssetApiDigestsVerified": True, "OriginalArchivesRewritten": False, "Files": files}, indent=2) + "\n")
state = read(PUB / "state.json")
state.update(OfficialRestApiMetadataVerified=True, ReleaseId=release["id"],
             Stage="published-official-api-website-and-anonymous-bytes-verified")
(PUB / "state.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"ReleaseId": release["id"], "OfficialApiArtifactDigestsVerified": True,
                  "All20PublicAssetApiDigestsVerified": True, "OriginalArchivesRewritten": False}, indent=2))
