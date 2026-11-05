"""Stage only this review's named evidence and verify raw index blob bytes."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
SOURCE = "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"
RAW_RECORDS = ROOT / "tasks/done/T-039C.evidence/v029-review-command-originals-20261105-1"
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
subprocess.run(["git", "merge-base", "--is-ancestor", SOURCE, "HEAD"], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--quiet", SOURCE, "--", "src", "tests", "build", ".github", "docs", "README.md",
                "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"], cwd=ROOT, check=True)
summary = ("**2026-11-05 12:20 UTC 最新有效状态**：唯一活跃 T-039C，由本主会话实施、用户手工产品操作。"
    "最终 0.2.8 已授权且仅推版本标签→0c9b0a1，实际 CI42015639513/attempt1 建测失败、publish skipped，"
    "没有正式 0.2.8 资产；原tag/ZIP/官网完整digest/51原件和源保持，REST补验403仍明确保留。"
    "最终 0.2.9 source0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2/treefcb432c3df3b5930bbc2900f43235384130b97ae"
    "已正常本地整合；Release0警告/错误，全量3771/3769成功/0失败/2既有error183跳过，"
    "独立721来源/19资产/97载荷及正式027/002历史核对通过。冷启动host原失败、首轮本地主题失败和受控反例完整保留；"
    "WPF初始化与主题集合修正保留生产代码和原断言，host13/主题8通过且完整主题区间不与其他App项重叠。"
    "同一Sandbox刷新后仍正式027/PID8820、记录器8152/服务7328、开关false，三项原数据哈希、原历史与日志前缀保持，"
    "无未完成事务。0.2.9/分支/后继版本未授权；审阅原件、index字节核对与最终只读预检完成后仅请求新029标签。"
    "正式资产通过后用户先手工安装修正版，再单独准备/确认下一版真实UI更新/一次重启/数据和卸载验收。"
    "C/父T039/T040/NFR、首次.NET安装性能和M6延期均保持；不在宿主安装、不读改.codex/私钥、不创建聊天/子代理。"
    "当前入口：[0.2.9检查点](F:/Iris/tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V029_20261105.md)与"
    "[当前交接](F:/Iris/tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V029_20261105.md)。")
handoffs = ["specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/current_task.md"]
for name in handoffs:
    path = ROOT / name
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    matches = [i for i, line in enumerate(lines) if line.startswith("**2026-11-05 12:08 UTC ")]
    assert len(matches) == 1
    lines[matches[0]] = summary
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
session = ROOT / "tasks/T-039C.coordination/SESSION_STATUS_20261105.md"
session.write_text(summary + "\n\n" + session.read_text(encoding="utf-8-sig"), encoding="utf-8", newline="\n")
RAW_RECORDS.mkdir(exist_ok=False)
labels = ["v028-final-approved-tag-publication-2", "v028-original-ci-failure-archive-download-1",
    "v028-ci-and-v029-original-local-failure-archive-1", "v029-ci-host-fixture-worktree-1",
    "v029-host-dispatcher-counterexample-before-1", "v029-host-dispatcher-counterexample-after-1", "v029-host-composition-after-1",
    "v029-source-stage-1", "v029-source-commit-1", "v029-full-validation-after-1",
    "v029-theme-dispatcher-interference-before-1", "v029-theme-dispatcher-interference-before-2",
    "v029-theme-preview-after-1", "v029-theme-preview-after-2", "v029-final-source-diff-check-2",
    "v029-final-source-stage-2", "v029-final-source-commit-2", "v029-full-validation-after-2",
    "v029-package-public-base-candidate-1", "v029-candidate-independent-review-1",
    "v029-current-sandbox-list-1", "v029-current-old-ready-readonly-1", "v029-current-old-processes-readonly-1",
    "v029-current-old-ready-independent-archive-1", "v029-current-old-ready-independent-archive-2",
    "v029-final-reviewed-source-local-merge-1"]
for label in labels:
    for suffix in (".log", ".command.json"):
        source = ROOT / "tasks/T-039C.coordination" / (label + suffix)
        (RAW_RECORDS / source.name).open("xb").write(source.read_bytes())
for name in ("run_review.py", "archive_old_ready_v029_before_log_suffix_20261105.py.txt"):
    source = ROOT / "tasks/T-039C.coordination" / name
    (RAW_RECORDS / source.name).open("xb").write(source.read_bytes())
raw_directories = ["ci-v028-original-42015639513-1", "ci-v028-failure-analysis-20261105-1",
    "next-source-v029-local-failure-20261105-1", "next-source-v029-final-20261105-1",
    "manual-old-ready-v027-before-v029-20261105-1", RAW_RECORDS.name]
raw_paths = []
for name in raw_directories:
    directory = ROOT / "tasks/done/T-039C.evidence" / name
    raw_paths.extend(path for path in sorted(directory.rglob("*")) if path.is_file())
publication = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.8-20261105-1"
raw_paths.extend(path for path in sorted(publication.rglob("*")) if path.is_file() and path.name != "previous-public-full.body")
explicit = handoffs + ["tasks/T-039C.coordination/SESSION_STATUS_20261105.md",
    "tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V028_FINAL_20261105.md",
    "tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V029_20261105.md",
    "tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V029_20261105.md",
    "tasks/T-039C.coordination/CI_V028_FAILURE_SCOPE_20261105.md",
    "tasks/T-039C.coordination/v028-final-user-approval-20261105-2.json",
    "tasks/T-039C.coordination/observe_v028_web_20261105.py", "tasks/T-039C.coordination/observe_v028_release_20261105.py",
    "tasks/T-039C.coordination/archive_v028_failure_and_v029_initial_20261105.py",
    "tasks/T-039C.coordination/review_v029_candidate_20261105.py", "tasks/T-039C.coordination/archive_old_ready_v029_20261105.py",
    "tasks/T-039C.coordination/prepare_v029_review_archive_20261105.py"]
assert all((ROOT / name).is_file() for name in explicit)
relative_raw = [path.relative_to(ROOT).as_posix() for path in raw_paths]
paths = sorted(set(explicit + relative_raw))
assert not any(name.startswith(".codex/") or name.startswith("artifacts/") for name in paths)
pathspec = b"".join(name.encode("utf-8") + b"\0" for name in paths)
subprocess.run(["git", "-c", "core.longpaths=true", "add", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"],
               cwd=ROOT, input=pathspec, check=True)
index = {}
for row in subprocess.check_output(["git", "ls-files", "--stage", "-z"], cwd=ROOT).decode().strip("\0").split("\0"):
    metadata, path = row.split("\t")
    mode, blob, stage = metadata.split()
    if path in relative_raw:
        assert mode == "100644" and stage == "0"
        index[path] = blob
assert set(index) == set(relative_raw)
batch = subprocess.Popen(["git", "cat-file", "--batch"], cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
verified = []
try:
    for name in relative_raw:
        path = ROOT / name
        batch.stdin.write((index[name] + "\n").encode("ascii"))
        batch.stdin.flush()
        header = batch.stdout.readline().decode("ascii").split()
        assert header[0] == index[name] and header[1] == "blob"
        remaining = int(header[2])
        assert remaining == path.stat().st_size
        digest = hashlib.sha256()
        while remaining:
            part = batch.stdout.read(min(1024 * 1024, remaining))
            assert part
            digest.update(part)
            remaining -= len(part)
        assert batch.stdout.read(1) == b"\n"
        with path.open("rb") as stream:
            assert digest.hexdigest() == hashlib.file_digest(stream, "sha256").hexdigest()
        verified.append({"Path": name, "Bytes": path.stat().st_size, "Sha256": digest.hexdigest(), "GitBlob": index[name]})
finally:
    batch.stdin.close()
    assert batch.wait() == 0
record = ROOT / "tasks/T-039C.coordination/v029-review-archive-index-byte-check-1.json"
record.open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": SOURCE,
    "VerifiedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "VerifiedRawFiles": len(verified),
    "VerifiedRawBytes": sum(row["Bytes"] for row in verified), "UnrelatedUntrackedStaged": False,
    "PreviousPublicFullBodyRetainedOutsideGit": True, "Files": verified}, indent=2) + "\n")
subprocess.run(["git", "add", "--", record.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--cached", "--check", "--", *explicit], cwd=ROOT, check=True)
staged = subprocess.check_output(["git", "diff", "--cached", "--name-only", "-z"], cwd=ROOT).decode().strip("\0").split("\0")
assert set(staged) == set(paths) | {record.relative_to(ROOT).as_posix()}
print(json.dumps({"StagedFiles": len(staged), "RawEvidenceFilesVerified": len(verified),
                  "RawEvidenceBytesVerified": sum(row["Bytes"] for row in verified), "SourceCommit": SOURCE,
                  "UnrelatedFilesStaged": False}, indent=2))
