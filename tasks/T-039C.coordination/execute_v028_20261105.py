"""Default to read-only preflight; a separate explicit approval permits only the reviewed 0.2.8 tag."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import os
import subprocess

ROOT = Path("F:/Iris")
SOURCE = "2b2bbe70f765f9805471a0d555874f8e2383923a"
TREE = "5e15542f003811e262e80737113ce11b3c8b8b83"
REVIEW = "2cedde27acf5e5457dbc848283c10c93b274309d"
TAG = "v0.2.8"
SANDBOX = "2fc7c168-2f34-437a-8319-8974e6f592b2"
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
BASELINE_PATH = "tasks/done/T-039C.evidence/manual-old-ready-before-v027-20261105-1/binding.json"
CANDIDATE_PATH = "tasks/done/T-039C.evidence/next-source-v028-20261105-1/review.json"
parser = argparse.ArgumentParser()
parser.add_argument("--mode", choices=("preflight", "publish"), default="preflight")
parser.add_argument("--approval-file", type=Path)
parser.add_argument("--publication-attempt", type=int, default=1)
parser.add_argument("--public-verification", choices=("api", "website"), default="website")
args = parser.parse_args()
if not 1 <= args.publication_attempt <= 10:
    parser.error("Publication attempt must identify a new retained receipt between 1 and 10.")
approval = None
if args.mode == "publish":
    if args.approval_file is None:
        parser.error("Publish requires the separate explicit user approval for v0.2.8; no approval was provided.")
    approval_path = args.approval_file.resolve()
    if not approval_path.is_relative_to(ROOT / "tasks/T-039C.coordination"):
        parser.error("Approval must be the recorded user reply in this task's coordination directory.")
    approval = json.loads(approval_path.read_text(encoding="utf-8-sig"))
    if not (approval.get("ExplicitUserConfirmation") is True and approval.get("Tag") == TAG
            and approval.get("SourceCommit") == SOURCE and approval.get("ReviewArchiveCommit") == REVIEW
            and approval.get("BranchPushAuthorized") is False and approval.get("OtherVersionsAuthorized") is False
            and isinstance(approval.get("Answer"), str) and "0.2.8" in approval["Answer"]
            and isinstance(approval.get("QuestionItemId"), str) and approval["QuestionItemId"]):
        parser.error("Approval does not match the reviewed v0.2.8 tag-only scope.")


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    path.open("x", encoding="utf-8", newline="\n").write(json.dumps(value, indent=2) + "\n")


EVIDENCE = ROOT / ("tasks/done/T-039C.evidence/publication-v0.2.8-20261105-" + str(args.publication_attempt) if args.mode == "publish" else
    "tasks/T-039C.coordination/v028-execution-preflight-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ"))
EVIDENCE.mkdir(parents=True, exist_ok=False)
environment = dict(os.environ, GIT_TERMINAL_PROMPT="0")
state = {"Tag": TAG, "SourceCommit": SOURCE, "ReviewArchiveCommit": REVIEW,
         "PublicVerificationMode": args.public_verification,
         "Stage": "approval-recorded" if approval else "read-only-preflight-no-user-approval",
         "PublicationAuthorized": approval is not None, "PushRequestAttempted": False,
         "PushCliConfirmed": False, "TagRemoteVerified": False, "ReleasePublic": None, "Failure": None}
if approval:
    save(EVIDENCE / "user-approval.json", {**approval, "RecordedUtc": now()})


def persist():
    (EVIDENCE / "state.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")


def run(label, command, allow_nonzero=False):
    started = now()
    # Keep the actual process alive for observation by the outer tool session.
    # A lost observer or elapsed wait is not permission to repeat a push.
    process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    save(EVIDENCE / (label + ".process.json"), {"Pid": process.pid, "Command": command, "StartedUtc": started})
    output, _ = process.communicate()
    (EVIDENCE / (label + ".log")).open("xb").write(output)
    save(EVIDENCE / (label + ".command.json"), {"Command": command, "StartedUtc": started,
        "FinishedUtc": now(), "ExitCode": process.returncode, "Bytes": len(output),
        "Sha256": hashlib.sha256(output).hexdigest()})
    if process.returncode != 0 and not allow_nonzero:
        raise RuntimeError(label + " failed with exit " + str(process.returncode))
    return process.returncode, output.decode("utf-8-sig", errors="replace")


def remote(label):
    _, text = run(label, ["git", "ls-remote", "origin", "refs/heads/main", "refs/heads/codex/t039c-public-release", "refs/tags/v0.2.*"])
    return {name: blob for blob, name in (row.split() for row in text.splitlines() if row.strip())}


def anonymous_release(label, version, expected_status):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    url = "https://api.github.com/repos/Tmcity/Iris/releases/tags/v" + version + "?iris_observation=v028-" + stamp
    command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "40", "--retry", "0",
        "-H", "Accept: application/vnd.github+json", "-H", "Cache-Control: no-cache", "-A", "Iris-public-release-verification",
        "-D", str(EVIDENCE / (label + ".headers")), "-o", str(EVIDENCE / (label + ".body")), "--write-out", "%{http_code}", url]
    code, status = run(label, command, allow_nonzero=True)
    body_path = EVIDENCE / (label + ".body")
    body = body_path.read_bytes() if body_path.exists() else b""
    save(EVIDENCE / (label + ".observation.json"), {"Url": url, "HttpStatus": status.strip(),
        "ExitCode": code, "ObservedUtc": now(), "AuthorizationSent": False,
        "BodyBytes": len(body), "BodySha256": hashlib.sha256(body).hexdigest()})
    if code != 0 or status.strip() != expected_status:
        raise RuntimeError(label + " observation incomplete: curl=" + str(code) + ", HTTP=" + status.strip())
    return json.loads(body)


def public_website_asset(label, url, expected_status, timeout=40):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    observed_url = url + "?iris_observation=v028-" + stamp
    command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", str(timeout), "--retry", "0",
        "--proto", "=https", "--proto-redir", "=https", "-H", "Cache-Control: no-cache",
        "-A", "Iris-public-release-verification", "-D", str(EVIDENCE / (label + ".headers")),
        "-o", str(EVIDENCE / (label + ".body")), "--write-out", "%{http_code}", observed_url]
    code, status = run(label, command, allow_nonzero=True)
    body_path = EVIDENCE / (label + ".body")
    size = body_path.stat().st_size if body_path.exists() else 0
    save(EVIDENCE / (label + ".observation.json"), {"Url": observed_url, "HttpStatus": status.strip(),
        "ExitCode": code, "ObservedUtc": now(), "AuthorizationSent": False, "UsesGitHubRestApi": False,
        "BodyBytes": size, "BodySha256": sha(body_path) if size else hashlib.sha256(b"").hexdigest()})
    if code != 0 or status.strip() != expected_status:
        raise RuntimeError(label + " observation incomplete: curl=" + str(code) + ", HTTP=" + status.strip())
    return body_path


def verify_public_website():
    base = "https://github.com/Tmcity/Iris/releases"
    previous_page = public_website_asset("previous-public-page", base + "/tag/v0.2.7", "200")
    assert "/Tmcity/Iris/releases/tag/v0.2.7" in previous_page.read_text(encoding="utf-8")
    public_website_asset("new-public-page-absence", base + "/tag/v0.2.8", "404")
    previous_feed = public_website_asset("previous-public-feed", base + "/download/v0.2.7/releases.win.json", "200")
    feed = read(previous_feed)["Assets"]
    assert len(feed) == 3
    previous = next(item for item in feed if item["Version"] == "0.2.7" and item["Type"] == "Full")
    assert previous["PackageId"] == "Iris" and previous["Version"] == "0.2.7" and previous["Type"] == "Full"
    assert previous["FileName"] == "Iris-0.2.7-full.nupkg" and previous["Size"] == 64627391
    assert previous["SHA256"].lower() == "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8"
    previous_full = public_website_asset("previous-public-full", base + "/download/v0.2.7/Iris-0.2.7-full.nupkg", "200", timeout=90)
    assert previous_full.stat().st_size == previous["Size"] and sha(previous_full) == previous["SHA256"].lower()
    save(EVIDENCE / "website-public-base-binding.json", {"Version": "0.2.7", "Full": previous,
        "OfficialApiDigestAlreadyBoundInReview": True, "FreshAnonymousFullBytesMatched": True,
        "SourceOfCurrentObservations": "GitHub official public website and published download URLs",
        "GitHubRestApiQuotaBypassAttempted": False, "PublicationAuthorized": approval is not None})


persist()
try:
    _, origin = run("origin-push-target", ["git", "remote", "get-url", "--push", "origin"])
    assert origin.strip() == "git@github.com:Tmcity/Iris.git"
    _, candidate_text = run("committed-candidate-review", ["git", "show", REVIEW + ":" + CANDIDATE_PATH])
    candidate = json.loads(candidate_text)
    _, baseline_text = run("committed-old-baseline", ["git", "show", REVIEW + ":" + BASELINE_PATH])
    baseline = json.loads(baseline_text)
    assert candidate == read(ROOT / CANDIDATE_PATH) and baseline == read(ROOT / BASELINE_PATH)
    assert candidate["SourceCommit"] == SOURCE and candidate["SourceGitTree"] == TREE
    assert candidate["FullTests"] == {"Total": 3770, "Passed": 3768, "Failed": 0, "Skipped": 2}
    assert candidate["PublicAssets"] == 19 and candidate["PayloadEntries"] == 97
    assert baseline["OldReadyVerified"] and baseline["SandboxId"] == SANDBOX
    run("source-integrated-into-review", ["git", "merge-base", "--is-ancestor", SOURCE, REVIEW])
    run("review-retained-in-current-head", ["git", "merge-base", "--is-ancestor", REVIEW, "HEAD"])
    roots = ["src", "tests", "build", ".github", "docs", "README.md", "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"]
    run("reviewed-release-sources-unchanged", ["git", "diff", "--quiet", SOURCE, "--", *roots])
    before = remote("remote-before")
    expected_refs = read(ROOT / "tasks/done/T-039C.evidence/publication-v0.2.7-20261105-2/state.json")["RemoteAfter"]
    assert before == expected_refs and "refs/tags/" + TAG not in before
    local_code, _ = run("local-tag-absence", ["git", "show-ref", "--verify", "--quiet", "refs/tags/" + TAG], allow_nonzero=True)
    assert local_code == 1
    if args.public_verification == "website":
        verify_public_website()
    else:
        release = anonymous_release("previous-public-release", "0.2.7", "200")
        assert release["draft"] is False and release["prerelease"] is False
        previous = next(item for item in release["assets"] if item["name"] == "Iris-0.2.7-full.nupkg")
        assert previous["size"] == 64627391 and previous["digest"] == (
            "sha256:f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8")
        anonymous_release("new-public-release-absence", "0.2.8", "404")
    candidate_directory = ROOT / "artifacts/t039c/release-0.2.8-public-base-candidate-1"
    assert {path.name for path in candidate_directory.iterdir()} == {item["Name"] for item in candidate["Assets"]}
    for item in candidate["Assets"]:
        path = candidate_directory / item["Name"]
        assert path.is_file() and not path.is_symlink() and path.stat().st_size == item["Bytes"]
        assert sha(path) == item["Sha256"]
    _, sandbox_text = run("live-sandbox-list", ["wsb.exe", "list", "--raw"])
    assert SANDBOX in {item["Id"] for item in json.loads(sandbox_text)["WindowsSandboxEnvironments"]}
    captured_after = datetime.datetime.now(datetime.timezone.utc)
    for label, command in (
        ("fresh-old-ready", r"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\save-update-evidence.ps1 -Stage old-ready"),
        ("fresh-old-processes", r"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\inspect-old-ready-readonly.ps1")):
        _, text = run(label, ["wsb.exe", "exec", "--id", SANDBOX, "--run-as", "ExistingLogin", "--command", command, "--raw"])
        assert json.loads(text)["ExitCode"] == 0
    old_path = sorted(SESSION.glob("old-ready-*.json"))[-1]
    supplement_path = sorted(SESSION.glob("old-ready-supplement-*"))[-1]
    inspection_path = sorted(SESSION.glob("old-ready-readonly-*"))[-1]
    old = read(old_path)
    supplement = read(supplement_path / "supplement.json")
    inspection = read(inspection_path / "inspection.json")
    status = read(supplement_path / "watcher-v2-status.json")
    pointer = read(supplement_path / "active-recorder-v2.json")
    assert datetime.datetime.fromisoformat(old["Utc"].replace("Z", "+00:00")) >= captured_after
    assert len(old["Processes"]) == 1 and old["Processes"][0]["Pid"] == baseline["AppPid"]
    assert old["Processes"][0]["StartedUtc"] == baseline["AppStartedUtc"]
    assert old["InstalledSource"]["Version"] == inspection["ProductSource"]["Version"] == "0.2.2"
    assert {item["Pid"] for item in inspection["Processes"]} == {baseline["AppPid"], baseline["ProviderPid"], baseline["RecorderPid"]}
    assert inspection["CheckForUpdates"] is supplement["CheckForUpdates"] is False
    hashes = {item["Path"]: item.get("Sha256") for item in old["DataHashes"]}
    assert all(hashes.get(name) == value for name, value in baseline["DataHashes"].items())
    assert inspection["SettingsSha256"] == hashes["settings.json"] and inspection["SecretsSha256"] == hashes["secrets.dat"]
    assert inspection["SyntheticProfiles"] == [baseline["SyntheticProfile"]]
    assert status["RecorderVersion"] == pointer["RecorderVersion"] == 3
    assert status["RecorderPid"] == pointer["RecorderPid"] == baseline["RecorderPid"]
    assert status["Ready"] and status["Failure"] is None and pointer["Ready"]
    assert pointer["SourceSha256"] == "3515546d9e344331cc2c95fc4c97f60b213bfc06ae11ada98d96349b3e3dac62"
    assert (datetime.datetime.now(datetime.timezone.utc) - datetime.datetime.fromisoformat(status["LastHeartbeatUtc"].replace("Z", "+00:00"))).total_seconds() < 30
    assert not any("0.2.8" in item["Name"] for item in old["Packages"])
    snapshot = EVIDENCE / "before-tag-snapshot"
    snapshot.mkdir()
    (snapshot / old_path.name).open("xb").write(old_path.read_bytes())
    for folder, name in ((supplement_path, "supplement"), (inspection_path, "inspection")):
        for path in folder.iterdir():
            if path.is_file() and path.suffix in {".json", ".jsonl", ".log"}:
                target = snapshot / name / path.name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.open("xb").write(path.read_bytes())
    save(EVIDENCE / "gates.json", {"ObservedUtc": now(), "SourceCommit": SOURCE, "ReviewArchiveCommit": REVIEW,
        "PublicVerificationMode": args.public_verification,
        "RemoteBefore": before, "OldAppPid": baseline["AppPid"], "OldAppStartedUtc": baseline["AppStartedUtc"],
        "OldVersion": "0.2.2", "RecorderPid": baseline["RecorderPid"], "SyntheticProviderPid": baseline["ProviderPid"],
        "CheckForUpdates": False, "OldDataHashesMatched": True, "AllCandidateAssetBytesMatched": True,
        "Scope": "Only one annotated version tag; no branch push, other version, old CI rerun or asset overwrite"})
    state["Stage"] = "preflight-verified"
    persist()
    if args.mode == "preflight":
        state["Stage"] = "preflight-verified-awaiting-explicit-user-approval"
        persist()
        print(json.dumps({"Tag": TAG, "SourceCommit": SOURCE, "ReviewArchiveCommit": REVIEW,
            "PreflightPassed": True, "PublicationAuthorized": False, "TagCreated": False,
            "RemoteWriteRequested": False, "Evidence": str(EVIDENCE)}, indent=2))
        raise SystemExit(0)
    assert approval is not None
    run("create-local-v028", ["git", "tag", "-a", TAG, SOURCE, "-m", "Iris 0.2.8 approved source for the public 0.2.2 upgrade"])
    _, tag_object = run("local-tag-object", ["git", "rev-parse", "refs/tags/" + TAG])
    _, tag_target = run("local-tag-target", ["git", "rev-parse", "refs/tags/" + TAG + "^{}"])
    assert tag_target.strip() == SOURCE
    state.update(Stage="tag-created-push-not-requested", TagObject=tag_object.strip())
    persist()
    state.update(Stage="tag-push-requested-outcome-unknown", PushRequestAttempted=True)
    persist()
    push_code, _ = run("push-v028", ["git", "-c", "push.followTags=false", "push", "--no-follow-tags", "origin",
                                   "refs/tags/" + TAG + ":refs/tags/" + TAG], allow_nonzero=True)
    state["PushCliConfirmed"] = push_code == 0
    persist()
    after = remote("remote-after-v028")
    assert after["refs/tags/" + TAG] == state["TagObject"] and after["refs/tags/" + TAG + "^{}"] == SOURCE
    assert {name: value for name, value in after.items() if name not in ("refs/tags/" + TAG, "refs/tags/" + TAG + "^{}")} == before
    state.update(Stage="tag-remotely-verified-awaiting-ci", TagRemoteVerified=True, RemoteAfter=after)
    persist()
    print(json.dumps({"Tag": TAG, "TagObject": state["TagObject"], "Target": SOURCE,
        "PushCliConfirmed": state["PushCliConfirmed"], "TagRemoteVerified": True,
        "BranchRefsPreserved": True, "ReleasePublic": None}, indent=2))
except Exception as error:
    state["Failure"] = {"Type": type(error).__name__, "Message": str(error)}
    raise
finally:
    persist()
