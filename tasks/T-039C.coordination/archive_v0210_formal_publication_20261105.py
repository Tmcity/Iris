"""Archive exact official 0.2.10 proof, the retained 504 response and ready user-operated offer input."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
PUBLIC = ROOT / "artifacts/t039c/public-0.2.10-download-20261105-2"
PUB = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1"
INPUT = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/input"
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
REPORT = ROOT / "tasks/done/T-039C.evidence/public-v0210-reviewed-materials-20261105-1"
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
state = json.loads((PUB / "state.json").read_text(encoding="utf-8-sig"))
assert state["ReleasePublic"] and state["OfficialRestApiMetadataVerified"] and state["ReleaseId"] == 435297674
assert state["FullTests"] == {"Total":3771,"Passed":3771,"Failed":0,"Skipped":0}
REPORT.mkdir(exist_ok=False)


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(source.read_bytes())


for path in sorted(PUBLIC.iterdir()):
    if path.suffix not in {".exe", ".nupkg"}:
        copy(path, REPORT / "public-assets" / path.name)
copy(ROOT / "artifacts/t039c/public-0.2.10-download-20261105-1/RELEASES", REPORT / "original-504-RELEASES.body")
inspection = sorted(SESSION.glob("ui-v0210-input-inspection-*.json"))[-1]
copy(inspection, REPORT / "guest-ui-input-inspection.json")
for name in ("Save-Offer-V0210-State.cmd", "Capture-Offer-V0210-View.cmd", "save-ui-v0210-evidence.ps1", "inspect-ui-v0210-input-readonly.ps1",
             "save-update-evidence.ps1", "manual-capture-state.ps1", "capture-manual-view.ps1", "inspect-old-ready-readonly.ps1"):
    copy(INPUT / name, REPORT / "user-operated-input" / name)
labels = ["v0210-approved-tag-publication-1", "v0210-original-ci-artifact-readback-1", "v0210-official-anonymous-assets-readback-1",
    "v0210-public-release-index-recovery-1", "v0210-original-ci-and-official-assets-independent-1", "v0210-official-api-supplement-1",
    "v0210-ui-capture-powershell51-parse-1", "v0210-ui-input-readonly-share-1", "v0210-ui-review-input-readonly-share-1",
    "v0210-ui-input-readonly-inspection-1", "v0210-pre-offer-old-ready-readonly-1", "v0210-pre-offer-old-detail-readonly-1"]
for label in labels:
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "commands" / (label + suffix))
latest = ("**2026-11-05 14:42 UTC 最新有效状态**：唯一T-039C。用户已授权并仅推v0.2.10，tag8b3af299→source227b3d88938543766a27d86a94ade52f9e5b89b7，"
    "原refs/main/codex285df3a保持。正式CI42042550153/attempt1/package127296844225/publish127301558754成功；原3771项全部成功、0跳过。"
    "原验证ZIP509556/SHA237f8958…与发布ZIP424/SHAd08fde86…和55原件/发布回执官网/API digest一致。正式20资产/97载荷/721来源、全部官网/API摘要通过，Release id435297674。"
    "首轮单项RELEASES返回HTTP504/92字节，原记录保留，仅重读该项HTTP200后在新目录public-0.2.10-download-20261105-2验证全部20；不混用失败92字节。"
    "正式Setup116769024/SHA56166ec3e71a0fad72b1eb135e119635e63720ebe0a8fcf301f58d7ef8e053c7，full64628628/SHAdca307a0afc1827e27bc7ec2614e5ead3a30978d4c5d318e4d0d842ebc73f136。"
    "新用户只读入口C:\\IrisAcceptanceUI0210的3项Offer输入指纹在来宾匹配，PS5.1解析通过；同一029/PID5672/原数据和记录器/服务保持、检查前无0210包。"
    "已给用户第一段真实UI检查：一次Check，Capture-Offer-V0210-View和Save-Offer-V0210-State，当前等待实际Offer版本/状态反馈；不提前下载/安装。"
    "后续分段真实下载Ready、正式包与旧数据复核、确认安装/native退出和一次正常新App、两条原历史/原服务新翻译、默认保留资料卸载完成C。"
    "首轮002→027失败与手工027/029恢复保持，C/父T039/T040/NFR、首次冷.NET性能和M6延期未结案。下一版本未授权、不在宿主安装、不读改.codex/私钥、不创建聊天/子代理。"
    "当前入口：[0210检查更新清单](F:/Iris/tasks/T-039C.coordination/MANUAL_OFFER_V0210_20261105.md)。")
handoffs = ["specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261105.md"]
for name in handoffs:
    path = ROOT / name
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    matches = [i for i,line in enumerate(lines) if line.startswith("**2026-11-05 14:19 UTC ")]
    assert len(matches) == 1
    lines[matches[0]] = latest
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
raw_dirs = ["ci-v0210-original-42042550153-1", "ci-v0210-api-supplement-20261105-1", PUB.name, REPORT.name]
raw_paths = [path for name in raw_dirs for path in sorted((ROOT / "tasks/done/T-039C.evidence" / name).rglob("*"))
             if path.is_file() and path.name != "previous-public-full.body"]
scripts = ["prepare_v0210_observers_20261105.py", "observe_v0210_release_20261105.py", "observe_v0210_web_20261105.py", "prepare_v0210_readback_tools_20261105.py",
    "download_public_v0210_20261105.py", "download_v0210_ci_evidence_20261105.py", "record_v0210_ci_started_20261105.py", "recover_public_v0210_readback_20261105.py",
    "prepare_v0210_official_review_20261105.py", "review_v0210_official_web_and_ci_20261105.py", "prepare_v0210_api_supplement_20261105.py",
    "supplement_v0210_official_api_20261105.py", "archive_v0210_formal_publication_20261105.py"]
explicit = handoffs + ["tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V0210_20261105.md", "tasks/T-039C.coordination/MANUAL_OFFER_V0210_20261105.md",
    "tasks/T-039C.coordination/v0210-user-approval-20261105-1.json"] + ["tasks/T-039C.coordination/" + name for name in scripts]
relative_raw = [path.relative_to(ROOT).as_posix() for path in raw_paths]
paths = sorted(set(explicit + relative_raw))
subprocess.run(["git", "-c", "core.longpaths=true", "add", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"], cwd=ROOT,
               input=b"".join(name.encode("utf-8") + b"\0" for name in paths), check=True)
index = {}
for row in subprocess.check_output(["git", "ls-files", "--stage", "-z"], cwd=ROOT).decode().strip("\0").split("\0"):
    metadata, name = row.split("\t")
    if name in relative_raw:
        mode, blob, stage = metadata.split()
        assert mode == "100644" and stage == "0"
        index[name] = blob
assert set(index) == set(relative_raw)
batch = subprocess.Popen(["git", "cat-file", "--batch"], cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
records = []
try:
    for name in relative_raw:
        raw = (ROOT / name).read_bytes()
        batch.stdin.write((index[name]+"\n").encode("ascii")); batch.stdin.flush()
        assert batch.stdout.readline().decode().split() == [index[name],"blob",str(len(raw))]
        assert batch.stdout.read(len(raw)) == raw and batch.stdout.read(1) == b"\n"
        records.append({"Path":name,"Bytes":len(raw),"Sha256":hashlib.sha256(raw).hexdigest(),"GitBlob":index[name]})
finally:
    batch.stdin.close(); assert batch.wait() == 0
record_path = ROOT / "tasks/T-039C.coordination/v0210-formal-publication-index-byte-check-1.json"
record_path.open("x",encoding="utf-8",newline="\n").write(json.dumps({"SourceCommit":state["SourceCommit"],"VerifiedRawFiles":len(records),
    "VerifiedRawBytes":sum(row["Bytes"] for row in records),"Files":records},indent=2)+"\n")
subprocess.run(["git","add","--",record_path.relative_to(ROOT).as_posix()],cwd=ROOT,check=True)
subprocess.run(["git","diff","--cached","--check","--",*explicit],cwd=ROOT,check=True)
print(json.dumps({"RawFilesVerified":len(records),"RawBytesVerified":sum(row["Bytes"] for row in records),"Formal0210Verified":True,"UserOfferStillPending":True},indent=2))
