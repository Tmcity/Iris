"""Bind the UI-acceptance release candidate to unchanged production code and actual public 0.2.9 history."""
from pathlib import Path
import hashlib
import importlib.util
import json
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path("F:/Iris")
WORK = ROOT / "artifacts/t039c/worktree-v0210-ui-acceptance-source-20261105-1"
SOURCE = "227b3d88938543766a27d86a94ade52f9e5b89b7"
BASE = "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"
CANDIDATE = ROOT / "artifacts/t039c/release-0.2.10-public-base-candidate-1"
VALIDATION = ROOT / "artifacts/t039c/local-validation-v0210-20261105-1"
PACKAGE = ROOT / "artifacts/t039c/package-0.2.10-public-base-candidate-1"
PREVIOUS = ROOT / "artifacts/t039c/public-0.2.9-download-20261105-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/next-source-v0210-ui-acceptance-20261105-1"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(*args):
    return subprocess.check_output(["git", *args], cwd=WORK)


assert git("rev-parse", "HEAD").decode().strip() == SOURCE
spec = importlib.util.spec_from_file_location("candidate_manifest", WORK / "build/release_manifest.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
subprocess.run(["git", "diff", "--quiet", SOURCE, "--", *module.SOURCE_ROOTS], cwd=WORK, check=True)
assert not git("ls-files", "--others", "--exclude-standard", "--", *module.SOURCE_ROOTS).strip()
changes = git("diff", "--name-only", BASE, SOURCE).decode().splitlines()
assert set(changes) == {"build/release-plan.json", "docs/RELEASING.md"}
subprocess.run(["git", "diff", "--quiet", BASE, SOURCE, "--", "src", "tests", ".github", "Directory.Build.props", "Directory.Packages.props", "global.json"], cwd=WORK, check=True)
assert not git("tag", "--list", "v0.2.10").strip()
verified = module.verify_directory(CANDIDATE, "0.2.10")
assert verified["SourceCommit"] == SOURCE and verified["PublicAssets"] == 20 and verified["PayloadEntries"] == 97
provenance = read(CANDIDATE / "release-provenance.json")
committed, nodes = {}, {}
for row in git("ls-tree", "-r", "-z", SOURCE, "--", *module.SOURCE_ROOTS).decode().strip("\0").split("\0"):
    metadata, path = row.split("\t")
    mode, kind, blob = metadata.split()
    assert (mode, kind) == ("100644", "blob")
    committed[path] = blob
    node = nodes
    parts = path.split("/")
    for part in parts[:-1]:
        node = node.setdefault(part, {})
    node[parts[-1]] = blob


def tree_hash(node):
    raw = b""
    for name, value in sorted(node.items(), key=lambda pair: pair[0].encode() + (b"/" if isinstance(pair[1], dict) else b"\0")):
        mode, blob = (b"40000", tree_hash(value)) if isinstance(value, dict) else (b"100644", value)
        raw += mode + b" " + name.encode() + b"\0" + bytes.fromhex(blob)
    return hashlib.sha1(b"tree " + str(len(raw)).encode() + b"\0" + raw).hexdigest()


assert tree_hash(nodes) == verified["SourceGitTree"]
assert len(committed) == len(provenance["SourceFiles"]) == 721
assert {row["Path"]: row["GitBlob"] for row in provenance["SourceFiles"]} == committed
for entry in provenance["SourceFiles"]:
    path = WORK / entry["Path"]
    assert path.stat().st_size == entry["WorkingBytes"] and sha(path) == entry["WorkingSha256"]
summary = read(VALIDATION / "validation-summary.json")
totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
tests = []
for path in sorted((VALIDATION / "reports").glob("*.xml")):
    tree = ET.parse(path)
    for assembly in tree.iter("assembly"):
        assert int(assembly.get("errors", 0)) == int(assembly.get("not-run", 0)) == 0
        for key in totals:
            totals[key] += int(assembly.get(key.lower(), 0))
    tests.extend(tree.iter("test"))
assert totals == summary["FullTests"] == {"Total": 3771, "Passed": 3769, "Failed": 0, "Skipped": 2}
assert summary["SourceCommit"] == SOURCE and summary["BuildWarnings"] == summary["BuildErrors"] == 0
assert all("error=183" in row.findtext("reason", "") for row in tests if row.get("result") == "Skip")
assert len([row for row in tests if row.get("type") == "Iris.App.Tests.Hosting.AppHostBuilderTests"]) == 13
for record in summary["Commands"]:
    assert record["ExitCode"] == 0 and sha(VALIDATION / record["Log"]) == record["Sha256"]
package = read(PACKAGE / "package.command.json")
assert package["ExitCode"] == 0 and sha(PACKAGE / "package.log") == package["Sha256"]
assert Path(package["Command"][0]).as_posix().endswith("System32/WindowsPowerShell/v1.0/powershell.exe")
previous_verification = module.verify_directory(PREVIOUS, "0.2.9")
assert previous_verification["SourceCommit"] == BASE and previous_verification["PublicAssets"] == 19
feed = read(CANDIDATE / "releases.win.json")["Assets"]
history = []
for version in ("0.2.2", "0.2.7", "0.2.9"):
    asset = next(row for row in feed if row["Version"] == version and row["Type"] == "Full")
    assert (CANDIDATE / asset["FileName"]).stat().st_size == asset["Size"]
    assert sha(CANDIDATE / asset["FileName"]) == sha(PREVIOUS / asset["FileName"]) == asset["SHA256"].lower()
    history.append(asset)
previous_full = next(row for row in history if row["Version"] == "0.2.9")
assert previous_full["Size"] == 64628500 and previous_full["SHA256"].lower() == "42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e"
api = read(ROOT / "tasks/done/T-039C.evidence/ci-v029-api-supplement-20261105-1/binding.json")
assert api["OfficialApiArtifactDigestsVerified"] and api["All19PublicAssetApiDigestsVerified"] and api["ReleaseId"] == 435193220
REPORT.mkdir(exist_ok=False)


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(source.read_bytes())


for path in sorted(VALIDATION.rglob("*")):
    if path.is_file():
        copy(path, REPORT / "validation" / path.relative_to(VALIDATION))
for name in ("release-provenance.json", "SHA256.json", "payload-manifest.json", "staging-manifest.json", "releases.win.json", "assets.win.json", "RELEASES", "RELEASE_NOTES.md", "INSTALLATION.md", "package.log"):
    copy(CANDIDATE / name, REPORT / name)
copy(PACKAGE / "package.command.json", REPORT / "package.command.json")
for label in ("v0210-ui-acceptance-worktree-create-1", "v0210-source-stage-1", "v0210-source-commit-1", "v0210-full-validation-1", "v0210-package-public-base-candidate-1"):
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "raw-command-records" / (label + suffix))
for name in changes:
    destination = REPORT / "source" / name
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(git("show", SOURCE + ":" + name))
assets = [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": sha(path)} for path in sorted(CANDIDATE.iterdir())]
review = {**verified, "FullTests": totals, "CommittedSourceEntriesMatched": 721, "WorkingSourceEntriesMatched": 721,
          "SourceChanges": changes, "ProductionAndTestSourcesUnchangedFromPublished029": True,
          "RetainedPublishedHistory": history, "PreviousOfficialReleaseId": 435193220,
          "Assets": assets, "PublicationAuthorized": False, "Manual029BaselineFinalizationPending": True,
          "RealUiUpgradeVerified": False, "PostUpdateUninstallVerified": False}
(REPORT / "review.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(review, indent=2) + "\n")
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)} for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": SOURCE, "Files": files}, indent=2) + "\n")
print(json.dumps({key: review[key] for key in ("Version", "SourceCommit", "SourceGitTree", "PublicAssets", "PayloadEntries", "FullTests", "FullSha256", "OuterSha256", "PublicationAuthorized")}, indent=2))
