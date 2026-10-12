"""Read-only audit of the existing T-039A candidate artifacts; does not run Iris or SDK."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess
import sys
import zipfile

sys.stdout.reconfigure(encoding="utf-8")
review = Path(__file__).resolve().parent
repository = review.parents[2]
evidence = repository / "tasks/done/T-039A.evidence"
artifacts = repository / "artifacts/t039a"
official = artifacts / "official-source/velopack-1.2.161"


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def inside(root, relative):
    candidate = (root / relative).resolve()
    candidate.relative_to(root.resolve())
    return candidate


report = {
    "phase": "pre-delivery candidate audit, not final acceptance",
    "startedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "executesApplicationOrSdk": False,
    "failures": [],
}
inputs = {}
for name in ["source-bindings.json", "package-inventory.json", "Probe/Program.cs", "Probe/Probe.csproj", "sdk-probes-2.log"]:
    inputs[name] = sha(evidence / name)
report["inputSha256"] = inputs

sources = json.loads((evidence / "source-bindings.json").read_text(encoding="utf-8-sig"))
source_results = []
for entry in sources["OfficialFiles"]:
    original = inside(official, entry["Path"])
    copy = inside(evidence / "official-source", entry["Path"])
    original_sha = sha(original)
    copied_sha = sha(copy)
    matches = original_sha == copied_sha == entry["Sha256"]
    source_results.append({"path": entry["Path"], "sha256": original_sha, "matchesRecordedAndArchived": matches})
    if not matches:
        report["failures"].append("official source mismatch: " + entry["Path"])
report["officialSources"] = source_results

package_results = []
for entry in json.loads((evidence / "package-inventory.json").read_text(encoding="utf-8-sig")):
    path = inside(artifacts, entry["Path"])
    result = {"path": entry["Path"], "bytes": path.stat().st_size, "sha256": sha(path)}
    result["matchesInventory"] = result["bytes"] == entry["Bytes"] and result["sha256"] == entry["Sha256"]
    if not result["matchesInventory"]:
        report["failures"].append("artifact mismatch: " + entry["Path"])
    if path.suffix == ".nupkg":
        with zipfile.ZipFile(path) as package:
            bad = package.testzip()
            result["zipCrcFailure"] = bad
            if bad is not None:
                report["failures"].append("package CRC failure: " + entry["Path"])
            result["entryNamesMatchInventory"] = package.namelist() == entry["Files"]
            if not result["entryNamesMatchInventory"]:
                report["failures"].append("package file list mismatch: " + entry["Path"])
            if path.name.endswith("-full.nupkg"):
                copied = {}
                for relative in ["uia/Iris.UiaHost.exe", "uia/Iris.UiaHost.runtimeconfig.json", "en-US/Iris.App.resources.dll", "en-US/Iris.Core.resources.dll"]:
                    copied[relative] = hashlib.sha256(package.read("lib/app/" + relative)).hexdigest() == sha(inside(artifacts / "payload-v1", relative))
                result["publishedHelperAndSatellitesMatch"] = copied
                if not all(copied.values()):
                    report["failures"].append("published file mismatch: " + entry["Path"])
    package_results.append(result)
report["packageArtifacts"] = package_results
report["inputsUnchangedAfterAudit"] = all(sha(evidence / name) == value for name, value in inputs.items())
if not report["inputsUnchangedAfterAudit"]:
    report["failures"].append("candidate input changed during audit")
report["productionDiffNamesAtAudit"] = subprocess.check_output(
    ["git", "diff", "--name-only", "HEAD", "--", "src", "tests", "specs/CONTRACTS.cs", "Directory.Packages.props", "build"],
    cwd=repository, text=True, encoding="utf-8"
).splitlines()
report["completedAt"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
result_path = review / "audit-pre-delivery-1.json"
with result_path.open("x", encoding="utf-8") as output:
    json.dump(report, output, ensure_ascii=False, indent=2)
print(json.dumps({"report": str(result_path), "officialSources": len(source_results), "packageArtifacts": len(package_results), "failures": report["failures"], "inputsUnchanged": report["inputsUnchangedAfterAudit"], "productionDiffNames": report["productionDiffNamesAtAudit"]}, ensure_ascii=False))
sys.exit(1 if report["failures"] else 0)
