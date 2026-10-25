架构师补充：独立 UI 复核已结束，两个运行进程均 exit0，现明确归还建测/打包窗口。继续当前 R4；原生产拓扑改进保留。

发现实际可读性缺陷：受测冻结 App DLL 773cb4a7… 的新恢复窗口，在真实应用 Dark 主题下，zh-CN/en-US × 380/540 四张图中两个动作文字近乎白色且落在白色背景，按钮/边界几乎不可见。Light 四张可读。已经实际运行并逐张查看，不是仅源码意见。详见 F:\Iris\tasks\done\T-039B.review\acceptance-review-4\source-feedback-2\README.md，原图在 recovery-ui/attempt-2/images，实际主题/语言/图片 SHA/加载 DLL 绑定均保留。

探针使用 App.xaml 的全局资源构成和生产 ApplicationThemeManager.Apply，没有给 Window 额外套字典或改前景/背景；回调无副作用，不联网、不运行产品 Host/安装。首轮语言夹具的问题已另行纠正：同一 BeginRun 不允许切语言，第一轮英文标签无效；第二轮每种语言独立进程并检查实际语言。产品稳定语言行为没有被判错，真正缺陷是两语言的深色动作不可读。

请在本 R4 修复生产 Window/内容/按钮的主题资源，补真实 Light/Dark 渲染验证并保留修前原图。不要仅修改测试字典或主题草稿。随后按原单执行受影响检查、最终 Release/full 和新的唯一命名包/来源绑定，保留 final-6/旧快照，不覆盖；新来宾验证仍等待架构师处理隔离环境。无需用户代传或重启任务。
