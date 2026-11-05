"""Archive the fixed baseline, new candidate, official API supplement and original capture failures."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
SOURCE = "227b3d88938543766a27d86a94ade52f9e5b89b7"
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
INPUT = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/input"
RAW = ROOT / "tasks/done/T-039C.evidence/v0210-review-command-originals-20261105-1"
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
candidate = json.loads((ROOT / "tasks/done/T-039C.evidence/next-source-v0210-ui-acceptance-20261105-1/final-review.json").read_text(encoding="utf-8-sig"))
assert candidate["SourceCommit"] == SOURCE and candidate["Manual029BaselineVerified"]
assert candidate["PublicAssets"] == 20 and candidate["PayloadEntries"] == 97 and not candidate["PublicationAuthorized"]
RAW.mkdir(exist_ok=False)


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(source.read_bytes())


labels = ["v029-after-user-feedback-state-readonly-1", "v029-after-user-feedback-payload-readonly-1", "v029-after-user-feedback-detail-readonly-1",
    "v029-manual-fixed-baseline-independent-review-1", "v029-new-translation-state-readonly-2", "v029-new-translation-payload-readonly-2",
    "v029-new-translation-detail-readonly-2", "v029-manual-fixed-baseline-independent-review-2", "v029-capture-diagnostic-readonly-1",
    "v029-review-input-readonly-alias-1", "v029-capture-diagnostic-readonly-2", "v029-manual-fixed-baseline-independent-review-3",
    "v0210-fixed029-old-ready-readonly-1", "v0210-fixed029-old-detail-readonly-1", "v0210-fixed029-baseline-and-final-candidate-binding-1",
    "v0210-reviewed-source-local-merge-1", "v0210-candidate-independent-review-1", "v029-official-api-supplement-1"]
for label in labels:
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), RAW / "commands" / (label + suffix))
for name in ("save-update-evidence.ps1", "diagnose-v029-capture-readonly.ps1", "inspect-v029-progress-readonly.ps1"):
    copy(INPUT / name, RAW / "capture-source" / name)
copy(ROOT / "tasks/T-039C.coordination/review_manual_fixed_v029_before_new_text_20261105.py.txt", RAW / "first-review-source.py.txt")
for name in ("updated-supplement-20261105T1334185336685Z", "updated-supplement-20261105T1337036142082Z"):
    for path in sorted((SESSION / name).rglob("*")):
        if path.is_file():
            copy(path, RAW / "original-incomplete-captures" / name / path.relative_to(SESSION / name))
for path in sorted(SESSION.glob("capture-v029-diagnostic-*.json")):
    copy(path, RAW / path.name)
latest = ("**2026-11-05 13:48 UTC 最新有效状态**：唯一T-039C，0.2.9用户已手工安装，实际5672/PID/13:15:09.5871510Z、97正式载荷、"
    "简中版本截图/开关false和原配置/秘密/旧历史匹配。用户初反馈和随后‘翻译正常’均保留；补做‘Iris fixed baseline 029’后13:34:01新POST200/"
    "虚构凭据匹配和新历史row2与原row1全部独立通过，修正版029基线已完整归档，截图已实际视觉检查。初次缺新请求、辅助退出1/缺supplement、"
    "新文件投影-196608和后一次采集完成原件保持，未凭推断改记原错误原因。029官方API补验通过，Release id435193220，19资产及两份原ZIP官网/API digest匹配。"
    "下一0210固定source227b3d88938543766a27d86a94ade52f9e5b89b7/tree69d4faf228d9d0b98d77ecb4a8dac582ad33421b；仅发布计划/说明，产品/测试/workflow/依赖保持正式029。"
    "Release0/0、full3771/3769成功/0失败/2既有183跳过，唯一candidate1的20资产/97载荷/721来源blob/工作字节、独立tree和正式002/027/029历史全部通过。"
    "source已正常本地合并，审阅原件/index字节和执行预检正在准备；0210未授权、无tag/Release，不推分支。完整归档及预检后仅请求v0.2.10→固定source。"
    "正式新资产通过后用户真实029 UI逐段检查/下载Ready/确认安装/native顺序/一次正常新进程/旧两条历史和新翻译/卸载保留数据完成C。"
    "首轮002→027 sharing32失败、手工027修复及手工029安装均保持各自结果；C/父T039/T040/NFR及冷.NET安装性能/M6延期未结案。"
    "当前入口：[0210检查点](F:/Iris/tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V0210_20261105.md)。")
handoffs = ["specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261105.md"]
for name in handoffs:
    path = ROOT / name
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    matches = [i for i, line in enumerate(lines) if line.startswith("**2026-11-05 12:58 UTC ")]
    assert len(matches) == 1
    lines[matches[0]] = latest
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
raw_dirs = ["ci-v029-api-supplement-20261105-1", "manual-fixed-v029-20261105-1", "manual-old-ready-v029-before-v0210-20261105-1",
    "next-source-v0210-ui-acceptance-20261105-1", RAW.name]
raw_paths = [path for name in raw_dirs for path in sorted((ROOT / "tasks/done/T-039C.evidence" / name).rglob("*")) if path.is_file()]
pub = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1"
raw_paths.append(pub / "state.json")
for name in ("20261105T132712621568Z-artifacts", "20261105T132747468113Z-run", "20261105T132747485624Z-jobs", "20261105T132747498580Z-release"):
    raw_paths.extend(path for path in sorted((pub / "observations" / name).iterdir()) if path.is_file())
explicit = handoffs + ["tasks/T-039C.coordination/" + name for name in (
    "CURRENT_RELEASE_GATE_V029_20261105.md", "CURRENT_RELEASE_GATE_V0210_20261105.md", "PREPUBLICATION_REVIEW_V0210_20261105.md",
    "v029-manual-user-feedback-20261105-1.json", "v029-manual-translation-feedback-20261105-1.json", "review_manual_fixed_v029_20261105.py",
    "finalize_fixed029_baseline_20261105.py", "review_v0210_candidate_20261105.py", "archive_v0210_review_20261105.py")]
relative_raw = [path.relative_to(ROOT).as_posix() for path in raw_paths]
paths = sorted(set(explicit + relative_raw))
assert not any(name.startswith(".codex/") or name.startswith("artifacts/") for name in paths)
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
        assert batch.stdout.readline().decode("ascii").split() == [index[name], "blob", str(len(raw))]
        assert batch.stdout.read(len(raw)) == raw and batch.stdout.read(1) == b"\n"
        verified.append({"Path": name, "Bytes": len(raw), "Sha256": hashlib.sha256(raw).hexdigest(), "GitBlob": index[name]})
finally:
    batch.stdin.close()
    assert batch.wait() == 0
record = ROOT / "tasks/T-039C.coordination/v0210-review-index-byte-check-1.json"
record.open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": SOURCE, "VerifiedRawFiles": len(verified),
    "VerifiedRawBytes": sum(row["Bytes"] for row in verified), "Files": verified}, indent=2) + "\n")
subprocess.run(["git", "add", "--", record.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--cached", "--check", "--", *explicit], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--quiet", SOURCE, "--", "src", "tests", "build", ".github", "docs", "README.md", "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"], cwd=ROOT, check=True)
print(json.dumps({"RawFilesVerified": len(verified), "RawBytesVerified": sum(row["Bytes"] for row in verified),
    "NextSource": SOURCE, "Fixed029BaselineVerified": True, "PublicationAuthorized": False}, indent=2))
