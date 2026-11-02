"""Freeze original CI failure only after its complete ZIP matches the independent official digest."""
from pathlib import Path, PurePosixPath
import datetime
import hashlib
import json
import shutil
import stat
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path("F:/Iris")
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.3-20261102-1"
OBS = PUBLICATION / "observations"
accepted = []
for path in sorted(OBS.glob("*-curl-artifacts-*.body")):
    record = json.loads(path.with_suffix(".json").read_text(encoding="utf-8-sig"))
    if record["ExitCode"] == 0 and record["HttpStatus"] == "200":
        raw = path.read_bytes()
        assert len(raw) == record["BodyBytes"] and hashlib.sha256(raw).hexdigest() == record["BodySha256"]
        accepted.append((path, record, json.loads(raw)))
assert accepted
api_path, api_record, data = accepted[-1]
artifact = next(item for item in data["artifacts"] if item["name"] == "iris-validation-41397874525-1")
assert not artifact["expired"]
archive_path = ROOT / "artifacts/t039c/ci-v023-failure-unverified-41397874525-1.zip"
archive_bytes = archive_path.read_bytes()
digest = hashlib.sha256(archive_bytes).hexdigest()
assert "sha256:" + digest == artifact["digest"]
assert len(archive_bytes) == artifact["size_in_bytes"]
report = ROOT / "tasks/done/T-039C.evidence/ci-v023-original-41397874525-1"
report.mkdir(exist_ok=False)
shutil.copyfile(api_path, report / "official-artifacts-api.body")
shutil.copyfile(api_path.with_suffix(".json"), report / "official-artifacts-api.json")
files = []
with zipfile.ZipFile(archive_path) as archive:
    assert len(archive.infolist()) <= 2000
    total_bytes = 0
    for item in archive.infolist():
        if item.is_dir():
            continue
        path = PurePosixPath(item.filename.replace("\\", "/"))
        assert not path.is_absolute() and ".." not in path.parts and ":" not in str(path)
        assert stat.S_IFMT(item.external_attr >> 16) != stat.S_IFLNK
        assert path.suffix.lower() in {".json", ".xml", ".log", ".png", ".txt"}
        assert item.file_size <= 16 * 1024 * 1024
        total_bytes += item.file_size
        assert total_bytes <= 64 * 1024 * 1024
        target = report.joinpath(*path.parts).resolve()
        assert target.is_relative_to(report.resolve())
        target.parent.mkdir(parents=True, exist_ok=True)
        contents = archive.read(item)
        with target.open("xb") as stream:
            stream.write(contents)
        files.append({"ArchivePath": item.filename, "Path": target.relative_to(ROOT).as_posix(), "Bytes": len(contents), "Sha256": hashlib.sha256(contents).hexdigest()})
totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
failures = []
for path in report.rglob("*.xml"):
    tree = ET.parse(path)
    for assembly in tree.iter("assembly"):
        for key in totals:
            totals[key] += int(assembly.attrib[key.lower()])
    for test in tree.iter("test"):
        if test.get("result") == "Fail":
            failures.append({"Name": test.attrib["name"], "SourceFile": test.get("source-file"), "SourceLine": test.get("source-line"), "Message": "\n".join(node.text or "" for node in test.iter("message")), "StackTrace": "\n".join(node.text or "" for node in test.iter("stack-trace"))})
assert totals == {"Total": 3755, "Passed": 3754, "Failed": 1, "Skipped": 0}
assert len(failures) == 1 and failures[0]["Name"].endswith("NetworkSettingsTests.InPlaceValueWithoutEventIsDetected_AndContinuouslyChangingStoreIsBounded")
binding = {"ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "RunId": 41397874525, "SourceCommit": "fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11", "Tag": "v0.2.3", "TagObject": "b4b8be76b6f4f08f62067d2a73f3e443fc439481", "ArtifactId": artifact["id"], "OfficialDigest": artifact["digest"], "OriginalArchive": str(archive_path), "ArchiveBytes": len(archive_bytes), "ArchiveSha256": digest, "ExactArchiveMatchedGitHubDigest": True, "AuthorizationSent": False, "FullTests": totals, "Failures": failures, "Files": files, "Scope": "All original CI bytes remain; no workflow rerun, tag rewrite or failure converted to a skip. Anonymous relay identity is not a provenance authority; its entire ZIP matched the separately obtained official GitHub digest."}
(report / "binding.json").write_text(json.dumps(binding, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
state = json.loads((PUBLICATION / "state.json").read_text(encoding="utf-8-sig"))
state.update(Stage="ci-validation-failed-publication-skipped", ReleasePublic=False, CiRunId=41397874525, OriginalArtifactDigestVerified=True, Failure={"Type": "CiValidationFailure", "Test": failures[0]["Name"]})
(PUBLICATION / "state.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"ArchiveBytes": len(archive_bytes), "OfficialDigestMatched": True, "OriginalFiles": len(files), "FullTests": totals, "FailedTest": failures[0]["Name"], "Public": False}, indent=2))
