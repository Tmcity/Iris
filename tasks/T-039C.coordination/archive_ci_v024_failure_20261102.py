"""Retain the failed v0.2.4 run after binding its ZIP to the official API digest."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import shutil
import stat
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path("F:/Iris")
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.4-20261102-1"
accepted = []
for folder in sorted((PUBLICATION / "observations").glob("*-artifacts")):
    metadata = json.loads((folder / "observation.json").read_text(encoding="utf-8-sig"))
    if metadata["Status"] == 200:
        raw = (folder / "response.body").read_bytes()
        assert hashlib.sha256(raw).hexdigest() == metadata["Sha256"]
        accepted.append((folder, json.loads(raw)))
assert accepted
folder, data = accepted[-1]
artifact = next(item for item in data["artifacts"] if item["name"] == "iris-validation-41407242768-1")
archive_path = ROOT / "artifacts/t039c/ci-v024-original-41407242768-1.zip"
raw = archive_path.read_bytes()
digest = hashlib.sha256(raw).hexdigest()
assert artifact["size_in_bytes"] == len(raw) == 505866
assert artifact["digest"] == "sha256:" + digest
report = ROOT / "tasks/done/T-039C.evidence/ci-v024-original-41407242768-1"
report.mkdir(exist_ok=False)
shutil.copyfile(folder / "response.body", report / "official-artifacts-api.body")
shutil.copyfile(folder / "observation.json", report / "official-artifacts-api.json")
files = []
with zipfile.ZipFile(archive_path) as archive:
    total = 0
    assert len(archive.infolist()) <= 2000
    for item in archive.infolist():
        if item.is_dir():
            continue
        path = PurePosixPath(item.filename.replace("\\", "/"))
        assert not path.is_absolute() and ".." not in path.parts and ":" not in str(path)
        assert stat.S_IFMT(item.external_attr >> 16) != stat.S_IFLNK
        assert path.suffix in {".json", ".xml", ".log", ".png", ".txt"} and item.file_size <= 16 * 1024 * 1024
        total += item.file_size
        assert total <= 64 * 1024 * 1024
        destination = report.joinpath(*path.parts).resolve()
        assert destination.is_relative_to(report.resolve())
        destination.parent.mkdir(parents=True, exist_ok=True)
        contents = archive.read(item)
        destination.open("xb").write(contents)
        files.append({"Path": destination.relative_to(ROOT).as_posix(), "ArchivePath": item.filename, "Bytes": len(contents), "Sha256": hashlib.sha256(contents).hexdigest()})
totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
failures = []
for path in report.rglob("*.xml"):
    tree = ET.parse(path)
    for assembly in tree.iter("assembly"):
        for key in totals:
            totals[key] += int(assembly.get(key.lower(), 0))
    for test in tree.iter("test"):
        if test.get("result") == "Fail":
            failures.append({"Name": test.get("name"), "Duration": test.get("time"), "Message": "\n".join(node.text or "" for node in test.iter("message")), "StackTrace": "\n".join(node.text or "" for node in test.iter("stack-trace"))})
assert totals == {"Total": 3756, "Passed": 3753, "Failed": 2, "Skipped": 1}
binding = {"RunId": 41407242768, "SourceCommit": "942b0ddeb62d9eb3e4f244574c0c093ab2942ffc", "Tag": "v0.2.4", "TagObject": "09ee251c853b03e628b8099a439c5dd75faf3141", "ArtifactId": artifact["id"], "Archive": str(archive_path), "ArchiveBytes": len(raw), "OfficialDigest": artifact["digest"], "ExactArchiveMatchedGitHubDigest": True, "FullTests": totals, "Failures": failures, "Files": files}
(report / "binding.json").write_text(json.dumps(binding, indent=2) + "\n", encoding="utf-8", newline="\n")
state = json.loads((PUBLICATION / "state.json").read_text(encoding="utf-8-sig"))
state.update(Stage="ci-validation-failed-publication-skipped", ReleasePublic=False, CiRunId=41407242768, OriginalArtifactDigestVerified=True, Failure={"Type": "CiValidationFailure", "Tests": [failure["Name"] for failure in failures]})
(PUBLICATION / "state.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"OfficialDigestMatched": True, "OriginalFiles": len(files), "FullTests": totals}, indent=2))
