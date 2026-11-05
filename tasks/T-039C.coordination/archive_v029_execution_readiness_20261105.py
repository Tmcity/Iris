"""Freeze the completed read-only executor, rejected old approvals and official CI supplement."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
SOURCE = "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"
REVIEW = "7662ed580eb924756885db7ab3a7d7a04e8ea8ea"
PREFLIGHT = ROOT / "tasks/T-039C.coordination/v029-execution-preflight-20261105T122533411998Z"
REPORT = ROOT / "tasks/done/T-039C.evidence/v029-execution-readiness-20261105-1"
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
state = json.loads((PREFLIGHT / "state.json").read_text(encoding="utf-8-sig"))
assert state["Stage"] == "preflight-verified-awaiting-explicit-user-approval"
assert state["PublicationAuthorized"] is False and state["PushRequestAttempted"] is False and state["Failure"] is None
assert state["SourceCommit"] == SOURCE and state["ReviewArchiveCommit"] == REVIEW
assert not (ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1").exists()
assert not subprocess.check_output(["git", "tag", "--list", "v0.2.9"], cwd=ROOT).strip()
body = PREFLIGHT / "previous-public-full.body"
assert body.stat().st_size == 64627391
with body.open("rb") as stream:
    assert hashlib.file_digest(stream, "sha256").hexdigest() == "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8"
REPORT.mkdir(exist_ok=False)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    target.open("xb").write(source.read_bytes())


for path in sorted(PREFLIGHT.rglob("*")):
    if path.is_file() and path.name != "previous-public-full.body":
        copy(path, REPORT / "preflight" / path.relative_to(PREFLIGHT))
labels = ["v029-review-evidence-stage-and-index-check-1", "v029-final-review-archive-commit-1",
    "v029-prepare-execution-materials-1", "v029-executor-missing-approval-guard-1", "v029-executor-old-approval-guard-1",
    "v029-executor-readonly-preflight-1", "v028-official-api-metadata-supplement-1"]
for label in labels:
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "raw-command-records" / (label + suffix))
for label in ("v029-executor-missing-approval-guard-1", "v029-executor-old-approval-guard-1"):
    record = json.loads((REPORT / "raw-command-records" / (label + ".command.json")).read_text(encoding="utf-8-sig"))
    assert record["exitCode"] == 2
copy(ROOT / "tasks/T-039C.coordination/execute_v029_20261105.py", REPORT / "executor/execute_v029_20261105.py")
copy(ROOT / "tasks/T-039C.coordination/prepare_v029_execution_20261105.py", REPORT / "executor/prepare_v029_execution_20261105.py")
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size,
          "Sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": SOURCE,
    "ReviewArchiveCommit": REVIEW, "PreflightPassed": True, "MissingApprovalRejectedBeforeMutation": True,
    "Old028ApprovalRejectedBeforeMutation": True, "TagCreated": False, "PublicationAuthorized": False,
    "FreshPreviousPublicFullBytes": 64627391, "FreshPreviousPublicFullSha256": "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8",
    "PreviousFullBodyRetainedOutsideGit": str(body), "Files": files}, indent=2) + "\n")
handoffs = ["specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261105.md"]
latest = ("**2026-11-05 12:30 UTC 最新有效状态**：T-039C为唯一活跃工单。最终028已授权且实际CI42015639513/attempt1失败、publish skipped；"
    "原tag/source/ZIP/51原件保持，12:28 UTC官方API的source/attempt/最终status及ZIP完整digest补验通过，Release404，原403记录保持。"
    "最终029 source0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2/treefcb432c3df3b5930bbc2900f43235384130b97ae，"
    "Release0/0、full3771/3769成功/0失败/2既有183跳过，候选19资产/97载荷/721来源及正式027/002历史独立通过。"
    "审阅归档7662ed580eb924756885db7ab3a7d7a04e8ea8ea，430原件/10626856字节与Git index一致。"
    "默认只读执行预检通过：远端refs/正式027匿名full/候选和当前Sandbox旧027、记录器/服务/开关false/原三项哈希及日志保持；"
    "缺少授权与旧028授权在任何发布目录/标签创建前拒绝。029未授权、无标签/Release；执行材料归档后仅请求v0.2.9→0b3267f。"
    "正式资产通过后用户先手工安装修正版，再单独准备和确认下一版本进行真实UI更新/一次重启/数据/卸载验收。"
    "C/父T039/T040/NFR、首次.NET安装性能及M6延期保持；不推main/codex、不重写历史、不在宿主安装、不读改.codex/私钥、不创建聊天/子代理。"
    "当前入口：[029检查点](F:/Iris/tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V029_20261105.md)和"
    "[执行准备](F:/Iris/tasks/T-039C.coordination/EXECUTION_READY_V029_20261105.md)。")
for name in handoffs:
    path = ROOT / name
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    matches = [i for i, line in enumerate(lines) if line.startswith("**2026-11-05 12:20 UTC ")]
    assert len(matches) == 1
    lines[matches[0]] = latest
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
raw_paths = [path for path in sorted(REPORT.rglob("*")) if path.is_file()]
supplement = ROOT / "tasks/done/T-039C.evidence/ci-v028-api-supplement-20261105-1"
raw_paths.extend(path for path in sorted(supplement.rglob("*")) if path.is_file())
publication = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.8-20261105-1/observations"
for name in ("20261105T122736672284Z-artifacts", "20261105T122810132579Z-run", "20261105T122810151327Z-jobs", "20261105T122810162331Z-release"):
    raw_paths.extend(path for path in sorted((publication / name).iterdir()) if path.is_file())
explicit = handoffs + ["tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V029_20261105.md",
    "tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V029_20261105.md", "tasks/T-039C.coordination/EXECUTION_READY_V029_20261105.md",
    "tasks/T-039C.coordination/execute_v029_20261105.py", "tasks/T-039C.coordination/prepare_v029_execution_20261105.py",
    "tasks/T-039C.coordination/archive_v028_api_supplement_20261105.py", "tasks/T-039C.coordination/archive_v029_execution_readiness_20261105.py"]
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
verified = []
try:
    for name in relative_raw:
        raw = (ROOT / name).read_bytes()
        batch.stdin.write((index[name] + "\n").encode("ascii")); batch.stdin.flush()
        header = batch.stdout.readline().decode("ascii").split()
        assert header == [index[name], "blob", str(len(raw))]
        actual = batch.stdout.read(len(raw))
        assert actual == raw and batch.stdout.read(1) == b"\n"
        verified.append({"Path": name, "Bytes": len(raw), "Sha256": hashlib.sha256(raw).hexdigest(), "GitBlob": index[name]})
finally:
    batch.stdin.close()
    assert batch.wait() == 0
record = ROOT / "tasks/T-039C.coordination/v029-execution-readiness-index-byte-check-1.json"
record.open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": SOURCE, "ReviewArchiveCommit": REVIEW,
    "VerifiedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "VerifiedRawFiles": len(verified),
    "VerifiedRawBytes": sum(row["Bytes"] for row in verified), "Files": verified}, indent=2) + "\n")
subprocess.run(["git", "add", "--", record.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--cached", "--check", "--", *explicit], cwd=ROOT, check=True)
staged = subprocess.check_output(["git", "diff", "--cached", "--name-only", "-z"], cwd=ROOT).decode().strip("\0").split("\0")
assert set(staged) == set(paths) | {record.relative_to(ROOT).as_posix()}
print(json.dumps({"StagedFiles": len(staged), "VerifiedRawFiles": len(verified),
    "VerifiedRawBytes": sum(row["Bytes"] for row in verified), "PublicationAuthorized": False, "TagCreated": False}, indent=2))
