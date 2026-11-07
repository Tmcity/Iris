from pathlib import Path
import shutil
import json

root = Path('F:/Iris')
old = root/'artifacts/t040/startup-diagnostic-clone-20261108-1'
new = root/'artifacts/t040/startup-diagnostic-clone-20261108-2'
shutil.copytree(old,new,ignore=shutil.ignore_patterns('bin','obj'))
path = new/'src/Iris.App/Program.cs'
text = path.read_text(encoding='utf-8-sig')
text = text.replace('var identity = runtime.GetIdentity();', 'var prehost = Stopwatch.StartNew();\n            var identity = runtime.GetIdentity();')
text = text.replace('primary = true;', 'primary = true;\n                var timingRoot = runtime.CreatePaths().DataDirectory;\n                void Mark(string step) { System.IO.File.AppendAllText(System.IO.Path.Combine(timingRoot, "startup-prehost.txt"), step + "=" + prehost.Elapsed.TotalMilliseconds + "\\n"); prehost.Restart(); }\n                Mark("IdentityAndMaintenance");')
text = text.replace('languageRun = runtime.BeginPrimaryRun();', 'languageRun = runtime.BeginPrimaryRun();\n                Mark("LanguageRun");')
text = text.replace('runtime.BootstrapPrimary();', 'runtime.BootstrapPrimary();\n                Mark("VelopackPrimary");')
text = text.replace('var paths = runtime.CreatePaths();', 'var prehost = Stopwatch.StartNew();\n            var paths = runtime.CreatePaths();\n            void Mark(string step) { System.IO.File.AppendAllText(System.IO.Path.Combine(paths.DataDirectory, "startup-prehost.txt"), step + "=" + prehost.Elapsed.TotalMilliseconds + "\\n"); prehost.Restart(); }\n            Mark("CreatePaths");')
text = text.replace('runtime.OpenLogger(paths, levelSwitch); loggerOpened = true;', 'runtime.OpenLogger(paths, levelSwitch); loggerOpened = true;\n            Mark("OpenLogger");')
text = text.replace('host = runtime.BuildHost(identity, command, paths, levelSwitch, startTimestamp);', 'host = runtime.BuildHost(identity, command, paths, levelSwitch, startTimestamp);\n            Mark("BuildHost");')
path.write_text(text,encoding='utf-8-sig')
(new/'prehost-timing-binding.json').write_text(json.dumps({'DiagnosticOnly':True,'FormalNfrResult':False,'ProductWorkingTreeChanged':False},indent=2)+'\n',encoding='utf-8')
print(new)
