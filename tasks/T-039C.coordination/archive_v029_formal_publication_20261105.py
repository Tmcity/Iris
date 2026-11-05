"""Archive exact formal release proofs and ready user-operated input; stage only named files."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
PUBLIC = ROOT / "artifacts/t039c/public-0.2.9-download-20261105-1"
PUB = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1"
INPUT = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/input"
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
REPORT = ROOT / "tasks/done/T-039C.evidence/public-v029-reviewed-materials-20261105-1"
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
inspection_path = sorted(SESSION.glob("fixed-v029-input-inspection-*.json"))[-1]
inspection = json.loads(inspection_path.read_text(encoding="utf-8-sig"))
assert inspection["Root"] == "C:\\IrisAcceptanceFixed029" and inspection["ProductOperationPerformed"] is False
assert len(inspection["Rows"]) == 5
for row in inspection["Rows"]:
    assert row["Exists"] and hashlib.sha256((INPUT / row["Name"]).read_bytes()).hexdigest() == row["Sha256"]
state = json.loads((PUB / "state.json").read_text(encoding="utf-8-sig"))
assert state["ReleasePublic"] and state["AllPublicAssetBytesVerified"] and state["CiConclusion"] == "success"
REPORT.mkdir(exist_ok=False)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    target.open("xb").write(source.read_bytes())


for path in sorted(PUBLIC.iterdir()):
    if path.suffix not in {".exe", ".nupkg"}:
        copy(path, REPORT / "public-assets" / path.name)
copy(inspection_path, REPORT / "guest-input-inspection.json")
copy(INPUT / "inspect-v029-input-readonly.ps1", REPORT / "inspect-v029-input-readonly.ps1")
labels = ["v029-approved-tag-publication-1", "v029-original-ci-artifact-readback-1",
    "v029-official-anonymous-assets-readback-1", "v029-original-ci-and-published-assets-independent-archive-1",
    "v029-official-manual-install-materials-1", "v029-manual-verifier-powershell51-parse-1",
    "v029-sandbox-fixed-input-readonly-share-1", "v029-sandbox-fixed-input-readonly-inspection-1"]
for label in labels:
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "raw-command-records" / (label + suffix))
latest = ("**2026-11-05 12:58 UTC 最新有效状态**：唯一T-039C。用户已确认仅0.2.9，annotated tag3c3a6152→source0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2，"
    "全部旧refs/main/codex285df3a保持。正式CI42028799541/attempt1/package127249641900/publish127253344007成功；原full3771/3770成功/0失败/"
    "1既有error183剪贴板环境跳过，host13/theme8/http15全部通过。原验证ZIP509631字节/SHAecc5a3eb…、发布ZIP422字节/SHA9b194a0b…"
    "精确匹配官网完整digest，55原件和published-and-listed/Public=true回执已冻结。正式匿名19资产/97载荷/721来源及官网每项SHA均匹配；"
    "正式Setup116768884/SHA69ab96bb1f700b21fa430d69eab85d6d4a5c606c377424222f0f97af0e7f2891，full64628500/SHA42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e。"
    "当前API共享限额403/额度0、reset13:25:19UTC，原响应保留，原028API补验已通过，新的029API补验仍待窗口；不持续请求或更换身份。"
    "用户手工正式029安装材料已准备，PS5.1解析通过，新只读入口C:\\IrisAcceptanceFixed029的5个输入指纹实际匹配，当前同一Sandbox仍运行；"
    "实际029安装/截图/新翻译尚未由用户完成。按MANUAL_INSTALL_FIXED_V029_20261105.md由用户正式下载安装、看版本/截图、原历史与原服务新翻译、Save-Fixed-V029-State保存。"
    "旧027手工修复与首轮002→027失败保持，029手工安装也不替代真实UI升级。用户完成029及数据复核后再准备并单独确认下一正式版，执行真正UI/native一次重启/数据/卸载验收。"
    "C/父T039/T040/NFR、首次.NET安装性能和M6延期未结案；不在宿主安装、不读改.codex/私钥、不创建聊天/子代理。"
    "当前入口：[029手工安装清单](F:/Iris/tasks/T-039C.coordination/MANUAL_INSTALL_FIXED_V029_20261105.md)。")
handoffs = ["specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261105.md"]
for name in handoffs:
    path = ROOT / name
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    positions = [i for i, line in enumerate(lines) if line.startswith("**2026-11-05 12:42 UTC ")]
    assert len(positions) == 1
    lines[positions[0]] = latest
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
raw_paths = []
for name in ("ci-v029-original-42028799541-1", "manual-fixed-v029-input-20261105-1", REPORT.name, PUB.name):
    directory = ROOT / "tasks/done/T-039C.evidence" / name
    raw_paths.extend(path for path in sorted(directory.rglob("*")) if path.is_file() and path.name != "previous-public-full.body")
scripts = ["prepare_v029_observers_20261105.py", "observe_v029_release_20261105.py", "observe_v029_web_20261105.py",
    "prepare_v029_public_downloader_20261105.py", "download_public_v029_20261105.py", "download_v029_ci_evidence_20261105.py",
    "record_v029_ci_started_20261105.py", "review_v029_official_web_and_ci_20261105.py", "prepare_manual_fixed_v029_20261105.py",
    "archive_v029_formal_publication_20261105.py"]
explicit = handoffs + ["tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V029_20261105.md",
    "tasks/T-039C.coordination/MANUAL_INSTALL_FIXED_V029_20261105.md", "tasks/T-039C.coordination/v029-user-approval-20261105-1.json"]
explicit += ["tasks/T-039C.coordination/" + name for name in scripts]
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
        header = batch.stdout.readline().decode("ascii").split()
        assert header == [index[name], "blob", str(len(raw))]
        assert batch.stdout.read(len(raw)) == raw and batch.stdout.read(1) == b"\n"
        verified.append({"Path": name, "Bytes": len(raw), "Sha256": hashlib.sha256(raw).hexdigest(), "GitBlob": index[name]})
finally:
    batch.stdin.close()
    assert batch.wait() == 0
record = ROOT / "tasks/T-039C.coordination/v029-formal-publication-index-byte-check-1.json"
record.open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": state["SourceCommit"],
    "VerifiedRawFiles": len(verified), "VerifiedRawBytes": sum(row["Bytes"] for row in verified), "Files": verified}, indent=2) + "\n")
subprocess.run(["git", "add", "--", record.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--cached", "--check", "--", *explicit], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--quiet", state["SourceCommit"], "--", "src", "tests", "build", ".github", "docs", "README.md",
                "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"], cwd=ROOT, check=True)
print(json.dumps({"StagedPaths": len(paths) + 1, "RawFilesVerified": len(verified), "RawBytesVerified": sum(row["Bytes"] for row in verified),
                  "FormalReleaseVerified": True, "UserInstallationActuallyPerformed": False}, indent=2))
