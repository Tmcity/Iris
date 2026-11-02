"""Execute only the user-approved v0.2.4 tag boundary; retain every original command result."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import urllib.error
import urllib.request

ROOT = Path("F:/Iris")
SOURCE = "942b0ddeb62d9eb3e4f244574c0c093ab2942ffc"
TAG = "v0.2.4"
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1/output/session-1c8333f923744572ac2c71bab9576af5"
EVIDENCE = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.4-20261102-1"
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
save(EVIDENCE / "user-approval.json", {"Received": "Explicit user reply in the active conversation", "Answer": "确认发布0.2.4（仅新版本）", "RecordedUtc": now(), "ReviewArchiveCommit": "0d1c49a437f2ed75e19d4c1ecaf90df40491f8d2", "SourceCommit": SOURCE, "Tag": TAG, "BranchPushAuthorized": False, "OtherVersionsAuthorized": False})

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
    run("reviewed-source-integrated-locally", ["git", "merge-base", "--is-ancestor", SOURCE, "0d1c49a437f2ed75e19d4c1ecaf90df40491f8d2"])
    local_code, _ = run("local-tag-absence", ["git", "show-ref", "--verify", "--quiet", "refs/tags/" + TAG], allow_nonzero=True)
    assert local_code == 1
    old = read(sorted(SESSION.glob("old-ready-*.json"))[-1])
    supplement_path = sorted(SESSION.glob("old-ready-supplement-*"))[-1]
    supplement = read(supplement_path / "supplement.json")
    observer = read(supplement_path / "watcher-v2-status.json")
    assert old["InstalledSource"]["Version"] == "0.2.2" and old["Processes"][0]["Pid"] == 6604
    assert supplement["CheckForUpdates"] is False and observer["Ready"] is True and observer["Failure"] is None
    assert (datetime.datetime.now(datetime.timezone.utc) - datetime.datetime.fromisoformat(observer["LastHeartbeatUtc"].replace("Z", "+00:00"))).total_seconds() < 60
    assert not any("0.2.4" in item["Name"] for item in old["Packages"])
    snapshot = EVIDENCE / "before-tag-supplement"
    snapshot.mkdir()
    for path in supplement_path.iterdir():
        if path.is_file() and path.suffix in {".json", ".jsonl", ".log"}:
            shutil.copyfile(path, snapshot / path.name)
    save(EVIDENCE / "gates.json", {"ObservedUtc": now(), "SourceCommit": SOURCE, "ReviewArchiveCommit": "0d1c49a437f2ed75e19d4c1ecaf90df40491f8d2", "RemoteBefore": before, "OldAppPid": 6604, "OldVersion": "0.2.2", "ObserverPid": observer["RecorderPid"], "CheckForUpdates": False, "NewPackageBeforePublication": False, "Scope": "Only one annotated version tag; no branch push, rerun, asset overwrite or other version"})
    state["Stage"] = "preflight-verified"
    persist_state()
    run("create-local-v024", ["git", "tag", "-a", TAG, SOURCE, "-m", "Iris 0.2.4 approved source for the public 0.2.2 upgrade"])
    _, tag_object = run("local-tag-object", ["git", "rev-parse", "refs/tags/" + TAG])
    _, tag_target = run("local-tag-target", ["git", "rev-parse", "refs/tags/" + TAG + "^{}"])
    assert tag_target.strip() == SOURCE
    state.update(Stage="tag-created-push-not-requested", TagObject=tag_object.strip())
    persist_state()
    state.update(Stage="tag-push-requested-outcome-unknown", PushRequestAttempted=True)
    persist_state()
    push_code, _ = run("push-v024", ["git", *IDENTITY, "-c", "push.followTags=false", "push", "--no-follow-tags", "origin", "refs/tags/" + TAG + ":refs/tags/" + TAG], allow_nonzero=True)
    state["PushCliConfirmed"] = push_code == 0
    persist_state()
    after = remote("remote-after-v024")
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
