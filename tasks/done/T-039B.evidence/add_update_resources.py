from pathlib import Path
import json
import xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[3]
text={
'MaintenanceBusy':('Iris 正在安装、更新或卸载，请等待维护完成后重试。','Iris is being installed, updated or removed. Retry after maintenance finishes.'),
'UpdateTitle':('软件更新','Software updates'),
'UpdatePreference':('启动时检查更新','Check for updates at startup'),
'UpdateCheck':('检查更新','Check for updates'),
'UpdateDownload':('下载更新','Download update'),
'UpdateCancel':('取消／重试清理','Cancel / retry cleanup'),
'UpdateInstall':('安装并重启','Install and restart'),
'UpdateInstallConfirm':('安装更新需要关闭 Iris 并重新启动。未保存的设置草稿不会自动保存。是否继续？','Installing the update closes and restarts Iris. Unsaved settings will not be saved automatically. Continue?'),
'UpdateUnsupported':('此运行模式不支持自动更新。请使用 Iris 安装版。','Updates are unavailable in this mode. Use an installed copy of Iris.'),
'UpdateIdle':('尚未检查更新','Updates have not been checked'),
'UpdateChecking':('正在检查更新…','Checking for updates…'),
'UpdateNoRelease':('尚无兼容的已发布版本','No compatible release is published'),
'UpdateUpToDate':('已是最新版本','You are up to date'),
'UpdateAvailable':('有可用更新','An update is available'),
'UpdateDownloading':('正在下载更新…','Downloading update…'),
'UpdateReady':('更新已验证，可以安装','Update verified and ready to install'),
'UpdateCancelling':('正在取消并清理…','Canceling and cleaning up…'),
'UpdateCleanupPending':('清理未完成，请重试清理','Cleanup is incomplete. Retry cleanup.'),
'UpdateFailed':('更新操作失败','Update operation failed'),
'UpdateStopped':('更新服务已停止','Update service stopped')}
catalog=root/'build/localization/app-catalog.json';entries=json.loads(catalog.read_text(encoding='utf-8'))
for suffix,language,i in [('', 'zh-CN',0),('.en-US','en-US',1)]:
 p=root/f'src/Iris.App/Resources/Strings{suffix}.resx';tree=ET.parse(p);doc=tree.getroot()
 for key,pair in text.items():
  node=next((n for n in doc.findall('data') if n.attrib['name']==key),None)
  if node is None:node=ET.SubElement(doc,'data',name=key,attrib={'{http://www.w3.org/XML/1998/namespace}space':'preserve'});ET.SubElement(node,'value')
  node.find('value').text=pair[i]
 ET.indent(doc,space='  ');tree.write(p,encoding='utf-8',xml_declaration=True)
for key,pair in text.items():entries[key]={'zh-CN':pair[0],'en-US':pair[1]}
catalog.write_text(json.dumps(entries,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
code='namespace Iris.App;\n\ninternal static partial class UiText\n{\n'
for key in text:code+=f'    public static string {key} => global::Iris.App.Resources.AppResources.Get("{key}");\n'
code+='    internal static string UpdateState(Updates.UpdatePhase phase) => global::Iris.App.Resources.AppResources.Get("Update" + phase);\n}\n'
(root/'src/Iris.App/UiText.Updates.cs').write_text(code,encoding='utf-8')
