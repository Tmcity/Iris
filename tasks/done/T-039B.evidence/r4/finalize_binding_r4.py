from pathlib import Path
import json
import re
import subprocess
import sys
from inspect_delivery import file_record, json_read, sha, test_reports

sys.stdout.reconfigure(encoding="utf-8")
evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
active = json_read(evidence / "active_round.json")
source = json_read(evidence / active["SourceBinding"])
prior_independent_source = json_read(evidence / "tested-source-binding-2.json")
review = root / "tasks/done/T-039B.review/acceptance-review-4"
independent_runtime = json_read(review / "independent-runtime-candidate2-binding.json")
independent_bytes = json_read(review / "candidate-check-2.json")
independent_visual = json_read(review / "recovery-ui/after-observation-1/reviewed.json")
assert independent_runtime["sourceBindingSha256"] == sha(evidence / "tested-source-binding-2.json")
assert independent_runtime["sourceGitTree"] == prior_independent_source["GitTree"]
assert independent_runtime["allFrozenInputsUnchanged"] and not independent_runtime["inputErrors"]
assert independent_bytes["sourceBindingSha256"] == sha(evidence / "tested-source-binding-2.json")
assert independent_bytes["passed"] == independent_bytes["checks"] and not independent_bytes["failures"]
assert independent_visual["sourceGitTree"] == prior_independent_source["GitTree"]
independent_files = [review / name for name in ["independent-runtime-candidate2-1.log", "independent-runtime-candidate2-1.command.json",
    "independent-runtime-candidate2-binding.json", "candidate-check-2.json", "candidate-check-2.log", "candidate-check-2.command.json",
    "recovery-ui/after-observation-1/reviewed.json"]]
independent_files += sorted((review / "independent-runtime-1-reports").glob("*.xml"))
independent_files += [root / item["path"] for item in independent_runtime["workerResults"]]
current_independent_runtime = json_read(review / "independent-runtime-candidate3-binding.json")
current_independent_bytes = json_read(review / "candidate-check-3.json")
current_independent_visual = json_read(review / "recovery-ui/candidate3-visual-carry-forward.json")
current_independent_localization = json_read(review / "localization-after-1/verification.json")
assert current_independent_runtime["sourceBindingSha256"] == sha(evidence / active["SourceBinding"])
assert current_independent_runtime["sourceGitTree"] == source["GitTree"] and current_independent_runtime["allFrozenInputsUnchanged"]
assert current_independent_bytes["sourceBindingSha256"] == sha(evidence / active["SourceBinding"])
assert current_independent_bytes["checks"] == current_independent_bytes["passed"] and not current_independent_bytes["failures"]
assert current_independent_visual["sourceGitTree"] == source["GitTree"]
assert current_independent_visual["appDllSha256"].lower() == next(item["Sha256"] for item in source["TestedCandidateFiles"] if item["Path"] == "Iris.App.dll").lower()
for frame in current_independent_visual["frames"]:
    assert frame["identicalToIndependentlyViewedFrame"]
    assert sha(root / frame["image"]) == sha(root / frame["priorViewedImage"]) == frame["sha256"].lower()
assert current_independent_localization["unapproved"] == current_independent_localization["resourceErrors"] == 0
assert current_independent_localization["exceptionManifestSha256"] == sha(root / "build/localization/exceptions.json")
for item in current_independent_runtime["workerResults"]:
    assert sha(root / item["path"]) == item["sha256"]
    assert item["result"]["Failure"] is None and item["result"]["NativeCalls"] == item["result"]["Restarts"] == 0 and not item["result"]["GateBlocked"]
current_independent_files = [review / name for name in ["independent-runtime-candidate3-1.log", "independent-runtime-candidate3-1.command.json",
    "independent-runtime-candidate3-binding.json", "candidate-check-3.json", "candidate-check-3.log", "candidate-check-3.command.json",
    "recovery-ui/candidate3-visual-carry-forward.json", "localization-after-1/verification.json", "localization-after-1/README.md",
    "localization-after-1/audit-result.json", "localization-inventory-after-independent-1.log", "localization-inventory-after-independent-1.command.json",
    "localization-catalog-after-independent-1.log", "localization-catalog-after-independent-1.command.json"]]
current_independent_files += sorted((review / "independent-runtime-2-reports").glob("*.xml"))
current_independent_files += [root / item["path"] for item in current_independent_runtime["workerResults"]]
current_independent_files += [review / name for name in ["host-isolation-enabled-1.json", "sandbox-list-enabled-1.log", "sandbox-list-enabled-1.command.json"]]
current_guest_review = json_read(review / "guests-independent-final8-1.json")
assert current_guest_review["packageTag"] == active["PackageTag"]
assert current_guest_review["checks"] == current_guest_review["passed"] and not current_guest_review["failures"]
current_independent_files += [review / name for name in ["guests-independent-final8-1.json", "guests-independent-final8-1.command.json"]]
complete = (evidence / active["GuestInspection"]).exists()
if complete:
    viewed_guests = json_read(evidence / "reviewed-guests-r4-1.json")
    assert len(viewed_guests["Frames"]) == 2
    for frame in viewed_guests["Frames"]:
        assert file_record(root / frame["Image"]["Path"]) == frame["Image"]
inspection = json_read(evidence / (active["GuestInspection"] if complete else active["PackageInspection"]))
runtime_relocation = json_read(evidence / "runtime-binary-relocation-1.json")
runtime_artifacts = []
for entry in runtime_relocation["Files"]:
    assert not (root / entry["PreviousEvidenceDuplicate"]["Path"]).exists()
    for name in ["OriginalArtifactOutputRetained", "PreservedArtifactCopy"]:
        record = entry[name]
        assert file_record(root / record["Path"]) == record
        runtime_artifacts.append(record)
if complete:
    for scenario in current_guest_review["scenarios"]:
        guest = next(g for g in inspection["SandboxVerificationR4"] if g["SandboxId"] == scenario["sandboxId"])
        assert scenario["resultSha256"] == guest["Result"]["Sha256"]
        assert scenario["observations"] == guest["ObservationRecords"] and scenario["booleanAssertions"] == guest["BooleanAssertions"]
for entry in source["SourceFiles"]: assert sha(root / entry["Path"]) == entry["Sha256"], entry["Path"]
old = json_read(evidence / "historical-delivery-binding-r3.json")
build = (evidence / (active["ReleaseBuild"] + ".log")).read_text(encoding="utf-8")
assert re.search(r"0 个警告", build) and re.search(r"0 个错误", build)
assert json_read(evidence / (active["ReleaseBuild"] + ".command.json"))["ExitCode"] == 0
assert json_read(evidence / (active["FullTests"] + ".command.json"))["ExitCode"] == 0
assert inspection["FullTestReports"]["Failed"] == 0
assert inspection["FixedProductionReports"]["Total"] == 9 and inspection["FixedProductionReports"]["Failed"] == 0
constraints = []
for path in ["specs/CONTRACTS.cs", "specs/DB_SCHEMA.sql", "specs/settings.example.json", "Directory.Packages.props"]:
    current = subprocess.check_output(["git", "hash-object", "--path", path, path], cwd=root).decode().strip()
    baseline = subprocess.check_output(["git", "rev-parse", source["BaseCommit"] + ":" + path], cwd=root).decode().strip()
    assert current == baseline, path
    constraints.append(dict(file_record(root / path), GitBlob=current))

scenarios = [
    {"Scenario": "Bilingual uninstall choices, reinstall, failed data cleanup and retry", "EvidenceDir": "tasks/done/T-039B.evidence/guest-matrix-4",
     "PackageTag": "release-0.2.0-6", "InputBinding": file_record(evidence.parent / "guest-matrix-4/input-binding.json")},
    {"Scenario": "Full synthetic data retention/deletion and ownership branches", "EvidenceDir": "tasks/done/T-039B.evidence/guest-coverage-final-2",
     "PackageTag": "final-3", "InputBinding": file_record(evidence.parent / "guest-final-inputs-3/input-binding.json")},
    {"Scenario": "Installer cancellation and checksum failure", "EvidenceDir": "tasks/done/T-039B.evidence/guest-remaining-final-1",
     "PackageTag": "final-3", "InputBinding": file_record(evidence.parent / "guest-remaining-final-1/input-binding.json")},
    {"Scenario": "Committed native failure feedback and recovery", "EvidenceDir": "tasks/done/T-039B.evidence/guest-committed-failure-final-1",
     "PackageTag": "final-3", "InputBinding": file_record(evidence.parent / "guest-final-inputs-2/input-binding.json")},
]
binding = {
    "BaseCommit": source["BaseCommit"], "GitTree": source["GitTree"], "GitTreeSemantics": source["GitTreeSemantics"], "GitTreeScope": source["Roots"],
    "CompletionStatus": "ReadyForIndependentReview" if complete else "AwaitingOwnedGuestVerification_NotComplete",
    "RequiredUnfinished": [] if complete else [active["PackageTag"] + " install and running silent retention uninstall in clean owned guest", active["PackageTag"] + " two-version local full-package apply in clean owned guest"],
    "BoundPackageVersions": ["0.2.0", "0.2.1"], "BoundPackageArtifactTag": active["PackageTag"], "ProductSourcesMatchTestedCandidate": True,
    "SourceFiles": [dict(entry, WorkingSha256=entry["Sha256"]) for entry in source["SourceFiles"]],
    "TestedCandidate": file_record(evidence / active["SourceBinding"]), "UnchangedContractsSchemaAndVersions": constraints,
    "SupersededCandidatePreserved": {"SourceBinding": file_record(evidence / "tested-source-binding.json"), "PackageInspection": file_record(evidence / "package-inspection.json"), "PackageTag": "final-6", "BeforeThemeFix": file_record(evidence / "before-theme-fix/binding.json")},
    "SupersededCandidate2Preserved": {"SourceBinding": file_record(evidence / "tested-source-binding-2.json"), "PackageInspection": file_record(evidence / "package-inspection-2.json"), "PackageTag": "final-7", "BeforeLocalizationFix": file_record(evidence / "before-localization-fix/binding.json")},
    "ReleasePackages": inspection["ReleasePackages"],
    "FeedVerification": file_record(evidence / active["FeedInspection"]),
    "LocalizationVerification": json_read(evidence / "localization-verification-1.json"),
    "TestVerification": {"BuildRelease": {"Command": json_read(evidence / (active["ReleaseBuild"] + ".command.json")), "Warnings": 0, "Errors": 0},
        "FullTests": inspection["FullTestReports"], "FixedProductionTopology": inspection["FixedProductionReports"]},
    "RecoveryThemeVerification": {"IndependentFailure": file_record(root / "tasks/done/T-039B.review/acceptance-review-4/source-feedback-2/README.md"),
        "IndependentReviewedImages": file_record(root / "tasks/done/T-039B.review/acceptance-review-4/recovery-ui/attempt-2/reviewed.json"),
        "BeforeRegression": test_reports(evidence / (active["RenderingBefore"] + "-reports")),
        "AfterRegression": test_reports(evidence / (active["RenderingAfter"] + "-reports"), "Iris.App.Tests.Updates.UpdateRecoveryRenderingTests."),
        "AfterViewedImages": file_record(evidence / active["RenderingReviewed"]),
        "ResourceScope": "App.xaml global ThemesDictionary and ControlsDictionary plus production ApplicationThemeManager.Apply; no window dictionaries or brush overrides from the test."},
    "IndependentReviewR4": {"AppliesToCandidate": "r4-tested-candidate-2", "AppliesToSourceGitTree": prior_independent_source["GitTree"], "AppliesToPackageTag": "final-7",
        "HistoricalOnlyForCurrentCandidate": source["GitTree"] != prior_independent_source["GitTree"],
        "RuntimeAssemblies": independent_runtime["assemblies"], "RuntimeScope": independent_runtime["scope"],
        "FrozenInputsUnchanged": independent_runtime["allFrozenInputsUnchanged"], "ByteChecks": independent_bytes["checks"],
        "ByteChecksPassed": independent_bytes["passed"], "ByteCheckScope": independent_bytes["scope"],
        "VisualScope": independent_visual["scope"], "ThemeDefectVisuallyClosed": independent_visual["themeDefectVisuallyClosedForCandidate2"]},
    "CurrentIndependentReviewR4": {"AppliesToCandidate": "r4-tested-candidate-3", "AppliesToSourceGitTree": source["GitTree"], "AppliesToPackageTag": active["PackageTag"],
        "RuntimeAssemblies": current_independent_runtime["assemblies"], "RuntimeScope": current_independent_runtime["scope"],
        "FrozenInputsUnchanged": current_independent_runtime["allFrozenInputsUnchanged"], "WorkerResults": len(current_independent_runtime["workerResults"]),
        "ByteChecks": current_independent_bytes["checks"], "ByteChecksPassed": current_independent_bytes["passed"], "ByteCheckScope": current_independent_bytes["scope"],
        "Localization": current_independent_localization, "VisualScope": current_independent_visual["scope"], "VisualCarryForwardFrames": len(current_independent_visual["frames"]),
        "AffectedGuestByteChecks": current_guest_review["checks"], "AffectedGuestByteChecksPassed": current_guest_review["passed"],
        "AffectedGuestScope": current_guest_review["scope"],
        "ArchitectVisualReviewSource": file_record(evidence / "architect-final8-guest-review-supplement-20261025.md"),
        "VisualReviewSemantics": "The raw automated guest JSON retains visuallyReviewed=false. The later architect conversation separately confirms actual individual viewing; the raw file was not rewritten.",
        "WholeR4OrFinalHeadAccepted": False},
    "IndependentReviewRecords": [file_record(path) for path in independent_files + current_independent_files],
    "BeforeAfter": {"Before": file_record(evidence / "frozen-r3-production-before-1.log"), "BeforeExit": 1,
        "FrozenR3Binding": file_record(root / "tasks/done/T-039B.review/acceptance-review-3/rollback-probe-binding.json"),
        "FrozenDependenciesVerified": inspection["FrozenR3DependenciesVerified"], "After": file_record(evidence / (active["FixedProduction"] + ".log")), "AfterExit": 0,
        "Scope": "Before replays the unmodified frozen R3 counterexample; after runs the published complete production topology with actual settings shutdown, Dispatcher, preparation, revoke and recovery receipt ownership. Different fixtures are disclosed; no claim that the old binary was rerun against new internal APIs."},
    "SandboxVerificationR4": inspection.get("SandboxVerificationR4", {"Status": "NotComplete_RuntimeDownloadFailure", "HistoricalPrerequisiteLog": file_record(evidence / "sandbox-preflight-1.log"),
        "HistoricalNoGuestAttempt": file_record(evidence / "guest-install-r4-1-run.log"), "EnabledPreflight": file_record(evidence / "sandbox-enabled-preflight-1.log")}),
    "GuestAttemptLedger": file_record(evidence / "guest-attempt-ledger-1.json") if (evidence / "guest-attempt-ledger-1.json").exists() else None,
    "EngineerViewedGuestImages": file_record(evidence / "reviewed-guests-r4-1.json") if complete else None,
    "RuntimeBinaryRelocation": file_record(evidence / "runtime-binary-relocation-1.json"),
    "RuntimeBinaryArtifacts": runtime_artifacts,
    "GuestExecutionScope": "Only newly owned clean Sandbox instances. Optional runtime prefetch places SHA512/Authenticode-verified official Microsoft bytes in the normal Downloads cache without installing .NET; production final-8 Setup still owns runtime detection and actual installation. No claim that the vendor's complete internet download succeeded. Public DNS was a failed guest-only experiment, not used by successful scenarios. No host network/product installation changes.",
    "HistoricalR3Correction": {"OriginalBinding": file_record(evidence / "historical-delivery-binding-r3.json"), "OriginalReport": file_record(evidence / "historical-report-r3.md"),
        "OriginalGitTree": old["GitTree"], "OriginalTreeSemanticsCorrection": "Earlier index snapshot differed from the final R3 commit at delivery-binding.json itself. It is preserved as history, not asserted as the final R4 commit or complete evidence tree.",
        "ActualObservations": inspection["CorrectedR3Observations"], "AppliedPackage": "Iris-0.2.1-full.nupkg", "Delta": "Generated only; application unverified"},
    "RetainedUnaffectedScenarios": scenarios,
    "ActiveOrderSnapshot": file_record(evidence / active["OrderSnapshot"]),
    "ArchivedOrderSha256": sha(evidence / active["OrderSnapshot"]),
    "PriorOrderSnapshots": [file_record(evidence / "signed-order-r4.md"), file_record(evidence / "signed-order-r4-env-supplement-20261025.md"), file_record(evidence / "signed-order-r4-scope-supplement-20261025.md"), file_record(evidence / "signed-order-r4-localization-supplement-20261025.md")],
    "EnvironmentCoordination": {"Owner": "Architect", "HistoricalBlockedSource": file_record(review / "host-isolation-preflight-1.json"),
        "EnabledSource": file_record(review / "host-isolation-enabled-1.json"), "EnabledEngineerPreflight": file_record(evidence / "sandbox-enabled-preflight-1.log"),
        "SandboxFeatureState": "Enabled (Win32_OptionalFeature InstallState=1)", "HistoricalHyperVAccess": "Current account denied",
        "HostFeatureEnablement": "User enabled Sandbox. Neither architect nor engineer ran the proposed enablement script; earlier blocked snapshots are preserved.",
        "EngineerMustNotRepeatUserRequest": True, "CurrentTaskRetainedUntilNewGuestVerificationCompletes": True},
    "UnverifiedItems": ["Default native complete runtime download in this network environment (Peer disconnected retained; verified cache bootstrap only)", "Delta package application", "Standard-user runtime elevation refusal", "System restart branch", "Cross-user install", "Physical portable environment", "Public anonymous GitHub user UI update path (T-039C)", "Final NFR (T-040)"],
    "EvidenceScope": "Exact file records below, created after logs finished. Binding excludes itself and any later verification output. GitTree deliberately excludes all evidence; no self-reference.",
    "Evidence": [file_record(p) for p in sorted(evidence.rglob("*")) if p.is_file() and "__pycache__" not in p.parts
        and not p.name.startswith(("binding-check-", "source-commit-check-"))]
        + [file_record(root / "tasks/done/T-039B.md")],
    "StagedCodexFiles": 0,
}
destination = evidence.parent / "delivery-binding.json"
destination.write_text(json.dumps(binding, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"Binding": str(destination), "Sha256": sha(destination), "CompletionStatus": binding["CompletionStatus"], "GitTree": binding["GitTree"]}, ensure_ascii=False))
