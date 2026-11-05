"""Record the approved 0.2.10 tag and observed live CI; no additional publication is performed."""
from pathlib import Path
import json

ROOT = Path("F:/Iris")
path = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1/state.json"
state = json.loads(path.read_text(encoding="utf-8-sig"))
assert state["TagRemoteVerified"] and state["TagObject"] == "8b3af29916cb42979fe2dd6ff63d6816cb7a796b"
state.update(CiRunId=42042550153, CiRunAttempt=1, PackageJobId=127296844225, CiStatus="in_progress", CiConclusion=None,
             Stage="ci-observed-in-progress")
path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
latest = ("**2026-11-05 14:19 UTC 最新有效状态**：唯一T-039C，用户已明确授权仅v0.2.10，call_pIjS2IrxpvdTukf3xVStNhE2原答和source227b3d8/reviewd779739/执行f5c5669已记录。"
    "仅annotated tag8b3af29916cb42979fe2dd6ff63d6816cb7a796b→227b3d88938543766a27d86a94ade52f9e5b89b7已正常推送，全部旧refs/main/codex285df3a保持。"
    "实际CI42042550153/attempt1/source一致，package127296844225正式建测/前版历史/打包success，上传产物进行中；发布及正式资产尚未完成核对。"
    "匿名API403额度0/reset14:30:19UTC原件保持，官网页面正常，跟进同一run不重复publish或重跑CI。"
    "用户真实029/PID5672/启动13:15:09.5871510Z基线已完整验证，97正式文件、简中截图、新POST13:34/HTTP200/虚构凭据匹配、旧row1/新row2和原配置/秘密/日志保持；V3/8152与服务7328正常。"
    "正式0210新资产/原CI全部回读后，才给用户真实029 UI第一段检查更新，逐段Offer/Ready/确认安装/native一次正常重启/旧数据新翻译/卸载保留数据完成C。"
    "此前首轮002→027 sharing32失败和手工恢复仍保持；C/父T039/T040/NFR、冷.NET性能和M6延期未结案。下一版本未授权、不推分支、不在宿主安装、不读改.codex/私钥、不创建聊天/子代理。"
    "当前入口：[0210当前交接](F:/Iris/tasks/T-039C.coordination/CURRENT_RELEASE_GATE_V0210_20261105.md)。")
for name in ("specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261105.md"):
    path = ROOT / name
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    matches = [i for i, line in enumerate(lines) if line.startswith("**2026-11-05 13:48 UTC ")]
    assert len(matches) == 1
    lines[matches[0]] = latest
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
print("Recorded the already pushed 0.2.10 tag and observed CI; no remote write requested.")
