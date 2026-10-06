# T-031B 独立验收

提交 cafc5ebdfe75c0c18a7b638ee4519dff47ea1e27，2026-10-07。

独立Release零警告/错误，16.97s；全量3234、3232成功/0失败/2既有隔离窗口站error183跳过，46.283s。任务全部正常退出。

最终提交构建后运行prepare.py绑定DLL，再分别运行caller-cancel、entries、effects、host、dispatcher，42项全部通过。真实三个Handler、Presenter/Window/VM、Dispatcher与Host；捕获/OCR/剪贴板/托盘边界替身，无用户内容。覆盖普通OCR只填原文与真实按钮继续、截图翻译一次、静默不干扰已有翻译、原caller LIFO取消、一次复制/提示、编辑隐藏撤销、非合作剪贴板退出、即时通知、真实DI单例及三动作互斥。

六张最终明暗loading/source/error已逐张查看，交付最终图逐字节匹配；没有OCR截图预览或独立结果窗。33项最终源码散列与开发报告一致；最终探针DLL绑定见manifest，不将提交前DLL戳冒称提交后戳。

首轮DI循环及夹具缺注册等真实失败保留于T-031B.evidence；原caller基线反例附本目录。独立夹具开发期修正包括缺IO using、泛型logger构造、合成字符串长度15及Host模式显式加载Wpf.Ui，不视为产品缺陷。未改产品或断言来强行通过。

不宣称真实桌面/剪贴板/云服务/全局热键/NFR已验证。A.review随B交付已保留。.codex未触碰。T-031/B与CCR033可结案，下一单T-032设置页与增量保存基础。
