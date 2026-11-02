"""Independently bind the new candidate, committed sources, actual validation and prior public package."""
from pathlib import Path
import datetime
import hashlib
import importlib.util
import json
import re
import shutil
import subprocess

ROOT = Path("F:/Iris")
WORK = ROOT / "artifacts/t039c/worktree-v025-ci-fix-20261102-1"
CANDIDATE = ROOT / "artifacts/t039c/release-0.2.5-public-base-candidate-2"
VALIDATION = ROOT / "artifacts/t039c/local-validation-v025-20261102-2"
REPORT = ROOT / "tasks/done/T-039C.evidence/next-source-v025-20261102-2"
SOURCE = "ee2d7118597d07b891066256349098877e65f794"
TREE = "9dff0cd9e8f7ef4da5ac1aa82d3b233c885a0883"
REPORT.mkdir(exist_ok=False)
spec = importlib.util.spec_from_file_location("candidate_manifest", ROOT / "build/release_manifest.py")
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)
manifest.ROOT = WORK
verified = manifest.verify_directory(CANDIDATE, "0.2.5")
assert verified["SourceCommit"] == SOURCE and verified["SourceGitTree"] == TREE
assert verified["PublicAssets"] == 18 and verified["PayloadEntries"] == 97
provenance = manifest.read_json(CANDIDATE / "release-provenance.json")
files = provenance["SourceFiles"]
committed = {}
for row in subprocess.check_output(["git", "ls-tree", "-r", "-z", SOURCE, "--", *manifest.SOURCE_ROOTS], cwd=WORK).decode().strip("\0").split("\0"):
    metadata, path = row.split("\t")
    mode, kind, blob = metadata.split()
    assert (mode, kind) == ("100644", "blob")
    committed[path] = blob
assert len(files) == len(committed) == 721
assert {item["Path"]: item["GitBlob"] for item in files} == committed
assert manifest.source_tree(files) == TREE
for item in files:
    path = WORK / item["Path"]
    assert path.stat().st_size == item["WorkingBytes"] and manifest.digest(path) == item["WorkingSha256"]
source_changes = subprocess.check_output(["git", "diff", "--name-only", "942b0ddeb62d9eb3e4f244574c0c093ab2942ffc", SOURCE], cwd=WORK).decode().splitlines()
assert set(source_changes) == {"build/release-plan.json", "docs/RELEASING.md", "tests/Iris.Core.Tests/Settings/SettingsEditCoordinatorTests.cs", "tests/Iris.Core.Tests/Translation/TranslationProviderRegistryTests.cs", "tests/Iris.Core.Tests/TestSupport/OwnedAsyncTestDriver.cs"}
previous = next(item for item in manifest.read_json(CANDIDATE / "releases.win.json")["Assets"] if item["Version"] == "0.2.2")
assert previous["Type"] == "Full" and previous["Size"] == 64627273 and previous["SHA256"].lower() == "c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14"
summary = manifest.read_json(VALIDATION / "validation-summary.json")
assert summary["SourceCommit"] == SOURCE and summary["BuildWarnings"] == summary["BuildErrors"] == 0
assert summary["FullTests"] == {"Total": 3757, "Passed": 3755, "Failed": 0, "Skipped": 2}
assert re.search(r"Ran 16 tests", (VALIDATION / "release-manifest-tests.log").read_text(encoding="utf-8-sig"))
assert all(item["ExitCode"] == 0 and manifest.digest(VALIDATION / item["Log"]) == item["Sha256"] for item in summary["Commands"])
for path in sorted(VALIDATION.rglob("*")):
    if path.is_file() and path.suffix in {".json", ".xml", ".log", ".png", ".txt"}:
        target = REPORT / "validation" / path.relative_to(VALIDATION)
        target.parent.mkdir(parents=True, exist_ok=True)
        assert not target.exists()
        shutil.copyfile(path, target)
for name in ("release-provenance.json", "SHA256.json", "payload-manifest.json", "staging-manifest.json", "releases.win.json", "assets.win.json", "RELEASES", "RELEASE_NOTES.md", "INSTALLATION.md", "package.log"):
    shutil.copyfile(CANDIDATE / name, REPORT / name)
assets = [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": manifest.digest(path)} for path in sorted(CANDIDATE.iterdir()) if path.is_file()]
review = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), **verified, "Candidate": str(CANDIDATE), "CommittedSourceEntriesMatched": 721, "WorkingSourceEntriesMatched": 721, "SourceChanges": source_changes, "ProductSourceChanges": [], "BuildWarnings": 0, "BuildErrors": 0, "FullTests": summary["FullTests"], "ReleaseRegressions": 16, "PreviousPublicFull": previous, "Assets": assets, "PublicationAuthorized": False, "RealUiUpgradeVerified": False, "RestartCountVerified": False, "PostUpdateUninstallVerified": False}
(REPORT / "review.json").write_text(json.dumps(review, indent=2) + "\n", encoding="utf-8", newline="\n")
records = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": manifest.digest(path)} for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").write_text(json.dumps({"SourceCommit": SOURCE, "Files": records}, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({key: review[key] for key in ("Version", "SourceCommit", "SourceGitTree", "PublicAssets", "PayloadEntries", "CommittedSourceEntriesMatched", "FullTests", "FullSha256", "OuterSha256", "PublicationAuthorized")}, indent=2))
