"""Freeze R3 binaries and a narrowly instrumented copy of its lifecycle test."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[4]
REVIEW = Path(__file__).resolve().parent
WORK = ROOT / "artifacts/t039b/independent-r3-rollback-1"
WORK.mkdir(exist_ok=False)
(WORK / "candidate").mkdir()
(REVIEW / "rollback-probe-source").mkdir()
sources = [
    "tests/Iris.App.Tests/Updates/UpdateHandoffLifecycleTests.cs",
    "tests/Iris.App.Tests/Updates/UpdateCoordinatorTests.cs",
    "tests/Iris.App.Tests/TestSupport/TempDirectory.cs",
    "tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs",
    "tests/Iris.App.Tests/Settings/TestSettingsEditCoordinator.cs",
]
bindings = []
for relative in sources:
    original = ROOT / relative
    content = original.read_text(encoding="utf-8-sig")
    if original.name == "UpdateHandoffLifecycleTests.cs":
        content = content.replace("using Iris.App.Tests.Hosting;\n", "")
        marker = "public sealed class UpdateHandoffLifecycleTests\n{"
        assert content.count(marker) == 1
        content = content.replace(marker, marker + "\n    public bool WireProductionUpdates { get; set; }\n")
        marker = "NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(Environment.TickCount64));"
        assert content.count(marker) == 1
        content = content.replace(marker, "NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(Environment.TickCount64), updates: WireProductionUpdates ? coordinator : null);")
        marker = "                // Coordinator rolled back to Failed with Unknown error"
        assert content.count(marker) == 1
        content = content.replace(marker, "                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { WireProductionUpdates, Phase = coordinator.Snapshot.Phase.ToString(), coordinator.HasCandidate, vm.UpdateHandoffTransferred, InstallCanExecute = vm.InstallUpdateCommand.CanExecute(null), CheckCanExecute = vm.CheckUpdateCommand.CanExecute(null), ShutdownException = thrown.GetType().Name }));\n" + marker)
    target = REVIEW / "rollback-probe-source" / original.name
    target.write_text(content, encoding="utf-8")
    shutil.copyfile(target, WORK / target.name)
    bindings.append(dict(original=relative, originalSha256=hashlib.sha256(original.read_bytes()).hexdigest(), probeSource=target.relative_to(ROOT).as_posix(), probeSha256=hashlib.sha256(target.read_bytes()).hexdigest()))

project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
properties = ET.SubElement(project, "PropertyGroup")
for name, value in dict(OutputType="Exe", TargetFramework="net10.0-windows10.0.19041.0", UseWPF="true", AssemblyName="Iris.App.Tests", ImplicitUsings="enable", Nullable="enable", EnableNETAnalyzers="false", TreatWarningsAsErrors="true", GenerateDocumentationFile="false").items():
    ET.SubElement(properties, name).text = value
references = ET.SubElement(project, "ItemGroup")
candidate = ROOT / "tests/Iris.App.Tests/bin/Release/net10.0-windows10.0.19041.0"
assemblies = []
for source in sorted(candidate.glob("*.dll")):
    if source.name == "Iris.App.Tests.dll":
        continue
    target = WORK / "candidate" / source.name
    shutil.copyfile(source, target)
    reference = ET.SubElement(references, "Reference", Include=source.stem)
    ET.SubElement(reference, "HintPath").text = str(target)
    assemblies.append(dict(path=str(source), sha256=hashlib.sha256(source.read_bytes()).hexdigest()))
ET.indent(project)
ET.ElementTree(project).write(WORK / "Review.csproj", encoding="utf-8", xml_declaration=True)
program = '''global using Xunit;
global using System.IO;
using Iris.App.Tests.Updates;
using Iris.App.Tests.TestSupport;
bool wired = args.Contains("--production-updates");
int result = 0;
try
{
    var fixture = new UpdateHandoffLifecycleTests { WireProductionUpdates = wired };
    await fixture.ShutdownFailureAfterHandoffPrepared_RollsBackCoordinator_AndResetsTransferredFlag();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { WireProductionUpdates = wired, RequirementPassed = true }));
}
catch (Exception error)
{
    result = 1;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { WireProductionUpdates = wired, RequirementPassed = false, ErrorType = error.GetType().Name, Detail = error.Message }));
}
finally
{
    await WpfTestRunner.CleanupWindowsAsync();
    await WpfTestRunner.TeardownAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { AllOwnedStaWorkJoined = !WpfTestRunner.IsStaThreadAlive }));
}
return result;
'''
(WORK / "Program.cs").write_text(program, encoding="utf-8")
shutil.copyfile(WORK / "Program.cs", REVIEW / "rollback-probe-source/Program.cs")
shutil.copyfile(WORK / "Review.csproj", REVIEW / "rollback-probe-source/Review.csproj")
with (REVIEW / "rollback-probe-binding.json").open("x", encoding="utf-8") as output:
    json.dump(dict(head=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(), sources=bindings, assemblies=assemblies, scope="One original R3 regression, compared with the same test plus the production updates dependency in AppBootstrapper. Product binaries are unchanged; no network, production IPC, user settings or installation."), output, indent=2)
print(json.dumps(dict(workDirectory=str(WORK), sourceFiles=len(sources), frozenAssemblies=len(assemblies))))
