"""Record the observed approved tag and first live CI attempt without repeating publication."""
from pathlib import Path
import json

ROOT = Path("F:/Iris")
path = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1/state.json"
state = json.loads(path.read_text(encoding="utf-8-sig"))
assert state["TagRemoteVerified"] and state["TagObject"] == "3c3a615234c77d61441ce60c6cbfe07f02f37f65"
assert state["SourceCommit"] == "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"
state.update(CiRunId=42028799541, CiRunAttempt=1, PackageJobId=127249641900,
             CiStatus="in_progress", CiConclusion=None, Stage="ci-observed-in-progress")
path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
latest = ("**2026-11-05 12:42 UTC 最新有效状态**：唯一T-039C，本主会话实施、用户真实产品操作。"
    "用户已明确确认仅0.2.9，授权call_2fGzwrWEMco5efV9nKXVJskO；v029-user-approval-20261105-1.json已记录。"
    "仅annotated v0.2.9已正常推送，object3c3a615234c77d61441ce60c6cbfe07f02f37f65→source0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2，"
    "全部原refs/main/codex285df3a保持。实际CI42028799541/attempt1/source一致，package127249641900正在完整建测，"
    "正式资产尚未核对；继续观察同一run，不再调用publish重复本标签。审阅7662ed5/执行65e4b92和本地full3771/3769成功/2原跳过、"
    "19资产/97载荷/721来源保持。原028实际失败/官网与API完整digest/51原件保持，新029不得与本地候选混用字节。"
    "正式CI成功和匿名全部资产/来源核对后才给用户手工安装修正版，沿用同一Sandbox/资料；后继版另行准备/确认以验真正UI更新/重启/数据/卸载。"
    "同一Sandbox最后仍正式027/PID8820、服务7328/V3记录器8152/开关false/原数据与日志保持，但任何下一操作须再查。"
    "C/父T039/T040/NFR、首次.NET安装性能/M6延期未结案；不推分支、不改写原tag/Release/CI、不在宿主安装、不读改.codex或私钥、不建聊天/子代理。"
    "当前入口：[029当前交接](F:/Iris/tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V029_20261105.md)。")
for name in ("specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261105.md"):
    path = ROOT / name
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    positions = [i for i, line in enumerate(lines) if line.startswith("**2026-11-05 12:30 UTC ")]
    assert len(positions) == 1
    lines[positions[0]] = latest
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
print("Recorded the approved v0.2.9 tag and first CI attempt; no remote write requested.")
