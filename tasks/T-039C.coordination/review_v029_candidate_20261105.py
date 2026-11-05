"""Independently bind the final test-fixture correction and package to original failures and public history."""
from pathlib import Path
import datetime
import hashlib
import importlib.util
import json
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path("F:/Iris")
WORK = ROOT / "artifacts/t039c/worktree-v029-ci-fix-20261105-1"
SOURCE = "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"
BASE = "0c9b0a1881d07224597163b1e9b2f080ab60d434"
CANDIDATE = ROOT / "artifacts/t039c/release-0.2.9-public-base-candidate-1"
VALIDATION = ROOT / "artifacts/t039c/local-validation-v029-20261105-2"
PACKAGE = ROOT / "artifacts/t039c/package-0.2.9-public-base-candidate-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/next-source-v029-final-20261105-1"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(*args):
    return subprocess.check_output(["git", *args], cwd=WORK)


def reports(directory):
    totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
    rows = []
    for path in sorted(directory.glob("*.xml")):
        tree = ET.parse(path)
        for assembly in tree.iter("assembly"):
            assert int(assembly.get("errors", 0)) == int(assembly.get("not-run", 0)) == 0
            for key in totals:
                totals[key] += int(assembly.get(key.lower(), 0))
        rows.extend(tree.iter("test"))
    return totals, rows


assert git("rev-parse", "HEAD").decode().strip() == SOURCE
spec = importlib.util.spec_from_file_location("candidate_manifest", WORK / "build/release_manifest.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
subprocess.run(["git", "diff", "--quiet", SOURCE, "--", *module.SOURCE_ROOTS], cwd=WORK, check=True)
assert not git("ls-files", "--others", "--exclude-standard", "--", *module.SOURCE_ROOTS).strip()
changes = git("diff", "--name-only", BASE, SOURCE).decode().splitlines()
assert set(changes) == {"tests/Iris.App.Tests/TestSupport/WpfTestAssemblyFixture.cs",
                       "tests/Iris.App.Tests/Services/ThemeServicePreviewTests.cs", "build/release-plan.json", "docs/RELEASING.md"}
subprocess.run(["git", "diff", "--quiet", BASE, SOURCE, "--", "src", ".github", "Directory.Build.props",
                "Directory.Packages.props", "global.json"], cwd=WORK, check=True)
theme_path = "tests/Iris.App.Tests/Services/ThemeServicePreviewTests.cs"
before_theme, after_theme = (git("show", commit + ":" + theme_path) for commit in (BASE, SOURCE))
assert before_theme.split(b"public sealed class ThemeServicePreviewTests", 1)[1] == after_theme.split(b"public sealed class ThemeServicePreviewTests", 1)[1]
assert b'[CollectionDefinition("ThemeServiceDispatcher", DisableParallelization = true)]' in after_theme
assert b'[Collection("ThemeServiceDispatcher")]' in after_theme
fixture = git("show", SOURCE + ":tests/Iris.App.Tests/TestSupport/WpfTestAssemblyFixture.cs")
assert b"new(WpfTestRunner.RunAsync(() => Task.CompletedTask))" in fixture
assert b"TeardownAsync(TimeSpan.FromSeconds(5))" in fixture
assert not git("tag", "--list", "v0.2.9").strip()
assert git("rev-parse", "v0.2.8^{}").decode().strip() == BASE
verified = module.verify_directory(CANDIDATE, "0.2.9")
assert verified["SourceCommit"] == SOURCE and verified["PublicAssets"] == 19 and verified["PayloadEntries"] == 97
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
assert {entry["Path"]: entry["GitBlob"] for entry in provenance["SourceFiles"]} == committed
for entry in provenance["SourceFiles"]:
    path = WORK / entry["Path"]
    assert path.stat().st_size == entry["WorkingBytes"] and sha(path) == entry["WorkingSha256"]
expected = {"Total": 3771, "Passed": 3769, "Failed": 0, "Skipped": 2}
summary = read(VALIDATION / "validation-summary.json")
totals, tests = reports(VALIDATION / "reports")
assert totals == summary["FullTests"] == expected and summary["SourceCommit"] == SOURCE
assert summary["BuildWarnings"] == summary["BuildErrors"] == 0
assert all("error=183" in row.findtext("reason", "") for row in tests if row.get("result") == "Skip")
host_tests = [row for row in tests if row.get("type") == "Iris.App.Tests.Hosting.AppHostBuilderTests"]
theme_tests = [row for row in tests if row.get("type") == "Iris.App.Tests.Services.ThemeServicePreviewTests"]
http_tests = [row for row in tests if row.get("type") == "Iris.App.Tests.Updates.UpdateHttpTests"]
assert len(host_tests) == 13 and len(theme_tests) == 8 and len(http_tests) == 15
assert all(row.get("result") == "Pass" for row in host_tests + theme_tests + http_tests)
other_app_tests = [row for row in tests if row.get("type", "").startswith("Iris.App.Tests.") and row not in theme_tests]


def time(row, key):
    return datetime.datetime.fromisoformat(row.get(key))


assert all(time(theme, "finish-rtf") <= time(other, "start-rtf") or time(theme, "start-rtf") >= time(other, "finish-rtf")
           for theme in theme_tests for other in other_app_tests)
for command in summary["Commands"]:
    assert command["ExitCode"] == 0 and sha(VALIDATION / command["Log"]) == command["Sha256"]
package = read(PACKAGE / "package.command.json")
assert package["ExitCode"] == 0 and sha(PACKAGE / "package.log") == package["Sha256"]
assert Path(package["Command"][0]).as_posix().endswith("System32/WindowsPowerShell/v1.0/powershell.exe")
before, before_tests = reports(ROOT / "artifacts/t039c/host-dispatcher-counterexample-before-20261105-1/reports")
after, _ = reports(ROOT / "artifacts/t039c/host-dispatcher-counterexample-after-20261105-1/reports")
host_after, _ = reports(ROOT / "artifacts/t039c/host-composition-after-20261105-1/reports")
assert before == {"Total": 1, "Passed": 0, "Failed": 1, "Skipped": 0}
assert "SelectionIconController.StopCoreAsync" in "".join(before_tests[0].find("failure").itertext())
assert after == {"Total": 1, "Passed": 1, "Failed": 0, "Skipped": 0}
assert host_after == {"Total": 13, "Passed": 13, "Failed": 0, "Skipped": 0}
previous_dir = ROOT / "artifacts/t039c/public-0.2.7-download-20261105-1"
previous_verification = module.verify_directory(previous_dir, "0.2.7")
assert previous_verification["SourceCommit"] == "fc44a3a78c2729c25dfb91296c51602b843a94f9"
feed = read(CANDIDATE / "releases.win.json")["Assets"]
previous = next(asset for asset in feed if asset["Version"] == "0.2.7" and asset["Type"] == "Full")
assert previous["Size"] == 64627391 and previous["SHA256"].lower() == "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8"
assert sha(CANDIDATE / previous["FileName"]) == sha(previous_dir / previous["FileName"])
older = next(asset for asset in feed if asset["Version"] == "0.2.2" and asset["Type"] == "Full")
assert sha(CANDIDATE / older["FileName"]) == sha(previous_dir / older["FileName"])
assert not any(asset["Version"] == "0.2.8" for asset in feed)
linked = {}
for name in ("ci-v028-original-42015639513-1", "ci-v028-failure-analysis-20261105-1", "next-source-v029-local-failure-20261105-1"):
    directory = ROOT / "tasks/done/T-039C.evidence" / name
    binding = read(directory / "binding.json")
    for entry in binding["Files"]:
        path = directory / entry["Path"]
        assert path.stat().st_size == entry["Bytes"] and sha(path) == entry["Sha256"]
    linked[name] = {"BindingSha256": sha(directory / "binding.json"), "SourceCommit": binding["SourceCommit"]}
REPORT.mkdir(exist_ok=False)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    target.open("xb").write(source.read_bytes())


for path in sorted(VALIDATION.rglob("*")):
    if path.is_file():
        copy(path, REPORT / "validation" / path.relative_to(VALIDATION))
for name in ("release-provenance.json", "SHA256.json", "payload-manifest.json", "staging-manifest.json", "releases.win.json",
             "assets.win.json", "RELEASES", "RELEASE_NOTES.md", "INSTALLATION.md", "package.log"):
    copy(CANDIDATE / name, REPORT / name)
copy(PACKAGE / "package.command.json", REPORT / "package.command.json")
for label in ("v029-host-dispatcher-counterexample-before-1", "v029-host-dispatcher-counterexample-after-1",
              "v029-host-composition-after-1", "v029-final-source-commit-2", "v029-full-validation-after-2", "v029-package-public-base-candidate-1"):
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "raw-command-records" / (label + suffix))
for name in ("host-dispatcher-counterexample-before-20261105-1", "host-dispatcher-counterexample-after-20261105-1", "host-composition-after-20261105-1"):
    for path in (ROOT / "artifacts/t039c" / name).rglob("*.xml"):
        copy(path, REPORT / "targeted-host" / name / path.name)
for path in changes:
    raw = git("show", SOURCE + ":" + path)
    destination = REPORT / "source" / path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(raw)
assets = [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": sha(path)} for path in sorted(CANDIDATE.iterdir())]
review = {**verified, "CommittedSourceEntriesMatched": 721, "WorkingSourceEntriesMatched": 721, "SourceChanges": changes,
          "ProductionCodeChangedFromFailed028": False, "FullTests": totals, "HostTests": host_after, "ThemePreviewTests": 8,
          "ThemePreviewTestIntervalsDoNotOverlapOtherAppTests": True, "ThemePreviewTestBodiesUnchanged": True,
          "TargetedUpdateHttpTests": 15, "ColdHostBefore": before, "ColdHostAfter": after, "OriginalFailureBindings": linked,
          "PreviousPublicFull": previous, "OlderPublicFull": older, "Assets": assets,
          "PublicationAuthorized": False, "RealUiUpgradeVerified": False, "RestartCountVerified": False,
          "NextUserActionAfterOfficialVerification": "Manually install fixed 0.2.9; separately review and authorize its later UI upgrade target."}
(REPORT / "review.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(review, indent=2) + "\n")
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": SOURCE, "Files": files}, indent=2) + "\n")
print(json.dumps({key: review[key] for key in ("Version", "SourceCommit", "SourceGitTree", "PublicAssets", "PayloadEntries",
    "FullTests", "FullSha256", "OuterSha256", "ThemePreviewTestIntervalsDoNotOverlapOtherAppTests", "PublicationAuthorized")}, indent=2))
