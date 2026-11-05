"""Freeze the failed published tag attempt and the first local successor failure."""
from pathlib import Path
import hashlib
import html
import json
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path("F:/Iris")
CI_SOURCE = "0c9b0a1881d07224597163b1e9b2f080ab60d434"
FIRST_SOURCE = "f2526abcdae9d2ed587fa8579a1a956f929d583d"
ORIGINAL = ROOT / "tasks/done/T-039C.evidence/ci-v028-original-42015639513-1"
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.8-20261105-1"
CI_REPORT = ROOT / "tasks/done/T-039C.evidence/ci-v028-failure-analysis-20261105-1"
LOCAL_REPORT = ROOT / "tasks/done/T-039C.evidence/next-source-v029-local-failure-20261105-1"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(source.read_bytes())


def copy_tree(source, destination):
    for path in sorted(source.rglob("*")):
        if path.is_file():
            assert not path.is_symlink()
            copy(path, destination / path.relative_to(source))


def tests(directory):
    totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
    rows = []
    for path in sorted(directory.rglob("*.xml")):
        tree = ET.parse(path)
        for assembly in tree.iter("assembly"):
            assert int(assembly.get("errors", 0)) == int(assembly.get("not-run", 0)) == 0
            for key in totals:
                totals[key] += int(assembly.get(key.lower(), 0))
        rows.extend(tree.iter("test"))
    return totals, rows


def finish(directory, record):
    files = [{"Path": path.relative_to(directory).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
             for path in sorted(directory.rglob("*")) if path.is_file()]
    (directory / "binding.json").open("x", encoding="utf-8", newline="\n").write(
        json.dumps({**record, "Files": files}, ensure_ascii=False, indent=2) + "\n")


original = read(ORIGINAL / "binding.json")
assert original["SourceCommit"] == CI_SOURCE and original["RunId"] == 42015639513 and original["RunAttempt"] == 1
assert sha(ORIGINAL / "original-artifact.zip") == original["OfficialPageDigest"]
for entry in original["Files"]:
    path = ORIGINAL / entry["Path"]
    assert path.stat().st_size == entry["Bytes"] and sha(path) == entry["Sha256"]
ci_totals, ci_tests = tests(ORIGINAL / "original-files")
assert ci_totals == original["Totals"] == {"Total": 3771, "Passed": 3769, "Failed": 2, "Skipped": 0}
failures = [row.get("name") for row in ci_tests if row.get("result") == "Fail"]
assert set(failures) == {row["Name"] for row in original["Failures"]}
state = read(PUBLICATION / "state.json")
assert state["Stage"] == "ci-validation-failed-publication-skipped" and state["ReleasePublic"] is False
assert state["SourceCommit"] == CI_SOURCE and state["CiRunAttempt"] == 1 and state["CiConclusion"] == "failure"

CI_REPORT.mkdir(exist_ok=False)
copy(ORIGINAL / "binding.json", CI_REPORT / "original-ci-binding.json")
copy(PUBLICATION / "state.json", CI_REPORT / "publication-state.json")
copy(PUBLICATION / "user-approval.json", CI_REPORT / "user-approval.json")
pages = {}
for name in ("20261105T111426616159Z-web-run", "20261105T111710122482Z-web-job", "20261105T111710133995Z-web-job"):
    folder = PUBLICATION / "observations" / name
    observation = read(folder / "observation.json")
    assert observation["HttpStatus"] == "200" and observation["AuthorizationSent"] is False
    assert observation["BodySha256"] == sha(folder / "response.body")
    copy_tree(folder, CI_REPORT / "official-public-pages" / name)
    pages[name] = (folder / "response.body").read_text(encoding="utf-8")
run_page = pages["20261105T111426616159Z-web-run"]
assert CI_SOURCE in run_page and original["OfficialPageDigest"] in run_page
job_page = pages["20261105T111710122482Z-web-job"]
steps = []
for tag in re.findall(r"<check-step\b[^>]*>", job_page, flags=re.S):
    attributes = {name: html.unescape(value) for name, value in re.findall(r'([\w-]+)="([^"]*)"', tag)}
    steps.append({"Name": attributes.get("data-name"), "Conclusion": attributes.get("data-conclusion")})
assert any("Release build, full tests and offline publishing regressions" == step["Name"] and step["Conclusion"] == "failure" for step in steps)
assert any("Build and bind exact public assets with Windows PowerShell 5.1" == step["Name"] and step["Conclusion"] == "skipped" for step in steps)
assert "skipped" in pages["20261105T111710133995Z-web-job"].lower()
copy_tree(PUBLICATION / "observations/20261105T120020973894Z-artifacts", CI_REPORT / "official-api-artifact-quota-response")
for path in ("tests/Iris.App.Tests/TestSupport/WpfTestAssemblyFixture.cs", "tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs",
             "tests/Iris.App.Tests/Hosting/AppHostBuilderTests.cs", "src/Iris.App/Hosting/AppHostBuilder.cs",
             "src/Iris.App/Selection/SelectionIconController.cs"):
    raw = subprocess.check_output(["git", "show", CI_SOURCE + ":" + path], cwd=ROOT)
    destination = CI_REPORT / "failed-source" / path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(raw)
finish(CI_REPORT, {"SourceCommit": CI_SOURCE, "Tag": "v0.2.8", "RunId": 42015639513, "RunAttempt": 1,
                  "TagObject": state["TagObject"], "OriginalArtifactDigestMatchedOfficialPage": True,
                  "OfficialRestApiArtifactMetadataVerified": False, "FullTests": ci_totals, "Failures": failures,
                  "PackageJobId": 127205378609, "PackageSteps": steps, "PublishJobId": 127208381501,
                  "PublishConclusion": "skipped", "ReleasePublic": False, "FailedTagOrRunRewritten": False})

validation = ROOT / "artifacts/t039c/local-validation-v029-20261105-1"
local_totals, local_tests = tests(validation / "reports")
assert local_totals == {"Total": 3771, "Passed": 3768, "Failed": 1, "Skipped": 2}
local_failures = [row for row in local_tests if row.get("result") == "Fail"]
assert local_failures[0].get("name") == "Iris.App.Tests.Services.ThemeServicePreviewTests.VisualThemeApplier_IsInvoked_WhenThemeChanges"
assert "TimeoutException" in "".join(local_failures[0].find("failure").itertext())
assert read(validation / "full-tests.command.json")["ExitCode"] == 2
probe = ROOT / "artifacts/t039c/theme-dispatcher-interference-before-20261105-2"
probe_totals, probe_tests = tests(probe / "reports")
assert probe_totals == {"Total": 1, "Passed": 0, "Failed": 1, "Skipped": 0}
assert "ThemeServicePreviewTests.VisualThemeApplier_IsInvoked_WhenThemeChanges" in "".join(probe_tests[0].find("failure").itertext())
after = ROOT / "artifacts/t039c/theme-preview-after-20261105-2"
after_totals, _ = tests(after / "reports")
assert after_totals == {"Total": 8, "Passed": 8, "Failed": 0, "Skipped": 0}
LOCAL_REPORT.mkdir(exist_ok=False)
copy_tree(validation, LOCAL_REPORT / "first-full-validation")
copy_tree(probe, LOCAL_REPORT / "controlled-shared-dispatcher-interference")
copy_tree(after, LOCAL_REPORT / "theme-preview-after-isolation")
for path in ("tests/Iris.App.Tests/Services/ThemeServicePreviewTests.cs", "tests/Iris.App.Tests/TestSupport/WpfTestAssemblyFixture.cs",
             "src/Iris.App/Services/ThemeService.cs", "docs/RELEASING.md", "build/release-plan.json"):
    raw = subprocess.check_output(["git", "show", FIRST_SOURCE + ":" + path], cwd=ROOT)
    destination = LOCAL_REPORT / "failed-source" / path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(raw)
for label in ("v029-source-commit-1", "v029-full-validation-after-1", "v029-theme-dispatcher-interference-before-1",
              "v029-theme-dispatcher-interference-before-2", "v029-theme-preview-after-1", "v029-theme-preview-after-2"):
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), LOCAL_REPORT / "raw-command-records" / (label + suffix))
for name in ("theme-interference-probe-before-cancellation-token-20261105.cs.txt", "theme-preview-before-definition-name-20261105.cs.txt"):
    copy(ROOT / "tasks/T-039C.coordination" / name, LOCAL_REPORT / "diagnostic-build-failures" / name)
finish(LOCAL_REPORT, {"SourceCommit": FIRST_SOURCE, "FullTests": local_totals,
                     "Failure": local_failures[0].attrib, "ControlledSharedDispatcherCounterexample": probe_totals,
                     "ThemePreviewAfterIsolation": after_totals, "ProductionThemeImplementationChanged": False,
                     "OriginalTimeoutsAndAssertionsChanged": False, "SuccessfulFinalFullValidationInferred": False})
print(json.dumps({"CiOriginal": ci_totals, "FirstLocalFull": local_totals, "ControlledCounterexample": probe_totals,
                  "ThemePreviewAfterIsolation": after_totals, "Reports": [str(CI_REPORT), str(LOCAL_REPORT)]}, indent=2))
