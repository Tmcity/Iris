"""Independently bind the update-discovery fix, actual tests and candidate to official 0.2.7 history."""
from pathlib import Path
import hashlib
import importlib.util
import json
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path("F:/Iris")
WORK = ROOT / "artifacts/t039c/worktree-v028-update-source-20261105-1"
SOURCE = "2b2bbe70f765f9805471a0d555874f8e2383923a"
BASE = "fc44a3a78c2729c25dfb91296c51602b843a94f9"
TREE = "5e15542f003811e262e80737113ce11b3c8b8b83"
CANDIDATE = ROOT / "artifacts/t039c/release-0.2.8-public-base-candidate-1"
VALIDATION = ROOT / "artifacts/t039c/local-validation-v028-20261105-1"
PACKAGE = ROOT / "artifacts/t039c/package-0.2.8-public-base-candidate-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/next-source-v028-20261105-1"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(*args):
    return subprocess.check_output(["git", *args], cwd=WORK)


def reports(directory):
    totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
    tests = []
    for path in sorted(directory.glob("*.xml")):
        tree = ET.parse(path)
        for assembly in tree.iter("assembly"):
            assert int(assembly.get("errors", 0)) == 0
            for key in totals:
                totals[key] += int(assembly.get(key.lower(), 0))
        tests.extend(tree.iter("test"))
    return totals, tests


assert git("rev-parse", "HEAD").decode().strip() == SOURCE
spec = importlib.util.spec_from_file_location("candidate_manifest", WORK / "build/release_manifest.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
subprocess.run(["git", "diff", "--quiet", SOURCE, "--", *module.SOURCE_ROOTS], cwd=WORK, check=True)
assert not git("ls-files", "--others", "--exclude-standard", "--", *module.SOURCE_ROOTS).strip()
verified = module.verify_directory(CANDIDATE, "0.2.8")
assert verified["SourceCommit"] == SOURCE and verified["SourceGitTree"] == TREE
assert verified["PublicAssets"] == 19 and verified["PayloadEntries"] == 97
assert verified["FullSha256"] == "18ef33f21d4256acc6dc8029e52568e8b5215c26682a786cc7601d00962f3c05"
assert verified["OuterSha256"] == "e5bf367248446a63f484afaff41a34f6ff992bb75ddac7ad96c8b4d1c041f19d"
provenance = read(CANDIDATE / "release-provenance.json")
committed = {}
nodes = {}
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


assert tree_hash(nodes) == TREE
assert len(committed) == len(provenance["SourceFiles"]) == 721
assert {entry["Path"]: entry["GitBlob"] for entry in provenance["SourceFiles"]} == committed
for entry in provenance["SourceFiles"]:
    path = WORK / entry["Path"]
    assert path.stat().st_size == entry["WorkingBytes"] and sha(path) == entry["WorkingSha256"]
changes = git("diff", "--name-only", BASE, SOURCE).decode().splitlines()
assert set(changes) == {"src/Iris.App/Updates/UpdateRuntime.cs", "src/Iris.App/Updates/UpdateHttpDownloader.cs",
    "tests/Iris.App.Tests/Updates/UpdateHttpTests.cs", "build/release-plan.json", "docs/RELEASING.md"}
expected = {"Total": 3770, "Passed": 3768, "Failed": 0, "Skipped": 2}
summary = read(VALIDATION / "validation-summary.json")
totals, tests = reports(VALIDATION / "reports")
assert totals == summary["FullTests"] == expected and summary["SourceCommit"] == SOURCE
assert summary["BuildWarnings"] == summary["BuildErrors"] == 0
targeted = [test for test in tests if "UpdateHttpTests." in test.get("name", "")]
assert len(targeted) == 15 and all(test.get("result") == "Pass" for test in targeted)
assert all("error=183" in test.findtext("reason", "") for test in tests if test.get("result") == "Skip")
for command in summary["Commands"]:
    assert command["ExitCode"] == 0 and sha(VALIDATION / command["Log"]) == command["Sha256"]
package = read(PACKAGE / "package.command.json")
assert package["ExitCode"] == 0 and sha(PACKAGE / "package.log") == package["Sha256"]
assert Path(package["Command"][0]).as_posix().endswith("System32/WindowsPowerShell/v1.0/powershell.exe")
before, before_tests = reports(ROOT / "artifacts/t039c/rest-quota-counterexample-before-20261105-1/reports")
assert before == {"Total": 1, "Passed": 0, "Failed": 1, "Skipped": 0}
assert "RateLimited" not in before_tests[0].get("name", "") and "IrisException" in "".join(before_tests[0].find("failure").itertext())
after, _ = reports(ROOT / "artifacts/t039c/official-feed-after-20261105-3/reports")
assert after == {"Total": 15, "Passed": 15, "Failed": 0, "Skipped": 0}
feed = read(CANDIDATE / "releases.win.json")["Assets"]
previous = next(asset for asset in feed if asset["Version"] == "0.2.7" and asset["Type"] == "Full")
assert previous["Size"] == 64627391 and previous["SHA256"].lower() == "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8"
assert sha(CANDIDATE / previous["FileName"]) == sha(ROOT / "artifacts/t039c/public-0.2.7-download-20261105-1" / previous["FileName"])
assert any(asset["Version"] == "0.2.2" and asset["Type"] == "Full" for asset in feed)
assert (ROOT / "artifacts/t039c/latest-feed-v028-read-20261105-1.body").read_bytes() == (
    ROOT / "artifacts/t039c/public-0.2.7-download-20261105-1/releases.win.json").read_bytes()
assert not git("tag", "--list", "v0.2.8").strip()
REPORT.mkdir(exist_ok=False)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    target.open("xb").write(source.read_bytes())


for path in VALIDATION.rglob("*"):
    if path.is_file():
        copy(path, REPORT / "validation" / path.relative_to(VALIDATION))
for name in ("release-provenance.json", "SHA256.json", "payload-manifest.json", "staging-manifest.json", "releases.win.json",
             "assets.win.json", "RELEASES", "RELEASE_NOTES.md", "INSTALLATION.md", "package.log"):
    copy(CANDIDATE / name, REPORT / name)
copy(PACKAGE / "package.command.json", REPORT / "package.command.json")
for label in ("v028-rest-quota-counterexample-before-1", "v028-official-feed-after-1", "v028-official-feed-after-2",
              "v028-official-feed-after-3", "v028-full-official-feed-validation-1", "v028-package-public-base-candidate-1",
              "v028-official-latest-feed-public-read-1"):
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "raw-command-records" / (label + suffix))
for name in ("rest-quota-counterexample-before-20261105-1", "official-feed-after-20261105-1", "official-feed-after-20261105-2", "official-feed-after-20261105-3"):
    for path in (ROOT / "artifacts/t039c" / name).rglob("*.xml"):
        copy(path, REPORT / "targeted" / name / path.name)
for path in changes:
    raw = git("show", SOURCE + ":" + path)
    destination = REPORT / "source" / path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(raw)
assets = [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": sha(path)} for path in sorted(CANDIDATE.iterdir())]
review = {**verified, "CommittedSourceEntriesMatched": 721, "WorkingSourceEntriesMatched": 721,
          "SourceChanges": changes, "FullTests": totals, "TargetedUpdateHttpTests": after, "BeforeRateLimitFailure": before,
          "PreviousPublicFull": previous, "Assets": assets, "PublicationAuthorized": False,
          "RealUiUpgradeVerified": False, "OldInstalledDiscoveryImplementationStillUnchanged": True}
(REPORT / "review.json").write_text(json.dumps(review, indent=2) + "\n", encoding="utf-8", newline="\n")
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").write_text(json.dumps({"SourceCommit": SOURCE, "Files": files}, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({key: review[key] for key in ("Version", "SourceCommit", "SourceGitTree", "PublicAssets", "PayloadEntries",
    "FullTests", "FullSha256", "OuterSha256", "PublicationAuthorized")}, indent=2))
