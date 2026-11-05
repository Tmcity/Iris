"""Freeze exact successful CI artifacts and cross-check their raw reports against public assets."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import stat
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path("F:/Iris")
REPORT = ROOT / "tasks/done/T-039C.evidence/ci-v027-original-41976966535-1"
PUBLIC = ROOT / "artifacts/t039c/public-0.2.7-download-20261105-1"
REPORT.mkdir(exist_ok=False)
files = []
specs = (("ci-v027-original-41976966535-1.zip", "validation", "f74d5800df0525b1594f5793394c8d6992f35b442ed429d9a7504bd4389f27a9"),
         ("ci-v027-publication-41976966535-1.zip", "publication", "22e043efae52700f56676539b6c87848ac86d4b2bd71d7755129656454b72cb8"))
for name, folder, expected in specs:
    archive = ROOT / "artifacts/t039c" / name
    raw = archive.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == expected
    (REPORT / name).open("xb").write(raw)
    seen, size = set(), 0
    with zipfile.ZipFile(archive) as zipped:
        assert len(zipped.infolist()) <= 2000
        for item in zipped.infolist():
            if item.is_dir():
                continue
            path = PurePosixPath(item.filename.replace("\\", "/"))
            assert not path.is_absolute() and ".." not in path.parts and ":" not in str(path)
            assert stat.S_IFMT(item.external_attr >> 16) != stat.S_IFLNK
            assert path.suffix in {".json", ".xml", ".log", ".png", ".txt"}
            size += item.file_size
            assert item.file_size < 16 * 1024 * 1024 and size < 64 * 1024 * 1024
            destination = (REPORT / folder).joinpath(*path.parts).resolve()
            assert destination.is_relative_to(REPORT.resolve()) and str(destination).casefold() not in seen
            seen.add(str(destination).casefold())
            destination.parent.mkdir(parents=True, exist_ok=True)
            data = zipped.read(item)
            destination.open("xb").write(data)
            files.append({"Path": destination.relative_to(REPORT).as_posix(), "Bytes": len(data),
                          "Sha256": hashlib.sha256(data).hexdigest()})
totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
tests = []
for path in (REPORT / "validation").rglob("*.xml"):
    tree = ET.parse(path)
    for assembly in tree.iter("assembly"):
        assert int(assembly.get("errors", 0)) == int(assembly.get("not-run", 0)) == 0
        for key in totals:
            totals[key] += int(assembly.get(key.lower(), 0))
    tests.extend(tree.iter("test"))
assert totals == {"Total": 3759, "Passed": 3759, "Failed": 0, "Skipped": 0}
for name in ("H13_OnHotkeyPressed_DoesNotBlockWndProc_RunsOnThreadPool",
             "LateStartAwaitObservation_WhenRunnerStartHasNotReturned_WaitsForPublicationAndActualDrain"):
    selected = [test for test in tests if test.get("name", "").endswith(name)]
    assert len(selected) == 1 and selected[0].get("result") == "Pass"
validation = REPORT / "validation/iris-validation"
summary = json.loads((validation / "validation-summary.json").read_bytes())
assert summary["SourceCommit"] == "fc44a3a78c2729c25dfb91296c51602b843a94f9" and summary["FullTests"] == totals
for name in ("release-manifest-tests.log", "release-build.log", "full-tests.log", "validation-summary.json"):
    assert (validation / name).read_bytes() == (PUBLIC / name).read_bytes(), name
for record in summary["Commands"]:
    data = (validation / record["Log"]).read_bytes()
    assert record["ExitCode"] == 0 and hashlib.sha256(data).hexdigest() == record["Sha256"]
package = REPORT / "validation/iris-package-logs"
assert (package / "package.log").read_bytes() == (PUBLIC / "package.log").read_bytes()
receipt = json.loads((REPORT / "publication/publish-result.json").read_bytes())
assert receipt["Tag"] == "v0.2.7" and receipt["SourceCommit"] == summary["SourceCommit"]
assert receipt["SourceGitTree"] == "81aea2fa6cad8a3dcde34b081185dd03aae9c31c"
assert receipt["Stage"] == "published-and-listed" and receipt["Public"] is True
assert receipt["VisibilityVerification"] == "completed" and receipt["PublicationCliConfirmed"] is True
all_files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size,
              "Sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"RunId": 41976966535, "RunAttempt": 1, "SourceCommit": summary["SourceCommit"],
           "ArtifactDigestsMatchedOfficialPublicPage": True, "OfficialRestApiArtifactMetadataVerified": False,
           "FullTests": totals, "PublicValidationAssetsMatchOriginalCiBytes": True,
           "PublicationReceipt": receipt, "ArchiveExtractedFiles": len(files), "Files": all_files}
(REPORT / "binding.json").write_text(json.dumps(binding, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"OriginalExtractedFiles": len(files), "FullTests": totals,
                  "PublicLogsMatchOriginalCiBytes": True, "PublicationReceiptVerified": True}, indent=2))
