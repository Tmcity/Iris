# T-026A 独立评审证据（2026-10-06）

被审提交 cb5981e7877bcdb3bad127934096b3f1290e18b3。先独立串行 Release build/full test，再刷新实际 DLL/外部 fixture 并执行 Review。原始完整输出分别为 review-build.txt、review-full.txt、review-probe.txt；退出码 0/0/2。最终探针九项中六通过、三失败，STA 已退出。初步试跑没有替代这里绑定最终提交的正式输出。

Program.cs.txt 是独立断言程序；使用实际 App 协调器/Presenter/session/VM、自有离屏 WPF 窗口，fake 仅为原生、存储、Provider 等外部边界。不读取真实桌面内容/剪贴板、不截屏、不注入输入、不装全局 hook、不访问用户配置或网络，不运行真实 Iris。普通 Hide/Stop、新 Show 保留与两类录入通知失败归还等均为通过对照。

复现：在独立临时目录把 Program.cs.txt/GlobalUsings.cs.txt/prepare.py.txt 去掉 .txt 后缀；在仓库完成 Release build/test，再运行 python prepare.py 和 dotnet run --project Review.csproj -c Release。prepare 从实际 App 测试产物引用产品 DLL，仅从仓库复制原有测试 wiring/fake，不复制产品状态机；AssemblyName 为 Iris.App.Tests，使用既有 IVT。Review.csproj.txt 是本次真实生成的项目快照。

源码/DLL与证据 SHA256 见 manifest.json。探针编译的三个 CS0649 来自抽取后未使用的 fake 字段，保留原文；独立解决方案构建仍是 0 警告/0 错误。全部三项失败均为已建立实际前提后的业务断言失败，非编译或 teardown 失败。后续修复请另存输出，不覆盖本目录失败证据。

真实桌面隐藏画面、焦点、混合 DPI、物理输入和 NFR 未验证。全解决方案仍含已验收 T-022A 只读 native smoke 和两项 error=183 环境跳过，不能泛称全套无桌面查询。
