"""Stage only this review and verify that Git retains every evidence file byte for byte."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess

ROOT = Path("F:/Iris")
BASE = ROOT / "tasks/done/T-039C.evidence"
FINAL = BASE / "final-review-v026-20261103-1"
SOURCE = "c32fcb283e417ab11715c46207fdc8cfde4baedf"

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

folders = ["publication-v0.2.5-20261102-1", "ci-v025-original-41461057191-1",
           "ci-v025-before-fix-20261103-1", "ci-v025-query-counterexample-20261103-1",
           "next-source-v026-20261103-1", "recorder-v2-failure-20261103-1",
           "recorder-v3-native-20261103-1", "manual-old-ready-before-v026-20261103-1"]
for name in folders:
    folder = BASE / name
    binding = folder / "binding.json"
    if binding.exists():
        for item in read(binding).get("Files", []):
            path = ROOT / item["Path"] if item["Path"].startswith("tasks/done/") else folder / item["Path"]
            assert path.stat().st_size == item["Bytes"] and digest(path) == item["Sha256"]
review = read(BASE / "next-source-v026-20261103-1/review.json")
assert review["SourceCommit"] == SOURCE and review["PublicAssets"] == 18 and review["PayloadEntries"] == 97
assert review["FullTests"] == {"Total": 3758, "Passed": 3756, "Failed": 0, "Skipped": 2}
state = read(BASE / "publication-v0.2.5-20261102-1/state.json")
assert state["TagRemoteVerified"] is True and state["ReleasePublic"] is False and state["CiRunId"] == 41461057191
old = read(BASE / "manual-old-ready-before-v026-20261103-1/binding.json")
assert old["Version"] == "0.2.2" and old["RecorderVersion"] == 3 and old["RecorderReady"] is True
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
subprocess.run(["git", "merge-base", "--is-ancestor", SOURCE, "HEAD"], cwd=ROOT, check=True)
subprocess.run(["git", "diff", SOURCE, "--quiet", "--", "src", "tests", "build", ".github", "docs", "README.md", "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"], cwd=ROOT, check=True)
FINAL.mkdir(exist_ok=False)
archive = ROOT / "artifacts/t039c/ci-v025-original-41461057191-1.zip"
assert archive.stat().st_size == 506060 and digest(archive) == "e3916ed568433386720fbacf81ab41c2c33016f30792fd0c32680ffbd5d69f0f"
shutil.copyfile(archive, FINAL / "original-ci-v025.zip")
for label in ("v026-candidate-independent-review-1", "v026-reviewed-source-local-merge-1", "v026-remote-before-approval-2"):
    metadata = read(ROOT / "tasks/T-039C.coordination" / (label + ".command.json"))
    log = ROOT / "tasks/T-039C.coordination" / (label + ".log")
    assert metadata["exitCode"] == 0 and digest(log) == metadata["logSha256"]
    for suffix in (".command.json", ".log"):
        shutil.copyfile(ROOT / "tasks/T-039C.coordination" / (label + suffix), FINAL / (label + suffix))
(FINAL / "FINAL_REVIEW.md").write_text("# T-039C 0.2.6 本地发布审阅\n\n"
    "0.2.5 已按用户确认推送，但实际 CI 建测失败，打包和发布跳过。官方 digest 匹配的原 ZIP、51 原件、"
    "修前源码和受控查询失败均保持。新源码保留原功能断言并观察异步自启状态完成，定向18/18、"
    "完整Release0/0和3758总计/3756成功/0失败/2既有error183跳过已核对。\n\n"
    "固定发布源码c32fcb283e417ab11715c46207fdc8cfde4baedf，规范来源树c39947f03475872a5178929e32b078a383d537e2；"
    "721提交blob/工作字节、18资产、97载荷、正式旧0.2.2full及全部原日志匹配。"
    "本地外层10274bb583095de434874cb15c95839c503d3be4bd21fc6d7f5c38102401ed02；"
    "full e478bb4b6accab61e5e7dc25229f8306771abf6c914026f75700ad7f45e75d8b。\n\n"
    "原V2只读记录器的缺失事件清理错误及停止原件已冻结。V3/4604在PowerShell5.1及真实native辅助进程上"
    "证明队列已不存在的事件被正确处理、一次辅助启动/退出0均可记录；原0.2.2/6604与三项数据哈希未变。"
    "辅助探针不是生产Iris，也不代替真实升级/native退出顺序/正常重启次数。\n\n"
    "新源码已正常本地合并，main/codex远端285df3a和所有旧标签保持，0.2.6未创建或推送。"
    "PREPUBLICATION_REVIEW_V026_20261103.md和MANUAL_UPDATE_V026_20261103.md已完整。"
    "下一步只在用户单独确认后创建、推送指向固定源码的v0.2.6；不推分支。"
    "随后实际CI/草稿及公开资产字节读回通过，才交用户逐段真实UI升级和卸载。"
    "C/父T-039、T-040/NFR未结案，M6延期。\n", encoding="utf-8", newline="\n")
records = [{"Path": path.relative_to(FINAL).as_posix(), "Bytes": path.stat().st_size, "Sha256": digest(path)}
           for path in sorted(FINAL.rglob("*")) if path.is_file()]
(FINAL / "binding.json").write_text(json.dumps({"SourceCommit": SOURCE, "SourceGitTree": review["SourceGitTree"],
    "LocalMergeCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT).decode().strip(),
    "PublicationAuthorized": False, "Files": records}, indent=2) + "\n", encoding="utf-8", newline="\n")
regular = ["specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "specs/DEVELOPER_RECOVERY.md",
           "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261102.md",
           "tasks/T-039C.coordination/SESSION_STATUS_20261103.md", "tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V026_20261103.md",
           "tasks/T-039C.coordination/MANUAL_UPDATE_V026_20261103.md"]
scripts = ["archive_ci_v025_failure_20261103.py", "prepare_v025_query_counterexample_20261103.py",
           "review_v026_candidate_20261103.py", "sync_v026_status_20261103.py", "observe_v026_preparation_20261103.py",
           "freeze_v026_old_ready_20261103.py", "prepare_v3_recorder_20261103.py", "manual-process-watch-v3.ps1",
           "start-process-watch-v3.ps1", "check_recorder_powershell51.ps1", "launch_v3_recorder_20261103.py",
           "archive_v3_recorder_20261103.py", "finalize_v026_review_20261103.py"]
regular += ["tasks/T-039C.coordination/" + name for name in scripts]
evidence = ["tasks/done/T-039C.evidence/" + name for name in folders + [FINAL.name]]
subprocess.run(["git", "add", "--", *regular], cwd=ROOT, check=True)
subprocess.run(["git", "add", "-f", "--", *evidence], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--cached", "--check", "--", *regular], cwd=ROOT, check=True)
raw_files = [path for folder in evidence for path in sorted((ROOT / folder).rglob("*")) if path.is_file()]
for path in raw_files:
    relative = path.relative_to(ROOT).as_posix()
    indexed = subprocess.check_output(["git", "show", ":" + relative], cwd=ROOT)
    assert indexed == path.read_bytes(), "Git index changed raw evidence: " + relative
proof = {"SourceCommit": SOURCE, "EvidenceIndexFilesMatched": len(raw_files), "OriginalCiZipIndexMatched": True,
         "OnlyReviewPathsStaged": True, "CodexDirectoryStaged": False, "PublicationAuthorized": False}
(FINAL / "index-byte-check.json").write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8", newline="\n")
subprocess.run(["git", "add", "--", str((FINAL / "index-byte-check.json").relative_to(ROOT))], cwd=ROOT, check=True)
staged = subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).decode().splitlines()
assert all(not name.startswith(".codex/") for name in staged)
print(json.dumps({**proof, "StagedPaths": len(staged)}, indent=2))
