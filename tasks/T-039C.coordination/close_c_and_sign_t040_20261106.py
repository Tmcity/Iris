"""Archive proven C completion and transfer the single active work order to full T-040."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT=Path("F:/Iris")
assert not subprocess.check_output(["git","diff","--cached","--name-only"],cwd=ROOT).strip()
assert subprocess.check_output(["git","diff","--name-only","227b3d88938543766a27d86a94ade52f9e5b89b7","--","src","tests","build",".github","docs","README.md","Iris.slnx","Directory.Build.props","Directory.Packages.props","global.json"],cwd=ROOT).strip()==b""
required=["manual-first-install-v022-20261105-1","ci-v029-api-supplement-20261105-1","ci-v0210-original-42042550153-1","ci-v0210-api-supplement-20261105-1",
          "manual-old-ready-v029-before-v0210-20261105-1","ui-offer-v0210-20261105-1","ui-ready-v0210-20261105-1","ui-upgrade-v0210-20261106-1","ui-retained-uninstall-v0210-20261106-1"]
proof=[];count=0
for name in required:
    directory=ROOT/"tasks/done/T-039C.evidence"/name;binding_path=directory/"binding.json";binding=json.loads(binding_path.read_text(encoding="utf-8-sig"))
    for item in binding["Files"]:
        p=directory/item["Path"];assert p.stat().st_size==item["Bytes"] and hashlib.sha256(p.read_bytes()).hexdigest()==item["Sha256"];count+=1
    proof.append({"Evidence":name,"BindingSha256":hashlib.sha256(binding_path.read_bytes()).hexdigest(),"FilesVerified":len(binding["Files"])})
final=ROOT/"tasks/done/T-039C.review-final-20261106"
final.mkdir(exist_ok=False)
current=ROOT/"tasks/current_task.md"
(final/"current_task.before-closure.md").open("xb").write(current.read_bytes())
(final/"FINAL_REVIEW.md").open("xb").write((ROOT/"tasks/T-039C.coordination/FINAL_C_REVIEW_20261106.md").read_bytes())
(final/"completion-audit.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"CRequiredEvidenceSets":len(proof),"ExactOriginalFilesVerified":count,"SourceCommit":"227b3d88938543766a27d86a94ade52f9e5b89b7",
    "CAndParentT039Completed":True,"WholeProjectGoalCompleted":False,"NextWorkOrder":"T-040","Evidence":proof},indent=2)+"\n")
(ROOT/"tasks/done/T-039C.md").open("x",encoding="utf-8",newline="\n").write("# T-039C 正式分发与匿名用户链路验收通过\n\n2026-11-06，本主会话依据原始数据逐项复核后结案。完整原工单保留于review-final/current_task.before-closure.md，逐项审计见[FINAL_REVIEW](F:/Iris/tasks/done/T-039C.review-final-20261106/FINAL_REVIEW.md)。\n\n正式0.2.9/0.2.10资产、source/tag/原CI与全部失败保持；无.NET/无缓存正式022默认安装成功、真实029 UI检查/下载Ready/确认安装至0210/native exit0/一次正常新App/资料与新翻译/默认保留数据卸载均独立证明。C/父T039闭合，完整项目的T040/NFR尚未完成，M6延期。\n")
(ROOT/"tasks/done/T-039.md").open("x",encoding="utf-8",newline="\n").write("# T-039 打包、安装、更新与卸载验收通过\n\n2026-11-06。A契约实证和B正式实现既有验收保持，C补齐实际公开匿名资产、默认无缓存运行时安装和真正用户UI full更新/一次正常重启、更新后默认保留数据卸载。证据与边界见[T-039C完整审计](F:/Iris/tasks/done/T-039C.review-final-20261106/FINAL_REVIEW.md)。\n\n正式产品仍为Windows x64/FDD App与UiaHost、SC维护/外层，版本0.2.10，发布source227b3d8。冷安装约10分钟/微软MSI签名策略等待、旧历史操作警告纳入T-040复验；本结论不代表全功能或NFR已通过。原失败/恢复、旧tag/源码、未签名和delta只生成边界保持，M6/T033延期。\n")
current.write_text((ROOT/"tasks/T-040.work-order-20261106.md").read_text(encoding="utf-8-sig"),encoding="utf-8",newline="\n")
backlog=ROOT/"tasks/BACKLOG.md";text=backlog.read_text(encoding="utf-8-sig")
for before,after in(("| T-039 | ▶ |","| T-039 | ☑ |"),("| T-039C | ▶ |","| T-039C | ☑ |"),("| T-040 | ☐ |","| T-040 | ▶ |")):
    assert before in text;text=text.replace(before,after)
backlog.write_text(text+"\n**2026-11-06 T-039C/父T039结案，T-040签发** — 正式匿名资产、无.NET/无缓存默认022安装、真实029→0210 UI/native一次正常启动/资料、默认保留数据卸载均逐项独立通过。审计tasks/done/T-039C.review-final-20261106；9证据集236原件再次匹配。下一唯一T-040全功能/NFR/缺陷复核，项目目标未完成，M6延期。\n",encoding="utf-8",newline="\n")
latest="**2026-11-06 当前唯一工单T-040**：C/父T039经过逐项独立审计结案，9证据集236原件匹配；正式0210/source227b3d8、CI3771全部成功、20资产/97载荷/721来源、默认冷安装、真正029 UI→0210/native0/一次新8980、旧2历史与新第3条及默认保留资料卸载全部证明。当前Iris已卸载、APPDATA资料/共享.NET10.0.14保持，Sandbox与合成服务/记录器待每次新查。T-040本主会话实施、用户实际产品操作；全功能与ARCH§1.2 NFR未验收，冷.NET微软MSI约487秒/签名策略481秒、旧History异常需复核。M6/T033延期。见tasks/current_task.md与tasks/done/T-039C.review-final-20261106/FINAL_REVIEW.md。旧C等待状态为历史；不改私钥/.codex、不创建聊天/子代理、不自动发布新版本。"
for name in("specs/PROJECT_HANDOFF.md","specs/DEVELOPER_HANDOFF.md","specs/DEVELOPER_RECOVERY.md"):
    p=ROOT/name;lines=p.read_text(encoding="utf-8-sig").splitlines();lines.insert(2,latest);lines.insert(3,"");p.write_text("\n".join(lines)+"\n",encoding="utf-8",newline="\n")
raw_paths=[p for p in final.rglob("*")if p.is_file()]
raw_paths += [p for p in(ROOT/"tasks/done/T-039C.evidence/ui-retained-uninstall-v0210-20261106-1").rglob("*")if p.is_file()]
explicit=["tasks/current_task.md","tasks/BACKLOG.md","tasks/done/T-039.md","tasks/done/T-039C.md","tasks/T-040.work-order-20261106.md","tasks/T-039C.coordination/FINAL_C_REVIEW_20261106.md",
    "tasks/T-039C.coordination/v0210-user-uninstall-feedback-20261106-1.json","tasks/T-039C.coordination/review_retained_uninstall_v0210_20261106.py","tasks/T-039C.coordination/close_c_and_sign_t040_20261106.py",
    "specs/PROJECT_HANDOFF.md","specs/DEVELOPER_HANDOFF.md","specs/DEVELOPER_RECOVERY.md"]
paths=explicit+[p.relative_to(ROOT).as_posix()for p in raw_paths]
subprocess.run(["git","-c","core.longpaths=true","add","-f","--pathspec-from-file=-","--pathspec-file-nul"],cwd=ROOT,input=b"".join((p+"\0").encode("utf-8")for p in paths),check=True)
for p in raw_paths:
    if "T-039C.evidence"in p.parts:assert subprocess.check_output(["git","show",":"+p.relative_to(ROOT).as_posix()],cwd=ROOT)==p.read_bytes()
subprocess.run(["git","diff","--cached","--check","--",*explicit],cwd=ROOT,check=True)
print(json.dumps({"CAndParentT039Completed":True,"WholeProjectGoalCompleted":False,"NewActiveWorkOrder":"T-040","RequiredOriginalFilesVerified":count,"ExplicitPathsStaged":len(paths)}))
