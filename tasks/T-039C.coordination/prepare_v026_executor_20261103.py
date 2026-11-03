"""Adapt the audited tag executor with a default read-only mode and explicit approval guard."""
from pathlib import Path
import ast

ROOT = Path("F:/Iris")
source = (ROOT / "tasks/T-039C.coordination/execute_approved_v025_20261102.py").read_text(encoding="utf-8-sig")
source = source.replace('"""Execute only the user-approved v0.2.5 tag boundary; retain every original command result."""',
    '"""Read-only preflight by default; publish only with a separate explicit v0.2.6 approval file."""')
source = source.replace("import datetime", "import argparse\nimport datetime", 1)
source = source.replace("0.2.5", "0.2.6").replace("v025", "v026")
source = source.replace("ee2d7118597d07b891066256349098877e65f794", "c32fcb283e417ab11715c46207fdc8cfde4baedf")
source = source.replace("0d8fd189e90bba605dff9221193e01a098584600", "2ccad4d210d5e76c0cd2d598037372f940da67ae")
source = source.replace('EVIDENCE = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.6-20261102-1"', '''parser = argparse.ArgumentParser()
parser.add_argument("--mode", choices=("preflight", "publish"), default="preflight")
parser.add_argument("--approval-file", type=Path)
args = parser.parse_args()
approval = None
if args.mode == "publish":
    if args.approval_file is None:
        parser.error("Publish requires the separate explicit user approval for v0.2.6; no approval was provided.")
    approval = json.loads(args.approval_file.read_text(encoding="utf-8-sig"))
    if not (approval.get("ExplicitUserConfirmation") is True and approval.get("Tag") == TAG
            and approval.get("SourceCommit") == SOURCE
            and approval.get("ReviewArchiveCommit") == "2ccad4d210d5e76c0cd2d598037372f940da67ae"
            and approval.get("BranchPushAuthorized") is False
            and approval.get("OtherVersionsAuthorized") is False
            and isinstance(approval.get("Answer"), str) and approval["Answer"].strip()):
        parser.error("The approval file does not match the reviewed v0.2.6 tag-only scope.")
EVIDENCE = (ROOT / "tasks/done/T-039C.evidence/publication-v0.2.6-20261103-1") if args.mode == "publish" else (
    ROOT / "tasks/T-039C.coordination" / ("v026-execution-preflight-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")))''')
original_approval = 'save(EVIDENCE / "user-approval.json", {"Received": "Explicit user reply in the active conversation", "Answer": "确认发布0.2.6（仅新版本）", "RecordedUtc": now(), "ReviewArchiveCommit": "2ccad4d210d5e76c0cd2d598037372f940da67ae", "SourceCommit": SOURCE, "Tag": TAG, "BranchPushAuthorized": False, "OtherVersionsAuthorized": False})'
assert original_approval in source
source = source.replace(original_approval, '''state["PublicationAuthorized"] = approval is not None
if approval is not None:
    save(EVIDENCE / "user-approval.json", {**approval, "RecordedUtc": now()})
else:
    state["Stage"] = "read-only-preflight-no-user-approval"''')
old_snapshot = '    old = read(sorted(SESSION.glob("old-ready-*.json"))[-1])'
assert old_snapshot in source
source = source.replace(old_snapshot, '''    candidate = read(ROOT / "tasks/done/T-039C.evidence/next-source-v026-20261103-1/review.json")
    assert candidate["SourceCommit"] == SOURCE and candidate["SourceGitTree"] == "c39947f03475872a5178929e32b078a383d537e2"
    assert candidate["FullTests"] == {"Total": 3758, "Passed": 3756, "Failed": 0, "Skipped": 2}
    assert candidate["PublicAssets"] == 18 and candidate["PayloadEntries"] == 97
    assert hashlib.sha256((ROOT / "artifacts/t039c/release-0.2.6-public-base-candidate-1/Iris-0.2.6-Setup.exe").read_bytes()).hexdigest() == candidate["OuterSha256"]
    assert hashlib.sha256((ROOT / "artifacts/t039c/release-0.2.6-public-base-candidate-1/Iris-0.2.6-full.nupkg").read_bytes()).hexdigest() == candidate["FullSha256"]
    observed_after = datetime.datetime.now(datetime.timezone.utc)
    _, guest_raw = run("fresh-guest-old-ready", ["wsb.exe", "exec", "--id", "590f8848-f6b4-4bdb-a151-85c4a4f19305", "--run-as", "ExistingLogin", "--command", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\IrisAcceptanceUpgrade\\save-update-evidence.ps1 -Stage old-ready", "--raw"])
    assert json.loads(guest_raw)["ExitCode"] == 0
    old = read(sorted(SESSION.glob("old-ready-*.json"))[-1])
    assert datetime.datetime.fromisoformat(old["Utc"].replace("Z", "+00:00")) >= observed_after''')
source = source.replace('    assert old["InstalledSource"]["Version"] == "0.2.2" and old["Processes"][0]["Pid"] == 6604', '''    assert old["InstalledSource"]["Version"] == "0.2.2" and len(old["Processes"]) == 1 and old["Processes"][0]["Pid"] == 6604
    assert old["Processes"][0]["StartedUtc"] == "2026-11-02T04:25:01.2204360Z"
    recorder = supplement["V2Recorder"]
    assert observer["RecorderVersion"] == recorder["RecorderVersion"] == 3 and observer["RecorderPid"] == recorder["RecorderPid"] == 4604
    assert recorder["SourceSha256"] == "3515546d9e344331cc2c95fc4c97f60b213bfc06ae11ada98d96349b3e3dac62"
    expected_hashes = {"settings.json": "4b94fbcbe8058b3b7415d1264f7e0766c49f3cd9bc9d9777c96e0fef156a2101",
        "secrets.dat": "6029155b4f25ed9ffd4d4091ebd6e8b5adbb380d23aaaadc7828e65e403886a9",
        "history.db": "8bb46ccb59258f8ce804a0b941635de6e031e68c618375306c7ccae08538f223"}
    actual_hashes = {item["Path"]: item.get("Sha256") for item in old["DataHashes"]}
    assert all(actual_hashes.get(name) == value for name, value in expected_hashes.items())''')
source = source.replace('    run("create-local-v026",', '''    if args.mode == "preflight":
        state["Stage"] = "preflight-verified-awaiting-explicit-user-approval"
        persist_state()
        print(json.dumps({"SourceCommit": SOURCE, "Tag": TAG, "ReviewArchiveCommit": "2ccad4d210d5e76c0cd2d598037372f940da67ae", "PreflightPassed": True, "PublicationAuthorized": False, "TagCreated": False, "RemoteWriteRequested": False, "Evidence": str(EVIDENCE)}, indent=2))
        raise SystemExit(0)
    assert approval is not None
    run("create-local-v026",''')
# Keep Windows guest paths literal in the generated Python argument.
source = source.replace('"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\IrisAcceptanceUpgrade\\save-update-evidence.ps1 -Stage old-ready"',
    'r"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\IrisAcceptanceUpgrade\\save-update-evidence.ps1 -Stage old-ready"')
assert "0.2.5" not in source and "ee2d711" not in source and "0d8fd18" not in source
ast.parse(source)
target = ROOT / "tasks/T-039C.coordination/execute_v026_20261103.py"
target.open("x", encoding="utf-8", newline="\n").write(source)
print("Prepared v0.2.6 executor: default read-only mode; explicit approval file required for tag creation/push.")
