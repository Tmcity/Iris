"""Keep the live handoff unambiguous while retaining all historical checkpoints."""
from pathlib import Path
import argparse
import json

ROOT = Path("F:/Iris")
parser = argparse.ArgumentParser()
parser.add_argument("--phase", choices=("validating", "packaging", "ready"), required=True)
args = parser.parse_args()
source = "c32fcb283e417ab11715c46207fdc8cfde4baedf"
phase = "Release/全量正在执行，先读取实际完成结果，不重复或并行建测；通过后从固定源码打包唯一0.2.6候选并独立核对。"
if args.phase == "packaging":
    summary = json.loads((ROOT / "artifacts/t039c/local-validation-v026-20261103-1/validation-summary.json").read_text(encoding="utf-8-sig"))
    assert summary["SourceCommit"] == source and summary["BuildWarnings"] == summary["BuildErrors"] == 0
    assert summary["FullTests"] == {"Total": 3758, "Passed": 3756, "Failed": 0, "Skipped": 2}
    phase = "完整Release0/0、全量3758/3756成功/0失败/2既有error183跳过及发布工具16项已通过；下一步唯一0.2.6候选打包与独立来源/资产/载荷核对，不重复全量。"
if args.phase == "ready":
    report = json.loads((ROOT / "tasks/done/T-039C.evidence/next-source-v026-20261103-1/review.json").read_text(encoding="utf-8-sig"))
    assert report["SourceCommit"] == source and report["BuildWarnings"] == report["BuildErrors"] == 0
    totals = report["FullTests"]
    phase = (f"完整Release0/0，全量{totals['Total']}/{totals['Passed']}成功/{totals['Failed']}失败/"
             f"{totals['Skipped']}既有跳过；来源{report['CommittedSourceEntriesMatched']}项、"
             f"{report['PublicAssets']}资产/{report['PayloadEntries']}载荷及正式旧0.2.2full独立通过。"
             "唯一candidate1和PREPUBLICATION_REVIEW_V026已齐备，下一步仅在用户新确认后创建、推送v0.2.6，不推分支。")
    old = json.loads((ROOT / "tasks/done/T-039C.evidence/manual-old-ready-before-v026-20261103-1/binding.json").read_text(encoding="utf-8-sig"))
    assert old["Version"] == "0.2.2" and old["AppPid"] == 6604 and old["RecorderVersion"] == 3 and old["RecorderReady"] is True
    phase += (f"原V2事件清理错误及停止原件保留；V3真实队列缺失清理与辅助native启动/退出0已独立通过。"
              f"{old['ObservedUtc']}旧应用仍原PID6604、开关false与原三项数据哈希，V3/PID{old['RecorderPid']} Ready。")
paragraph = ("**2026-11-03 当前有效状态**：T-039C仍是唯一活跃工单。用户已确认仅v0.2.5，"
             "tag95c9c344…→ee2d711已正常推送；实际CI41461057191建测失败，打包/publish skipped，无正式0.2.5资产。"
             "原ZIP506060字节/SHAe3916ed568433386720fbacf81ab41c2c33016f30792fd0c32680ffbd5d69f0f"
             "与官方artifact13148997572一致，51原件和三个修前源码已冻结。唯一失败是自启跨保存/Reload测试过早断言后台查询状态；"
             "受控查询阻塞反例实际复现同一True/False失败（筛选零测试和初次CA1859编译失败原记录也保持）。"
             "修正观察PropertyChanged通知，新增保存已完成但查询仍被显式阻塞的WPF用例；"
             "相邻两个自启外部意图用例保留全部断言、等待状态通知并在finally显式放行/完整观察工作。"
             f"定向18/18通过，固定source{source}，仅一个测试文件、release-plan和RELEASING变化，生产/契约/schema/依赖不变。"
             + phase + " 0.2.6未授权/创建/推送，原失败标签、候选和CI不重写/重跑。"
             "main/codex远端仍285df3a；实际正式前版仅0.2.2。用户Sandbox590f8848…及原合成数据保持，"
             "发布前重新查询实际进程/记录器和数据，不能据历史PID断言实时状态。产品UI由用户分段手工执行。"
             "C/父T-039、真实UI升级/退出/native顺序/恰好一次重启/数据保留/更新后卸载、T-040/NFR尚未结案，M6延期。"
             "不改.codex，不重开工程师聊天或子代理，不在宿主安装Iris。")
paths = ["specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "specs/DEVELOPER_RECOVERY.md",
         "tasks/current_task.md", "tasks/T-039C.coordination/SESSION_STATUS_20261102.md"]
for relative in paths:
    path = ROOT / relative
    content = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    marker = "**2026-11-03 当前有效状态**："
    if marker in content:
        start = content.index(marker)
        end = content.index("\n\n", start)
        content = content[:start] + paragraph + content[end:]
    else:
        first = content.index("\n\n") + 2
        content = content[:first] + paragraph + "\n\n" + content[first:]
    path.write_text(content, encoding="utf-8", newline="\n")
latest = ROOT / "tasks/T-039C.coordination/SESSION_STATUS_20261103.md"
latest.write_text("# T-039C 当前接续状态（2026-11-03）\n\n" + paragraph + "\n\n"
    "工作树：F:/Iris/artifacts/t039c/worktree-v026-ci-fix-20261103-1，分支codex/t039c-ci-v025-fixture。\n\n"
    "原CI：tasks/done/T-039C.evidence/ci-v025-original-41461057191-1/binding.json；"
    "发布receipt：publication-v0.2.5-20261102-1/state.json。\n\n"
    "完整验证：artifacts/t039c/local-validation-v026-20261103-1；"
    "唯一候选：artifacts/t039c/release-0.2.6-public-base-candidate-1。\n\n"
    "正式前版：artifacts/t039c/public-0.2.2-download-20261101-1；"
    "full SHA256 c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14。\n\n"
    "旧来宾入口C:\\IrisAcceptanceUpgrade，输出"
    "artifacts/t039c/manual-verification-v022-20261101-1/output/session-1c8333f923744572ac2c71bab9576af5。\n\n"
    "后续：新版本实际CI成功与正式公开字节/来源完整核对后，才依次让用户"
    "检查更新保存offer→下载保存Ready→确认安装及新版本/旧历史/原服务新翻译保存updated"
    "→独立复核升级→手工卸载保留数据保存uninstalled并停止记录器。C通过后才推进T-040。\n",
    encoding="utf-8", newline="\n")
print("Updated live handoff:", args.phase, "source", source)
