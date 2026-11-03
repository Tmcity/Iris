"""Bind the committed sources, actual test reports, candidate and public prior release."""
from pathlib import Path
import datetime
import hashlib
import importlib.util
import json
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path("F:/Iris")
WORK = ROOT / "artifacts/t039c/worktree-v026-ci-fix-20261103-1"
CANDIDATE = ROOT / "artifacts/t039c/release-0.2.6-public-base-candidate-1"
VALIDATION = ROOT / "artifacts/t039c/local-validation-v026-20261103-1"
PACKAGE = ROOT / "artifacts/t039c/package-0.2.6-public-base-candidate-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/next-source-v026-20261103-1"
OBSERVATIONS = ROOT / "tasks/T-039C.coordination/v026-prepublication-review-20261103-1"
SOURCE = "c32fcb283e417ab11715c46207fdc8cfde4baedf"
PREVIOUS_SOURCE = "ee2d7118597d07b891066256349098877e65f794"
TREE = "c39947f03475872a5178929e32b078a383d537e2"

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def totals_and_tests(directory):
    totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
    tests = []
    for path in sorted(Path(directory).glob("*.xml")):
        tree = ET.parse(path)
        for assembly in tree.iter("assembly"):
            for key in totals:
                totals[key] += int(assembly.get(key.lower(), 0))
        tests.extend(tree.iter("test"))
    return totals, tests

assert subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=WORK).decode().strip() == SOURCE
spec = importlib.util.spec_from_file_location("candidate_manifest", WORK / "build/release_manifest.py")
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)
subprocess.run(["git", "diff", "--quiet", "--", *manifest.SOURCE_ROOTS], cwd=WORK, check=True)
subprocess.run(["git", "diff", "--cached", "--quiet", "--", *manifest.SOURCE_ROOTS], cwd=WORK, check=True)
assert not subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "--", *manifest.SOURCE_ROOTS], cwd=WORK).strip()
verified = manifest.verify_directory(CANDIDATE, "0.2.6")
assert verified["SourceCommit"] == SOURCE and verified["SourceGitTree"] == TREE
assert verified["PublicAssets"] == 18 and verified["PayloadEntries"] == 97
assert verified["OuterSha256"] == "10274bb583095de434874cb15c95839c503d3be4bd21fc6d7f5c38102401ed02"
assert verified["FullSha256"] == "e478bb4b6accab61e5e7dc25229f8306771abf6c914026f75700ad7f45e75d8b"
provenance = read(CANDIDATE / "release-provenance.json")
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
    assert path.stat().st_size == item["WorkingBytes"] and digest(path) == item["WorkingSha256"]
changes = subprocess.check_output(["git", "diff", "--name-only", PREVIOUS_SOURCE, SOURCE], cwd=WORK).decode().splitlines()
assert set(changes) == {"build/release-plan.json", "docs/RELEASING.md", "tests/Iris.App.Tests/Settings/SettingsViewModelTests.cs"}
previous = next(item for item in read(CANDIDATE / "releases.win.json")["Assets"] if item["Version"] == "0.2.2")
assert previous["Type"] == "Full" and previous["Size"] == 64627273
assert previous["SHA256"].lower() == "c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14"
prior_full = ROOT / "artifacts/t039c/public-0.2.2-download-20261101-1" / previous["FileName"]
assert prior_full.stat().st_size == previous["Size"] and digest(prior_full) == previous["SHA256"].lower()
summary = read(VALIDATION / "validation-summary.json")
assert summary["SourceCommit"] == SOURCE and summary["BuildWarnings"] == summary["BuildErrors"] == 0
expected_totals = {"Total": 3758, "Passed": 3756, "Failed": 0, "Skipped": 2}
assert summary["FullTests"] == expected_totals
raw_totals, raw_tests = totals_and_tests(VALIDATION / "reports")
assert raw_totals == expected_totals
cross_save = [test for test in raw_tests if "SaveAsync_WhenAutoStartApplyFails_PreservesRetryEligibility_AcrossUnrelatedSavesAndReload" in test.get("name", "")]
assert len(cross_save) == 2 and all(test.get("result") == "Pass" for test in cross_save)
assert any("True" in test.get("name", "") for test in cross_save)
assert any("False" in test.get("name", "") for test in cross_save)
assert all("error=183" in "".join(test.find("reason").itertext()) for test in raw_tests if test.get("result") == "Skip")
assert re.search(r"Ran 16 tests", (VALIDATION / "release-manifest-tests.log").read_text(encoding="utf-8-sig"))
assert all(item["ExitCode"] == 0 and digest(VALIDATION / item["Log"]) == item["Sha256"] for item in summary["Commands"])
package = read(PACKAGE / "package.command.json")
assert package["ExitCode"] == 0 and digest(PACKAGE / "package.log") == package["Sha256"]
assert str(WORK / "build/pack.ps1") in package["Command"] and "0.2.6" in package["Command"]
assert Path(package["Command"][0]).as_posix().endswith("System32/WindowsPowerShell/v1.0/powershell.exe")
before_totals, before_tests = totals_and_tests(ROOT / "artifacts/t039c/query-counterexample-before-20261103-2/reports")
assert before_totals == {"Total": 1, "Passed": 0, "Failed": 1, "Skipped": 0}
assert before_tests[0].get("result") == "Fail"
assert "vm.CanRetryAutoStart" in "".join(before_tests[0].find("failure").itertext())
after_totals, _ = totals_and_tests(ROOT / "artifacts/t039c/autostart-handshakes-after-20261103-2/reports")
assert after_totals == {"Total": 18, "Passed": 18, "Failed": 0, "Skipped": 0}
public = {}
for name in ("run", "jobs", "releases"):
    metadata = read(OBSERVATIONS / ("public-" + name + ".json"))
    body = OBSERVATIONS / ("public-" + name + ".body")
    assert metadata["HttpStatus"] == "200" and metadata["AuthorizationSent"] is False
    assert body.stat().st_size == metadata["BodyBytes"] and digest(body) == metadata["BodySha256"]
    public[name] = json.loads(body.read_bytes())
assert public["run"]["head_sha"] == PREVIOUS_SOURCE and public["run"]["status"] == "completed"
assert public["run"]["conclusion"] == "failure" and public["run"]["run_attempt"] == 1
build_job = next(job for job in public["jobs"]["jobs"] if job["name"] == "package")
publish_job = next(job for job in public["jobs"]["jobs"] if job["name"] == "publish")
assert build_job["conclusion"] == "failure" and publish_job["conclusion"] == "skipped"
assert next(step for step in build_job["steps"] if step["name"] == "Build and bind exact public assets with Windows PowerShell 5.1")["conclusion"] == "skipped"
assert {release["tag_name"] for release in public["releases"]} == {"v0.2.2"}
assert not public["releases"][0]["draft"] and not public["releases"][0]["prerelease"]
assert len(public["releases"][0]["assets"]) == 16
remote_record = read(ROOT / "tasks/T-039C.coordination/v026-remote-preparation-before-1.command.json")
remote_log = ROOT / "tasks/T-039C.coordination/v026-remote-preparation-before-1.log"
assert remote_record["exitCode"] == 0 and digest(remote_log) == remote_record["logSha256"]
remote = {name: sha for sha, name in (row.split() for row in remote_log.read_text(encoding="utf-8-sig").splitlines())}
assert remote["refs/heads/main"] == remote["refs/heads/codex/t039c-public-release"] == "285df3a2986f3379994eb56dd8b75617db9f470d"
assert remote["refs/tags/v0.2.5^{}"] == PREVIOUS_SOURCE and "refs/tags/v0.2.6" not in remote
REPORT.mkdir(exist_ok=False)
for folder, target_folder in ((VALIDATION, "validation"), (OBSERVATIONS, "public-observations")):
    for path in sorted(folder.rglob("*")):
        if path.is_file() and path.suffix in {".json", ".xml", ".log", ".png", ".txt", ".body", ".headers"}:
            target = REPORT / target_folder / path.relative_to(folder)
            target.parent.mkdir(parents=True, exist_ok=True)
            assert not target.exists()
            shutil.copyfile(path, target)
for name in ("release-provenance.json", "SHA256.json", "payload-manifest.json", "staging-manifest.json", "releases.win.json", "assets.win.json", "RELEASES", "RELEASE_NOTES.md", "INSTALLATION.md", "package.log"):
    shutil.copyfile(CANDIDATE / name, REPORT / name)
shutil.copyfile(PACKAGE / "package.command.json", REPORT / "package.command.json")
shutil.copyfile(remote_log, REPORT / "remote-preparation-before.log")
shutil.copyfile(ROOT / "tasks/T-039C.coordination/v026-remote-preparation-before-1.command.json", REPORT / "remote-preparation-before.command.json")
targeted = REPORT / "targeted-regressions"
targeted.mkdir()
for label in ("v025-query-counterexample-before-1", "v025-query-counterexample-before-2", "v025-autostart-handshakes-after-1", "v025-autostart-handshakes-after-2"):
    command = read(ROOT / "tasks/T-039C.coordination" / (label + ".command.json"))
    log = ROOT / "tasks/T-039C.coordination" / (label + ".log")
    assert digest(log) == command["logSha256"]
    for suffix in (".command.json", ".log"):
        shutil.copyfile(ROOT / "tasks/T-039C.coordination" / (label + suffix), targeted / (label + suffix))
for folder in ("query-counterexample-before-20261103-1", "query-counterexample-before-20261103-2", "autostart-handshakes-after-20261103-2"):
    for path in sorted((ROOT / "artifacts/t039c" / folder).rglob("*.xml")):
        target = targeted / folder / path.name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, target)
assets = [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": digest(path)} for path in sorted(CANDIDATE.iterdir()) if path.is_file()]
review = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), **verified,
    "Candidate": str(CANDIDATE), "CommittedSourceEntriesMatched": 721, "WorkingSourceEntriesMatched": 721,
    "SourceChanges": changes, "ProductSourceChanges": [], "BuildWarnings": 0, "BuildErrors": 0,
    "FullTests": raw_totals, "TargetedRegressions": after_totals, "ControlledBeforeFailure": before_totals,
    "ReleaseRegressions": 16, "PreviousPublicFull": previous, "Assets": assets,
    "OriginalCiRun": 41461057191, "OriginalCiAttempt": 1, "OriginalCiPublicationSkipped": True,
    "RemoteBefore": remote, "PublicationAuthorized": False, "RealUiUpgradeVerified": False,
    "RestartCountVerified": False, "PostUpdateUninstallVerified": False}
(REPORT / "review.json").write_text(json.dumps(review, indent=2) + "\n", encoding="utf-8", newline="\n")
records = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": digest(path)} for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").write_text(json.dumps({"SourceCommit": SOURCE, "Files": records}, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({key: review[key] for key in ("Version", "SourceCommit", "SourceGitTree", "PublicAssets", "PayloadEntries", "CommittedSourceEntriesMatched", "FullTests", "FullSha256", "OuterSha256", "PublicationAuthorized")}, indent=2))
