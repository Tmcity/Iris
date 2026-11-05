"""Read-only preflight by default; publish only with a separate explicit v0.2.6 approval file."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import urllib.error
import urllib.request

ROOT = Path("F:/Iris")
SOURCE = "c32fcb283e417ab11715c46207fdc8cfde4baedf"
TAG = "v0.2.6"
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1/output/session-1c8333f923744572ac2c71bab9576af5"
parser = argparse.ArgumentParser()
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
    ROOT / "tasks/T-039C.coordination" / ("v026-execution-preflight-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")))
EVIDENCE.mkdir(parents=True, exist_ok=False)
IDENTITY = ["-c", "url.git@github.com:Tmcity/Iris.git.insteadOf=git@github.com:Tmcity/Iris.git", "-c", "url.git@github.com:Tmcity/Iris.git.pushInsteadOf=git@github.com:Tmcity/Iris.git"]
env = dict(os.environ, GIT_TERMINAL_PROMPT="0")

def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def save(path, value):
    with Path(path).open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")

state = {"Tag": TAG, "SourceCommit": SOURCE, "Stage": "approval-recorded", "PushRequestAttempted": False, "PushCliConfirmed": False, "TagRemoteVerified": False, "ReleasePublic": None, "Failure": None}
state["PublicationAuthorized"] = approval is not None
if approval is not None:
    save(EVIDENCE / "user-approval.json", {**approval, "RecordedUtc": now()})
else:
    state["Stage"] = "read-only-preflight-no-user-approval"

def persist_state():
    (EVIDENCE / "state.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")

def run(label, args, allow_nonzero=False):
    started = now()
    result = subprocess.run(args, cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=90)
    raw = result.stdout
    (EVIDENCE / (label + ".log")).open("xb").write(raw)
    save(EVIDENCE / (label + ".command.json"), {"Command": args, "StartedUtc": started, "FinishedUtc": now(), "ExitCode": result.returncode, "Bytes": len(raw), "Sha256": hashlib.sha256(raw).hexdigest()})
    if result.returncode and not allow_nonzero:
        raise RuntimeError(label + " failed with exit " + str(result.returncode))
    return result.returncode, raw.decode("utf-8-sig", errors="replace")

def remote(label):
    _, raw = run(label, ["git", "-c", "credential.helper=", "-c", "http.extraHeader=", "-c", "core.askPass=", "-c", "url.https://github.com/.insteadOf=", "ls-remote", "https://github.com/Tmcity/Iris.git", "refs/heads/main", "refs/heads/codex/t039c-public-release", "refs/tags/v0.2.*"])
    return {name: sha for sha, name in (row.split() for row in raw.splitlines() if row.strip())}

persist_state()
try:
    _, origin = run("origin-target", ["git", *IDENTITY, "remote", "get-url", "--push", "origin"])
    assert origin.strip() == "git@github.com:Tmcity/Iris.git"
    before = remote("remote-before")
    assert "refs/tags/" + TAG not in before
    assert before["refs/heads/main"] == before["refs/heads/codex/t039c-public-release"] == "285df3a2986f3379994eb56dd8b75617db9f470d"
    run("base-reachable-from-main", ["git", "merge-base", "--is-ancestor", "fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11", before["refs/heads/main"]])
    run("reviewed-source-descends-from-base", ["git", "merge-base", "--is-ancestor", "fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11", SOURCE])
    run("reviewed-source-integrated-locally", ["git", "merge-base", "--is-ancestor", SOURCE, "2ccad4d210d5e76c0cd2d598037372f940da67ae"])
    local_code, _ = run("local-tag-absence", ["git", "show-ref", "--verify", "--quiet", "refs/tags/" + TAG], allow_nonzero=True)
    assert local_code == 1
    candidate = read(ROOT / "tasks/done/T-039C.evidence/next-source-v026-20261103-1/review.json")
    assert candidate["SourceCommit"] == SOURCE and candidate["SourceGitTree"] == "c39947f03475872a5178929e32b078a383d537e2"
    assert candidate["FullTests"] == {"Total": 3758, "Passed": 3756, "Failed": 0, "Skipped": 2}
    assert candidate["PublicAssets"] == 18 and candidate["PayloadEntries"] == 97
    assert hashlib.sha256((ROOT / "artifacts/t039c/release-0.2.6-public-base-candidate-1/Iris-0.2.6-Setup.exe").read_bytes()).hexdigest() == candidate["OuterSha256"]
    assert hashlib.sha256((ROOT / "artifacts/t039c/release-0.2.6-public-base-candidate-1/Iris-0.2.6-full.nupkg").read_bytes()).hexdigest() == candidate["FullSha256"]
    observed_after = datetime.datetime.now(datetime.timezone.utc)
    _, guest_raw = run("fresh-guest-old-ready", ["wsb.exe", "exec", "--id", "590f8848-f6b4-4bdb-a151-85c4a4f19305", "--run-as", "ExistingLogin", "--command", r"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceUpgrade\save-update-evidence.ps1 -Stage old-ready", "--raw"])
    assert json.loads(guest_raw)["ExitCode"] == 0
    old = read(sorted(SESSION.glob("old-ready-*.json"))[-1])
    assert datetime.datetime.fromisoformat(old["Utc"].replace("Z", "+00:00")) >= observed_after
    supplement_path = sorted(SESSION.glob("old-ready-supplement-*"))[-1]
    supplement = read(supplement_path / "supplement.json")
    observer = read(supplement_path / "watcher-v2-status.json")
    assert old["InstalledSource"]["Version"] == "0.2.2" and len(old["Processes"]) == 1 and old["Processes"][0]["Pid"] == 6604
    assert old["Processes"][0]["StartedUtc"] == "2026-11-02T04:25:01.2204360Z"
    recorder = supplement["V2Recorder"]
    assert observer["RecorderVersion"] == recorder["RecorderVersion"] == 3 and observer["RecorderPid"] == recorder["RecorderPid"] == 4604
    assert recorder["SourceSha256"] == "3515546d9e344331cc2c95fc4c97f60b213bfc06ae11ada98d96349b3e3dac62"
    expected_hashes = {"settings.json": "4b94fbcbe8058b3b7415d1264f7e0766c49f3cd9bc9d9777c96e0fef156a2101",
        "secrets.dat": "6029155b4f25ed9ffd4d4091ebd6e8b5adbb380d23aaaadc7828e65e403886a9",
        "history.db": "8bb46ccb59258f8ce804a0b941635de6e031e68c618375306c7ccae08538f223"}
    actual_hashes = {item["Path"]: item.get("Sha256") for item in old["DataHashes"]}
    assert all(actual_hashes.get(name) == value for name, value in expected_hashes.items())
    assert supplement["CheckForUpdates"] is False and observer["Ready"] is True and observer["Failure"] is None
    assert (datetime.datetime.now(datetime.timezone.utc) - datetime.datetime.fromisoformat(observer["LastHeartbeatUtc"].replace("Z", "+00:00"))).total_seconds() < 60
    assert not any("0.2.6" in item["Name"] for item in old["Packages"])
    snapshot = EVIDENCE / "before-tag-supplement"
    snapshot.mkdir()
    for path in supplement_path.iterdir():
        if path.is_file() and path.suffix in {".json", ".jsonl", ".log"}:
            shutil.copyfile(path, snapshot / path.name)
    save(EVIDENCE / "gates.json", {"ObservedUtc": now(), "SourceCommit": SOURCE, "ReviewArchiveCommit": "2ccad4d210d5e76c0cd2d598037372f940da67ae", "RemoteBefore": before, "OldAppPid": 6604, "OldVersion": "0.2.2", "ObserverPid": observer["RecorderPid"], "CheckForUpdates": False, "NewPackageBeforePublication": False, "Scope": "Only one annotated version tag; no branch push, rerun, asset overwrite or other version"})
    state["Stage"] = "preflight-verified"
    persist_state()
    if args.mode == "preflight":
        state["Stage"] = "preflight-verified-awaiting-explicit-user-approval"
        persist_state()
        print(json.dumps({"SourceCommit": SOURCE, "Tag": TAG, "ReviewArchiveCommit": "2ccad4d210d5e76c0cd2d598037372f940da67ae", "PreflightPassed": True, "PublicationAuthorized": False, "TagCreated": False, "RemoteWriteRequested": False, "Evidence": str(EVIDENCE)}, indent=2))
        raise SystemExit(0)
    assert approval is not None
    run("create-local-v026", ["git", "tag", "-a", TAG, SOURCE, "-m", "Iris 0.2.6 approved source for the public 0.2.2 upgrade"])
    _, tag_object = run("local-tag-object", ["git", "rev-parse", "refs/tags/" + TAG])
    _, tag_target = run("local-tag-target", ["git", "rev-parse", "refs/tags/" + TAG + "^{}"])
    assert tag_target.strip() == SOURCE
    state.update(Stage="tag-created-push-not-requested", TagObject=tag_object.strip())
    persist_state()
    state.update(Stage="tag-push-requested-outcome-unknown", PushRequestAttempted=True)
    persist_state()
    push_code, _ = run("push-v026", ["git", *IDENTITY, "-c", "push.followTags=false", "push", "--no-follow-tags", "origin", "refs/tags/" + TAG + ":refs/tags/" + TAG], allow_nonzero=True)
    state["PushCliConfirmed"] = push_code == 0
    persist_state()
    after = remote("remote-after-v026")
    assert after["refs/tags/" + TAG] == state["TagObject"] and after["refs/tags/" + TAG + "^{}"] == SOURCE
    assert all(after.get(name) == sha for name, sha in before.items())
    state.update(Stage="tag-remotely-verified-awaiting-ci", TagRemoteVerified=True, RemoteAfter=after)
    persist_state()
    print(json.dumps({"Tag": TAG, "TagObject": state["TagObject"], "Target": SOURCE, "PushCliConfirmed": state["PushCliConfirmed"], "TagRemoteVerified": True, "BranchRefsPreserved": True, "ReleasePublic": None}, indent=2))
except Exception as error:
    state["Failure"] = {"Type": type(error).__name__, "Message": str(error)}
    raise
finally:
    persist_state()
