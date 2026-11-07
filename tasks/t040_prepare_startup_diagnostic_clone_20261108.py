"""Create an isolated diagnostic source clone with timing-only instrumentation."""
from pathlib import Path
import io
import json
import subprocess
import tarfile

ROOT = Path('F:/Iris')
DEST = ROOT/'artifacts/t040/startup-diagnostic-clone-20261108-1'
DEST.mkdir(parents=True,exist_ok=False)
names = ['src','Directory.Build.props','Directory.Packages.props','global.json']
archive = subprocess.check_output(['git','archive','HEAD',*names],cwd=ROOT)
with tarfile.open(fileobj=io.BytesIO(archive),mode='r:') as source:
    for member in source.getmembers():
        target = (DEST/member.name).resolve()
        if not target.is_relative_to(DEST.resolve()):
            raise ValueError('Archive path outside diagnostic clone')
    source.extractall(DEST,filter='data')
path = DEST/'src/Iris.App/Hosting/StartupRuntime.cs'
text = path.read_text(encoding='utf-8-sig')
text = text.replace('var app = host.Services.GetRequiredService<App>();',
    'var timing = System.Diagnostics.Stopwatch.StartNew();\n        var app = host.Services.GetRequiredService<App>();\n        System.IO.File.AppendAllText(System.IO.Path.Combine(host.Services.GetRequiredService<IAppPaths>().DataDirectory, "startup-phases.txt"), "ResolveApp=" + timing.Elapsed.TotalMilliseconds + "\\n");\n        timing.Restart();')
text = text.replace('app.InitializeComponent();',
    'app.InitializeComponent();\n        System.IO.File.AppendAllText(System.IO.Path.Combine(host.Services.GetRequiredService<IAppPaths>().DataDirectory, "startup-phases.txt"), "InitializeResources=" + timing.Elapsed.TotalMilliseconds + "\\n");')
path.write_text(text,encoding='utf-8-sig')
path = DEST/'src/Iris.App/Hosting/AppBootstrapper.cs'
text = path.read_text(encoding='utf-8-sig')
text = text.replace('await Task.Yield();',
    'await Task.Yield();\n        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();\n        void Phase(string name) { System.IO.File.AppendAllText(System.IO.Path.Combine(_settingsService.Current == null ? "" : _host.Services.GetRequiredService<Iris.Core.Storage.IAppPaths>().DataDirectory, "startup-phases.txt"), name + "=" + phaseWatch.Elapsed.TotalMilliseconds + "\\n"); phaseWatch.Restart(); }')
text = 'using Microsoft.Extensions.DependencyInjection;\n' + text
for old, new in (
    ('await _translationRegistry.RefreshAsync();', 'Phase("SettingsAndNetwork");\n        await _translationRegistry.RefreshAsync();\n        Phase("TranslationRegistry");'),
    ('if (_ocrRegistry != null) await _ocrRegistry.RefreshAsync();','if (_ocrRegistry != null) await _ocrRegistry.RefreshAsync();\n        Phase("OcrRegistry");'),
    ('_themeService.Apply(_settingsService.Current.General.Theme);','_themeService.Apply(_settingsService.Current.General.Theme);\n        Phase("LogAndTheme");'),
    ('_trayIconController.Created += OnTrayIconCreated;','Phase("Database");\n        _trayIconController.Created += OnTrayIconCreated;'),
    ('// 注册热键协调器','Phase("Tray");\n        // 注册热键协调器'),
    ('if (_selectionIconController != null) await _selectionIconController.StartAsync().ConfigureAwait(false);','Phase("Hotkeys");\n        if (_selectionIconController != null) await _selectionIconController.StartAsync().ConfigureAwait(false);\n        Phase("SelectionController");'),
    ('await _host.StartAsync();','await _host.StartAsync();\n        Phase("HostStart");')):
    if old not in text:
        raise ValueError('Timing marker not found: '+old)
    text = text.replace(old,new)
path.write_text(text,encoding='utf-8-sig')
state = {'SourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),
         'Purpose':'Timing-only isolated diagnostic; no installed binary or source worktree changed',
         'FormalNfrResult':False,'ProductWorkingTreeChanged':False,'RequiresOwnedSandboxLaunch':True}
(DEST/'binding.json').write_text(json.dumps(state,indent=2)+'\n',encoding='utf-8')
print(str(DEST))
