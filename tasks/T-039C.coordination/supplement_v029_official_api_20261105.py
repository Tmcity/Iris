"""Supplement existing official website proofs only after fresh anonymous API responses succeed."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json

ROOT = Path("F:/Iris")
PUB = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1"
PUBLIC = ROOT / "artifacts/t039c/public-0.2.9-download-20261105-1"
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
assert run["id"] == 42028799541 and run["run_attempt"] == 1 and run["head_branch"] == "v0.2.9"
assert run["head_sha"] == "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"
assert run["status"] == "completed" and run["conclusion"] == "success"
jobs = {row["name"]: row for row in data["jobs"]["jobs"]}
assert jobs["package"]["id"] == 127249641900 and jobs["package"]["conclusion"] == "success"
assert jobs["publish"]["id"] == 127253344007 and jobs["publish"]["conclusion"] == "success"
artifacts = {row["name"]: row for row in data["artifacts"]["artifacts"]}
for name, identity, filename, digest in (
    ("iris-validation-42028799541-1", 13414499847, "ci-v029-original-42028799541-1.zip", "ecc5a3ebf25404603d84c9aa729c74c35602664db122779ce6da095cbe641888"),
    ("iris-publication-42028799541-1", 13415502207, "ci-v029-publication-42028799541-1.zip", "9b194a0b291c07961af420ca0bef219fddbb7899ef2a968c1e4c71df509b2c0a"),
):
    item = artifacts[name]
    archive = ROOT / "artifacts/t039c" / filename
    assert item["id"] == identity and item["size_in_bytes"] == archive.stat().st_size
    assert item["digest"] == "sha256:" + digest and sha(archive) == digest and item["expired"] is False
release = data["release"]
assert release["tag_name"] == "v0.2.9" and release["draft"] is False and release["prerelease"] is False
assert release["html_url"] == "https://github.com/Tmcity/Iris/releases/tag/v0.2.9"
assert len(release["assets"]) == 19 and {item["name"] for item in release["assets"]} == {path.name for path in PUBLIC.iterdir()}
for item in release["assets"]:
    path = PUBLIC / item["name"]
    assert item["size"] == path.stat().st_size and item["digest"] == "sha256:" + sha(path)
    assert item["browser_download_url"] == "https://github.com/Tmcity/Iris/releases/download/v0.2.9/" + item["name"]
REPORT = ROOT / "tasks/done/T-039C.evidence/ci-v029-api-supplement-20261105-1"
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
    "RunId": 42028799541, "RunAttempt": 1, "ReleaseId": release["id"], "OfficialApiArtifactDigestsVerified": True,
    "All19PublicAssetApiDigestsVerified": True, "OriginalArchivesRewritten": False, "Files": files}, indent=2) + "\n")
state = read(PUB / "state.json")
state.update(OfficialRestApiMetadataVerified=True, ReleaseId=release["id"],
             Stage="published-official-api-website-and-anonymous-bytes-verified")
(PUB / "state.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"ReleaseId": release["id"], "OfficialApiArtifactDigestsVerified": True,
                  "All19PublicAssetApiDigestsVerified": True, "OriginalArchivesRewritten": False}, indent=2))
