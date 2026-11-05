"""Supplement the immutable original CI archive with newly available official anonymous API metadata."""
from pathlib import Path
import datetime
import hashlib
import json

ROOT = Path("F:/Iris")
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.8-20261105-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/ci-v028-api-supplement-20261105-1"
folders = {"artifacts": "20261105T122736672284Z-artifacts", "run": "20261105T122810132579Z-run",
           "jobs": "20261105T122810151327Z-jobs", "release": "20261105T122810162331Z-release"}
responses = {}
for kind, name in folders.items():
    folder = PUBLICATION / "observations" / name
    observation = json.loads((folder / "observation.json").read_text(encoding="utf-8-sig"))
    assert observation["HttpStatus"] == ("404" if kind == "release" else "200") and observation["ExitCode"] == 0
    assert observation["AuthorizationSent"] is False
    assert hashlib.sha256((folder / "response.body").read_bytes()).hexdigest() == observation["BodySha256"]
    responses[kind] = json.loads((folder / "response.body").read_bytes())
run = responses["run"]
assert run["id"] == 42015639513 and run["run_attempt"] == 1
assert run["head_sha"] == "0c9b0a1881d07224597163b1e9b2f080ab60d434" and run["head_branch"] == "v0.2.8"
assert run["status"] == "completed" and run["conclusion"] == "failure"
jobs = {row["name"]: row for row in responses["jobs"]["jobs"]}
assert jobs["package"]["id"] == 127205378609 and jobs["package"]["conclusion"] == "failure"
assert jobs["publish"]["id"] == 127208381501 and jobs["publish"]["conclusion"] == "skipped"
assert len(responses["artifacts"]["artifacts"]) == 1
artifact = responses["artifacts"]["artifacts"][0]
assert artifact["id"] == 13409681541 and artifact["name"] == "iris-validation-42015639513-1"
archive = ROOT / "tasks/done/T-039C.evidence/ci-v028-original-42015639513-1/original-artifact.zip"
assert artifact["size_in_bytes"] == archive.stat().st_size == 505370 and artifact["expired"] is False
assert artifact["digest"] == "sha256:" + hashlib.sha256(archive.read_bytes()).hexdigest()
REPORT.mkdir(exist_ok=False)
for kind, name in folders.items():
    target = REPORT / kind
    target.mkdir()
    for path in sorted((PUBLICATION / "observations" / name).iterdir()):
        if path.is_file():
            (target / path.name).open("xb").write(path.read_bytes())
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size,
          "Sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"VerifiedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "SourceCommit": run["head_sha"],
           "RunId": 42015639513, "RunAttempt": 1, "OfficialApiArtifactDigestVerified": True,
           "Artifact": {key: artifact[key] for key in ("id", "name", "size_in_bytes", "digest", "expired")},
           "CiConclusion": "failure", "PackageConclusion": "failure", "PublishConclusion": "skipped",
           "PublicReleaseAbsentHttp404": True, "OriginalArchiveRewritten": False, "AuthorizationSent": False, "Files": files}
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(binding, indent=2) + "\n")
print(json.dumps({key: binding[key] for key in ("RunId", "RunAttempt", "OfficialApiArtifactDigestVerified",
    "CiConclusion", "PublishConclusion", "PublicReleaseAbsentHttp404", "OriginalArchiveRewritten")}, indent=2))
