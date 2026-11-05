"""Freeze the original 0.2.6 CI and first 0.2.7 local failure without replacing either."""
from pathlib import Path, PurePosixPath
import datetime
import hashlib
import json
import stat
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path("F:/Iris")
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.6-20261103-1"
SOURCE = "c32fcb283e417ab11715c46207fdc8cfde4baedf"
TAG_OBJECT = "dc8caf8b0c6501639c61dc26801595340386a4cb"
RUN_ID = 41663111425
OBSERVATIONS = {
    "artifacts": "20261105T044408291964Z-artifacts",
    "run": "20261105T044425936952Z-run",
    "jobs": "20261105T044425938991Z-jobs",
    "release": "20261105T044425938991Z-release",
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.open("x", encoding="utf-8", newline="\n").write(json.dumps(value, indent=2) + "\n")


def observation(resource):
    directory = PUBLICATION / "observations" / OBSERVATIONS[resource]
    meta = json.loads((directory / "observation.json").read_bytes())
    data = (directory / "response.body").read_bytes()
    assert meta["AuthorizationSent"] is False and meta["ExitCode"] == 0
    assert sha(data) == meta["BodySha256"] and len(data) == meta["BodyBytes"]
    assert meta["HttpStatus"] == ("404" if resource == "release" else "200")
    return directory, json.loads(data)


def summarize(paths):
    totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
    failures, skipped = [], []
    for path in paths:
        tree = ET.parse(path)
        for assembly in tree.iter("assembly"):
            for key in totals:
                totals[key] += int(assembly.get(key.lower(), 0))
            assert int(assembly.get("errors", 0)) == 0
        for test in tree.iter("test"):
            if test.get("result") == "Fail":
                failures.append({"Name": test.get("name"),
                                 "Message": "\n".join(node.text or "" for node in test.iter("message")),
                                 "StackTrace": "\n".join(node.text or "" for node in test.iter("stack-trace"))})
            elif test.get("result") == "Skip":
                skipped.append({"Name": test.get("name"), "Reason": test.findtext("reason")})
    return totals, failures, skipped


def copy_file(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    data = source.read_bytes()
    destination.open("xb").write(data)
    assert destination.read_bytes() == data
    return {"Path": destination.relative_to(ROOT).as_posix(), "Bytes": len(data), "Sha256": sha(data)}


api_dir, api = observation("artifacts")
run_dir, run = observation("run")
jobs_dir, jobs = observation("jobs")
release_dir, release = observation("release")
assert run["id"] == RUN_ID and run["head_sha"] == SOURCE
assert run["head_branch"] == "v0.2.6" and run["run_attempt"] == 1
assert run["status"] == "completed" and run["conclusion"] == "failure"
package = next(job for job in jobs["jobs"] if job["name"] == "package")
publish = next(job for job in jobs["jobs"] if job["name"] == "publish")
assert package["id"] == 126011210543 and package["conclusion"] == "failure"
assert publish["conclusion"] == "skipped"
assert next(step for step in package["steps"] if step["name"].startswith("Release build,"))["conclusion"] == "failure"
assert next(step for step in package["steps"] if step["name"].startswith("Build and bind"))["conclusion"] == "skipped"
artifact = next(item for item in api["artifacts"] if item["name"] == "iris-validation-41663111425-1")
assert artifact["id"] == 13244736238 and artifact["expired"] is False
assert artifact["workflow_run"]["id"] == RUN_ID and artifact["workflow_run"]["head_sha"] == SOURCE
archive = ROOT / "artifacts/t039c/ci-v026-original-41663111425-1.zip"
raw = archive.read_bytes()
assert len(raw) == artifact["size_in_bytes"] == 506283
assert artifact["digest"] == "sha256:" + sha(raw) == "sha256:0eddaa74a4ecad4296c5c17a74ae1079394ec56a06ee892046ad68afd046bc7b"

report = ROOT / "tasks/done/T-039C.evidence/ci-v026-original-41663111425-1"
report.mkdir(exist_ok=False)
provenance = [copy_file(archive, report / "original-artifact.zip")]
for resource, directory in (("artifacts", api_dir), ("run", run_dir), ("jobs", jobs_dir), ("release", release_dir)):
    for name in ("response.body", "response.headers", "observation.json", "stderr.log"):
        provenance.append(copy_file(directory / name, report / "official-observations" / resource / name))
for name in ("v026-actions-public-20261103-1.html", "v026-job-126011210543-public-20261103-1.html",
             "v026-run-41663111425-public-20261103-1.html", "v026-run-41663111425-public-20261103-2.html"):
    provenance.append(copy_file(ROOT / "artifacts/t039c" / name, report / "public-pages" / name))
files, seen, expanded_bytes = [], set(), 0
with zipfile.ZipFile(archive) as zipped:
    assert len(zipped.infolist()) <= 2000
    for item in zipped.infolist():
        if item.is_dir():
            continue
        path = PurePosixPath(item.filename.replace("\\", "/"))
        assert not path.is_absolute() and ".." not in path.parts and ":" not in str(path)
        assert stat.S_IFMT(item.external_attr >> 16) != stat.S_IFLNK
        assert path.suffix in {".json", ".xml", ".log", ".png", ".txt"}
        assert item.file_size <= 16 * 1024 * 1024
        expanded_bytes += item.file_size
        assert expanded_bytes <= 64 * 1024 * 1024
        destination = (report / "original-files").joinpath(*path.parts).resolve()
        assert destination.is_relative_to(report.resolve())
        identity = str(destination).casefold()
        assert identity not in seen
        seen.add(identity)
        destination.parent.mkdir(parents=True, exist_ok=True)
        data = zipped.read(item)
        destination.open("xb").write(data)
        files.append({"Path": destination.relative_to(ROOT).as_posix(), "ArchivePath": item.filename,
                      "Bytes": len(data), "Sha256": sha(data)})
totals, failures, skipped = summarize(report.rglob("*.xml"))
assert totals == {"Total": 3758, "Passed": 3756, "Failed": 1, "Skipped": 1}
assert len(files) == 51 and len(failures) == 1 and failures[0]["Name"].endswith(
    "ReadAsync_WhenRealRunnerWrappingFails_WaitsForActualStderrDrain(stop: True)")
assert "Non-static field requires a target" in failures[0]["Message"]
state_path = PUBLICATION / "state.json"
provenance.append(copy_file(state_path, report / "publication-state-before-final-ci.json"))
write_json(report / "binding.json", {
    "ArchivedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "RunId": RUN_ID, "RunAttempt": 1, "SourceCommit": SOURCE, "Tag": "v0.2.6", "TagObject": TAG_OBJECT,
    "ArtifactId": artifact["id"], "ArchiveBytes": len(raw), "OfficialDigest": artifact["digest"],
    "ExactArchiveMatchedGitHubApiDigest": True, "FullTests": totals, "Failures": failures,
    "Skipped": skipped, "Files": files, "ProvenanceFiles": provenance,
})
state = json.loads(state_path.read_bytes())
assert state["Tag"] == "v0.2.6" and state["SourceCommit"] == SOURCE and state["TagObject"] == TAG_OBJECT
assert state["PublicationAuthorized"] and state["TagRemoteVerified"]
state.update(Stage="ci-validation-failed-publication-skipped", ReleasePublic=False, CiRunId=RUN_ID,
             CiRunAttempt=1, OriginalArtifactDigestVerified=True,
             Failure={"Type": "CiValidationFailure", "Tests": [failure["Name"] for failure in failures]},
             FinalObservationUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")

local_source = "1f4a0f393dc68cb5904828fe5f186af37cec6015"
local_base = ROOT / "artifacts/t039c/local-validation-v027-20261104-1"
local = ROOT / "tasks/done/T-039C.evidence/v027-local-before-fix-20261105-1"
local.mkdir(exist_ok=False)
local_files = []
for path in sorted(local_base.rglob("*")):
    if path.is_file():
        assert not path.is_symlink()
        local_files.append(copy_file(path, local / "original-validation" / path.relative_to(local_base)))
local_totals, local_failures, local_skipped = summarize((local / "original-validation/reports").glob("*.xml"))
assert local_totals == {"Total": 3759, "Passed": 3756, "Failed": 1, "Skipped": 2}
assert len(local_failures) == 1 and local_failures[0]["Name"].endswith(
    "H13_OnHotkeyPressed_DoesNotBlockWndProc_RunsOnThreadPool")
for label, expected in (("release-build", 0), ("full-tests", 2), ("release-manifest-tests", 0)):
    command = json.loads((local_base / (label + ".command.json")).read_bytes())
    assert command["ExitCode"] == expected
    assert command["Sha256"] == sha((local_base / command["Log"]).read_bytes())
snapshots = []
for path in ("tests/Iris.App.Tests/Hosting/HotkeyCoordinatorTests.cs", "src/Iris.App/Hosting/HotkeyCoordinator.cs",
             "tests/Iris.App.Tests/TestSupport/FakeNativeHotkeyAdapter.cs"):
    data = subprocess.check_output(["git", "show", local_source + ":" + path], cwd=ROOT)
    destination = local / "source" / path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(data)
    snapshots.append({"Path": path, "Bytes": len(data), "Sha256": sha(data)})
write_json(local / "binding.json", {
    "SourceCommit": local_source, "ValidationSource": str(local_base), "FullTests": local_totals,
    "Failures": local_failures, "Skipped": local_skipped, "Files": local_files, "FrozenSources": snapshots,
})
print(json.dumps({"OfficialCiDigestMatched": True, "CiOriginalFiles": len(files), "CiFullTests": totals,
                  "LocalOriginalFiles": len(local_files), "LocalFullTests": local_totals}, indent=2))
