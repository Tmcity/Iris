"""Read-only source/data review and immutable evidence capture before second-release approval."""
from pathlib import Path
import datetime
import hashlib
import importlib.util
import json
import shutil
import sqlite3
import subprocess
import sys
import urllib.error
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path("F:/Iris")
SOURCE = "fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11"
TREE = "60422257ce3c47f3eb83b291b02a84d4c75add8a"
WORKTREE = ROOT / "artifacts/t039c/worktree-fb9f46f-v023-20261102-1"
CANDIDATE = ROOT / "artifacts/t039c/release-0.2.3-public-base-candidate-2"
MANUAL = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1"
SESSION = MANUAL / "output/session-1c8333f923744572ac2c71bab9576af5"
REVIEW = ROOT / "tasks/T-039C.coordination/v023-prepublication-review-20261102-1"
ARCHIVE = ROOT / "tasks/done/T-039C.evidence/manual-old-ready-v022-20261102-1"

def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def save(path, value):
    with Path(path).open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")

def copy_file(source, destination):
    destination = Path(destination)
    if destination.exists():
        raise FileExistsError(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)

tools = read(REVIEW / "tool-results.json")
verified = json.loads(tools["CandidateVerification"]["output"])
assert tools["CandidateVerification"]["exit_code"] == 0
assert (verified["SourceCommit"], verified["SourceGitTree"], verified["PublicAssets"], verified["PayloadEntries"]) == (SOURCE, TREE, 18, 97)
provenance = read(CANDIDATE / "release-provenance.json")
spec = importlib.util.spec_from_file_location("iris_review_manifest", ROOT / "build/release_manifest.py")
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)
manifest.ROOT = WORKTREE
rows = subprocess.check_output(["git", "ls-tree", "-r", "-z", SOURCE, "--", *manifest.SOURCE_ROOTS], cwd=WORKTREE).decode().strip("\0").split("\0")
actual_blobs = {}
for row in rows:
    metadata, path = row.split("\t")
    mode, kind, blob = metadata.split()
    assert (mode, kind) == ("100644", "blob")
    actual_blobs[path] = blob
entries = provenance["SourceFiles"]
assert len(entries) == len(actual_blobs) == 720
assert {entry["Path"]: entry["GitBlob"] for entry in entries} == actual_blobs
assert manifest.source_tree(entries) == TREE
source_mismatches = []
for entry in entries:
    path = WORKTREE / entry["Path"]
    if path.stat().st_size != entry["WorkingBytes"] or sha(path) != entry["WorkingSha256"]:
        source_mismatches.append(entry["Path"])
assert not source_mismatches, source_mismatches
head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT).decode().strip()
source_delta = subprocess.check_output(["git", "diff", "--name-only", SOURCE, head, "--", *manifest.SOURCE_ROOTS], cwd=ROOT).decode().splitlines()
assert not source_delta, source_delta
source_review = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), **verified, "Candidate": str(CANDIDATE), "SourceEntriesMatchedToCommit": 720, "SourceWorkingBytesMatched": 720, "SourceMismatches": source_mismatches, "EvidenceHead": head, "ReleaseSourceDeltaSinceSourceCommit": source_delta, "PreviousPublicFull": next(row for row in read(CANDIDATE / "releases.win.json")["Assets"] if row["Version"] == "0.2.2"), "Assets": [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": sha(path)} for path in sorted(CANDIDATE.iterdir()) if path.is_file()]}
previous_hash = source_review["PreviousPublicFull"]["SHA256"].lower()
assert previous_hash == "c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14"
save(REVIEW / "candidate-source-review.json", source_review)

url = "https://api.github.com/repos/Tmcity/Iris/releases?per_page=10"
request = urllib.request.Request(url, headers={"User-Agent": "Iris-T039C-Anonymous-Review", "Accept": "application/vnd.github+json"})
try:
    response = urllib.request.urlopen(request, timeout=30)
except urllib.error.HTTPError as error:
    response = error
with response:
    body = response.read()
    public_state = {"ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "Url": url, "StatusCode": response.code, "AuthorizationSent": False, "RateLimitRemaining": response.headers.get("X-RateLimit-Remaining"), "BodySha256": hashlib.sha256(body).hexdigest()}
(REVIEW / "public-release-list.body").open("xb").write(body)
if public_state["StatusCode"] == 200:
    releases = json.loads(body)
    public_state["PublicTags"] = [item["tag_name"] for item in releases]
    public_state["PublicV023Exists"] = any(item["tag_name"] == "v0.2.3" for item in releases)
    public_v022 = next(item for item in releases if item["tag_name"] == "v0.2.2")
    public_state["PublicV022"] = {"Id": public_v022["id"], "Url": public_v022["html_url"], "Assets": len(public_v022["assets"])}
    full = next(item for item in public_v022["assets"] if item["name"] == "Iris-0.2.2-full.nupkg")
    public_state["PublicV022Full"] = {"Name": full["name"], "Bytes": full["size"], "Digest": full.get("digest")}
    assert not public_state["PublicV023Exists"]
    assert full["size"] == source_review["PreviousPublicFull"]["Size"]
    if full.get("digest"):
        assert full["digest"] == "sha256:" + previous_hash
save(REVIEW / "public-state.json", public_state)
assert public_state["StatusCode"] == 200, public_state

inspection_dir = sorted(SESSION.glob("old-ready-readonly-*"))[-1]
inspection = read(inspection_dir / "inspection.json")
old_ready_path = sorted(SESSION.glob("old-ready-*.json"))[-1]
old_ready = read(old_ready_path)
supplement_dir = sorted(SESSION.glob("old-ready-supplement-*"))[-1]
supplement = read(supplement_dir / "supplement.json")
v2_status = read(supplement_dir / "watcher-v2-status.json")
v2_events = [json.loads(row) for row in (supplement_dir / "process-events-v2.jsonl").read_text(encoding="utf-8-sig").splitlines() if row]
provider_requests = [json.loads(row) for row in (supplement_dir / "provider-requests.jsonl").read_text(encoding="utf-8-sig").splitlines() if row]
assert inspection["ProductSource"]["Version"] == old_ready["InstalledSource"]["Version"] == "0.2.2"
assert inspection["CheckForUpdates"] is False and supplement["CheckForUpdates"] is False
assert len(inspection["SyntheticProfiles"]) == 1 and inspection["SyntheticProfiles"][0]["Enabled"] is True and inspection["SyntheticProfiles"][0]["HasCredentialSetReference"] is True
assert any(row["Path"] == "/v1/chat/completions" and row["Status"] == 200 and row["SyntheticCredentialMatched"] is True for row in provider_requests)
baseline = next(row["Identity"] for row in v2_events if row["Kind"] == "Baseline" and row["Identity"]["Name"] == "Iris.App.exe")
assert baseline["Pid"] == 6604 and baseline["CreatedUtc"] == "2026-11-02T04:25:01.2204360Z"
assert v2_status["Ready"] is True and v2_status["Failure"] is None and v2_status["RecorderPid"] == 6956
assert old_ready["Processes"][0]["Pid"] == baseline["Pid"]
assert not any("0.2.3" in row["Name"] for row in old_ready["Packages"])
database = supplement_dir / "history.db"
connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
connection.row_factory = sqlite3.Row
integrity = connection.execute("PRAGMA integrity_check").fetchall()
history = [dict(row) for row in connection.execute("SELECT h.id,h.created_at,h.origin,h.source_text,r.provider_id,r.provider_name,r.model,r.result_text FROM translation_history h JOIN translation_result r ON r.history_id=h.id WHERE h.source_text=?", ("Iris acceptance check",))]
connection.close()
assert [tuple(row) for row in integrity] == [("ok",)]
assert len(history) == 1 and history[0]["result_text"] == "Iris 公共升级验证" and history[0]["provider_id"] == inspection["SyntheticProfiles"][0]["Id"]
assert sha(database) == next(row["Sha256"] for row in old_ready["DataHashes"] if row["Path"] == "history.db")
probe_path = sorted(SESSION.glob("process-lifecycle-probe-*.json"))[-1]
probe = read(probe_path)
assert probe["Passed"] is True and probe["ActualExitCode"] == 0
assert {row["Kind"] for row in probe["Events"]} == {"Started", "Exited"}
ARCHIVE.mkdir(parents=True, exist_ok=False)
copy_file(old_ready_path, ARCHIVE / old_ready_path.name)
copy_file(probe_path, ARCHIVE / probe_path.name)
copy_file(MANUAL / "output/process-lifecycle-encoded-launch-3.json", ARCHIVE / "process-lifecycle-encoded-launch-3.json")
copy_file(MANUAL / "output/process-lifecycle-launch-2.log", ARCHIVE / "original-missing-input-file-launch-2.log")
copy_file(REVIEW / "tool-results.json", ARCHIVE / "tool-results.json")
for name in ("candidate-source-review.json", "public-state.json", "public-release-list.body"):
    copy_file(REVIEW / name, ARCHIVE / name)
for folder, relative in ((inspection_dir, "inspection"), (supplement_dir, "prepublication-supplement")):
    for path in sorted(folder.iterdir()):
        if path.is_file() and path.suffix in (".json", ".jsonl", ".log"):
            copy_file(path, ARCHIVE / relative / path.name)
for path in sorted((MANUAL / "input").iterdir()):
    if path.suffix.lower() in (".ps1", ".cmd"):
        copy_file(path, ARCHIVE / "manual-input" / path.name)
files = [{"Path": path.relative_to(ARCHIVE).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)} for path in sorted(ARCHIVE.rglob("*")) if path.is_file()]
binding = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "UserReply": "开关已保存、翻译成功、历史可打开", "SandboxId": "590f8848-f6b4-4bdb-a151-85c4a4f19305", "SessionDirectory": str(SESSION), "OfficialV022InstalledPayloadPreviouslyVerified": 97, "SyntheticOldDataReadyVerified": True, "OldNormalApp": baseline, "SettingsSha256": inspection["SettingsSha256"], "SecretsSha256": inspection["SecretsSha256"], "SecretsContentsCollected": False, "HistorySnapshot": {"Path": str(database), "Sha256": sha(database), "IntegrityCheck": "ok", "Rows": history}, "ProviderRequests": provider_requests, "LifecycleProbePassed": True, "LifecycleProbeIsProductionUpgrade": False, "ObserverV2Ready": True, "ObserverPid": 6956, "V023Source": SOURCE, "V023Candidate": str(CANDIDATE), "V023PublicAssets": 18, "V023PublicationAuthorized": False, "ProductionUiUpgradeVerified": False, "RestartCountVerified": False, "PostUpdateUninstallVerified": False, "OriginalLaunchFailure": "Two -File probe launch attempts returned Sandbox ExitCode -196608; the raw second log states that the new input file did not exist in the existing projection. The exact-source EncodedCommand probe passed; the same input root was additionally shared read-only as C:\\IrisAcceptanceUpgrade for the new helpers.", "OriginalProductLogObservations": "Initial startup update warning and a pre-save /v1/models 401 remain in raw logs. The later actual translation POST returned 200 with SyntheticCredentialMatched=true and persisted the expected history result.", "Files": files}
save(ARCHIVE / "binding.json", binding)
print(json.dumps({"Archive": str(ARCHIVE), "SourceEntriesMatched": 720, "PublicAssets": 18, "OldAppPid": 6604, "ObserverPid": 6956, "SyntheticHistoryRows": len(history), "OldReadyVerified": True, "V023PublicExists": public_state["PublicV023Exists"], "PublicationAuthorized": False}, ensure_ascii=False, indent=2))
