"""Freeze the original failed v0.2.5 artifact after checking GitHub's digest."""
from pathlib import Path, PurePosixPath
import datetime
import hashlib
import json
import shutil
import stat
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path("F:/Iris")
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.5-20261102-1"
SOURCE = "ee2d7118597d07b891066256349098877e65f794"
api = ROOT / "artifacts/t039c/v025-artifacts-uncached-20261103-1.body"
accepted = json.loads((PUBLICATION / "artifact-binding-20261103.json").read_text(encoding="utf-8-sig"))
assert hashlib.sha256(api.read_bytes()).hexdigest() == accepted["ApiBodySha256"]
artifact = next(item for item in json.loads(api.read_bytes())["artifacts"]
                if item["name"] == "iris-validation-41461057191-1")
assert artifact["id"] == 13148997572 and artifact["workflow_run"]["head_sha"] == SOURCE
archive_path = ROOT / "artifacts/t039c/ci-v025-original-41461057191-1.zip"
raw = archive_path.read_bytes()
digest = hashlib.sha256(raw).hexdigest()
assert artifact["size_in_bytes"] == len(raw) == accepted["ArchiveBytes"] == 506060
assert artifact["digest"] == accepted["OfficialDigest"] == "sha256:" + digest
report = ROOT / "tasks/done/T-039C.evidence/ci-v025-original-41461057191-1"
report.mkdir(exist_ok=False)
shutil.copyfile(api, report / "official-artifacts-api.body")
shutil.copyfile(api.with_suffix(".headers"), report / "official-artifacts-api.headers")
shutil.copyfile(PUBLICATION / "artifact-binding-20261103.json", report / "official-artifact-binding.json")
pages = []
for name in ("v025-run-public-page-20261103-1.html", "v025-job-package-public-20261103-1.html",
             "v025-job-publish-public-20261103-1.html", "v025-workflow-public-page-20261103-1.html",
             "v025-release-public-page-20261103-1.html"):
    original = ROOT / "artifacts/t039c" / name
    shutil.copyfile(original, report / name)
    pages.append({"Path": name, "Bytes": original.stat().st_size,
                  "Sha256": hashlib.sha256(original.read_bytes()).hexdigest()})
files = []
seen = set()
with zipfile.ZipFile(archive_path) as archive:
    total = 0
    assert len(archive.infolist()) <= 2000
    for item in archive.infolist():
        if item.is_dir():
            continue
        path = PurePosixPath(item.filename.replace("\\", "/"))
        assert not path.is_absolute() and ".." not in path.parts and ":" not in str(path)
        assert stat.S_IFMT(item.external_attr >> 16) != stat.S_IFLNK
        assert path.suffix in {".json", ".xml", ".log", ".png", ".txt"}
        assert item.file_size <= 16 * 1024 * 1024
        total += item.file_size
        assert total <= 64 * 1024 * 1024
        destination = report.joinpath(*path.parts).resolve()
        assert destination.is_relative_to(report.resolve())
        identity = str(destination).casefold()
        assert identity not in seen
        seen.add(identity)
        destination.parent.mkdir(parents=True, exist_ok=True)
        contents = archive.read(item)
        destination.open("xb").write(contents)
        files.append({"Path": destination.relative_to(ROOT).as_posix(), "ArchivePath": item.filename,
                      "Bytes": len(contents), "Sha256": hashlib.sha256(contents).hexdigest()})
totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
failures = []
for path in report.rglob("*.xml"):
    tree = ET.parse(path)
    for assembly in tree.iter("assembly"):
        for key in totals:
            totals[key] += int(assembly.get(key.lower(), 0))
    for test in tree.iter("test"):
        if test.get("result") == "Fail":
            failures.append({"Name": test.get("name"), "Duration": test.get("time"),
                             "Message": "\n".join(node.text or "" for node in test.iter("message")),
                             "StackTrace": "\n".join(node.text or "" for node in test.iter("stack-trace"))})
assert totals == {"Total": 3757, "Passed": 3755, "Failed": 1, "Skipped": 1}
assert len(failures) == 1 and failures[0]["Name"].endswith(
    "SaveAsync_WhenAutoStartApplyFails_PreservesRetryEligibility_AcrossUnrelatedSavesAndReload")
binding = {"ArchivedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
           "RunId": 41461057191, "SourceCommit": SOURCE, "Tag": "v0.2.5",
           "TagObject": "95c9c344ac385798503a317b8a9ac2fb57485134", "ArtifactId": artifact["id"],
           "Archive": str(archive_path), "ArchiveBytes": len(raw), "OfficialDigest": artifact["digest"],
           "ExactArchiveMatchedGitHubDigest": True, "FullTests": totals,
           "Failures": failures, "Files": files, "PublicPages": pages}
(report / "binding.json").write_text(json.dumps(binding, indent=2) + "\n", encoding="utf-8", newline="\n")
state = json.loads((PUBLICATION / "state.json").read_text(encoding="utf-8-sig"))
assert state["Tag"] == "v0.2.5" and state["SourceCommit"] == SOURCE and state["TagRemoteVerified"]
state.update(Stage="ci-validation-failed-publication-skipped", ReleasePublic=False, CiRunId=41461057191,
             OriginalArtifactDigestVerified=True, Failure={"Type": "CiValidationFailure",
             "Tests": [failure["Name"] for failure in failures]})
(PUBLICATION / "state.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
before = ROOT / "tasks/done/T-039C.evidence/ci-v025-before-fix-20261103-1"
before.mkdir(exist_ok=False)
paths = ["tests/Iris.App.Tests/Settings/SettingsViewModelTests.cs",
         "src/Iris.App/Settings/SettingsViewModel.cs",
         "src/Iris.App/Services/GeneralSettingsSaveCoordinator.cs"]
snapshots = []
for path in paths:
    data = subprocess.check_output(["git", "show", SOURCE + ":" + path], cwd=ROOT)
    target = before / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.open("xb").write(data)
    snapshots.append({"Path": path, "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest()})
(before / "binding.json").write_text(json.dumps({"SourceCommit": SOURCE, "Files": snapshots}, indent=2)
                                   + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"OfficialDigestMatched": True, "OriginalFiles": len(files), "FullTests": totals,
                  "Failure": failures[0], "FrozenSources": len(snapshots)}, indent=2))
