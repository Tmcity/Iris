"""Bind the recovered A delivery to the already completed independent verification."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess

review = Path(__file__).resolve().parent
root = review.parents[2]
evidence = root / "tasks/done/T-039A.evidence"
artifacts = root / "artifacts/t039a"


def sha(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


inputs = read(review / "final-inputs-20261013.json")["files"]
changed = [item["path"] for item in inputs if sha(root / item["path"]) != item["sha256"]]
assert not changed, changed
prior = read(review / "ipc-early-close/candidate-binding.json")
ipc_source_matches = {path: sha(root / path) == value for path, value in prior["sources"].items() if path.startswith("src/")}
assert all(ipc_source_matches.values()), ipc_source_matches
ipc_assembly_matches = {Path(item["source"]).name: sha(Path(item["source"])) == item["sha256"] for item in prior["assemblies"]}
assert all(ipc_assembly_matches.values()), ipc_assembly_matches

sdk = read(review / "sdk-replay-source-binding.json")
sdk_matches = {Path(item["source"]).name: sha(Path(item["source"])) == item["sha256"] for item in sdk["files"]}
assert all(sdk_matches.values()), sdk_matches
bootstrap = read(review / "sdk-independent-bootstrap-result.json")
assert all(not case["errors"] for case in bootstrap["cases"])
assert read(review / "sdk-independent-replay-1.command.json")["exitCode"] == 0
for label in ["final-independent-release-20261013", "final-independent-full-20261013"]:
    assert read(review / (label + ".command.json"))["exitCode"] == 0

source_manifest = read(evidence / "source-bindings.json")
for item in source_manifest["OfficialFiles"]:
    assert sha(evidence / "official-source" / item["Path"]) == item["Sha256"], item["Path"]
    assert sha(artifacts / "official-source/velopack-1.2.161" / item["Path"]) == item["Sha256"], item["Path"]
packages = read(evidence / "package-inventory.json")
for item in packages:
    path = artifacts / item["Path"]
    assert path.stat().st_size == item["Bytes"] and sha(path) == item["Sha256"], item["Path"]

binary_suffixes = {".dll", ".exe", ".pdb", ".dmp", ".nupkg", ".zip"}
for folder in [review, evidence]:
    forbidden = [path.relative_to(root).as_posix() for path in folder.rglob("*") if path.is_file() and path.suffix.lower() in binary_suffixes]
    assert not forbidden, forbidden
changes = subprocess.check_output(["git", "diff", "--name-only", "HEAD", "--", "src", "tests", "specs/CONTRACTS.cs", "Directory.Build.props", "Directory.Packages.props", "global.json", "build"], cwd=root, text=True).splitlines()
allowed = {"src/Iris.Platform.Windows/NativeMethods.txt", "src/Iris.Platform.Windows/SystemIntegration/IpcPipeFactory.cs", "tests/Iris.Platform.Windows.Tests/SystemIntegration/NamedPipeIpcTests.cs"}
assert set(changes) == allowed, changes

artifacts_bound = []
for folder in [evidence, review]:
    for path in sorted(folder.rglob("*")):
        if path.is_file() and path.name != "final-binding-20261013.json":
            artifacts_bound.append({"path": path.relative_to(root).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)})
report = {
    "capturedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "sourceInputs": inputs,
    "changedDuringIndependentBuildAndTest": changed,
    "scopedTrackedChanges": changes,
    "scopedNewTest": "tests/Iris.Platform.Windows.Tests/SystemIntegration/IpcEarlyDisconnectTests.cs",
    "independentIpcSourceMatches": ipc_source_matches,
    "independentIpcAssemblyMatches": ipc_assembly_matches,
    "independentSdkSourceMatches": sdk_matches,
    "independentBootstrapCases": len(bootstrap["cases"]),
    "fixedOfficialSourceCount": len(source_manifest["OfficialFiles"]),
    "packageArtifactCount": len(packages),
    "evidenceFiles": artifacts_bound,
}
with (review / "final-binding-20261013.json").open("x", encoding="utf-8") as output:
    json.dump(report, output, indent=2)
print(json.dumps({"sourceInputsUnchanged": len(inputs), "officialSourcesMatched": report["fixedOfficialSourceCount"], "packageArtifactsMatched": report["packageArtifactCount"], "bootstrapCasesPassed": report["independentBootstrapCases"], "ipcSourceAndAssembliesMatch": True, "sdkSourcesMatch": True, "evidenceFilesBound": len(artifacts_bound), "forbiddenBinariesInEvidence": 0}))
