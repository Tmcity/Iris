"""Prepare a visual probe from the engineer's frozen R4 tested candidate; do not run it."""
from pathlib import Path
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
WORK = ROOT / "artifacts/t039b/independent-r4-recovery-ui-1"
WORK.mkdir(exist_ok=False)
(WORK / "candidate").mkdir()
source_binding_path = ROOT / "tasks/done/T-039B.evidence/r4/tested-source-binding.json"
source_binding = json.loads(source_binding_path.read_text("utf-8"))
candidate = Path(source_binding["TestedCandidateDirectory"])
hashes = {p["Path"]: p["Sha256"] for p in source_binding["TestedCandidateFiles"]}
records = []
project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
props = ET.SubElement(project, "PropertyGroup")
for name, value in dict(OutputType="Exe", TargetFramework="net10.0-windows10.0.19041.0", UseWPF="true",
        AssemblyName="Iris.App.Tests", ImplicitUsings="enable", Nullable="enable", EnableNETAnalyzers="false",
        TreatWarningsAsErrors="true", GenerateDocumentationFile="false", EnableDefaultCompileItems="false").items():
    ET.SubElement(props, name).text = value
refs = ET.SubElement(project, "ItemGroup")
ET.SubElement(refs, "Compile", Include="Program.cs")
for file in sorted(candidate.glob("*.dll")):
    if file.name == "Iris.App.Tests.dll":
        continue
    actual = hashlib.sha256(file.read_bytes()).hexdigest()
    assert hashes[file.name] == actual
    copied = WORK / "candidate" / file.name
    shutil.copyfile(file, copied)
    reference = ET.SubElement(refs, "Reference", Include=file.stem)
    ET.SubElement(reference, "HintPath").text = str(copied)
    records.append(dict(path=file.name, sha256=actual))
for culture in ["en-US"]:
    if (candidate / culture).is_dir():
        shutil.copytree(candidate / culture, WORK / "candidate" / culture)
        for file in (candidate / culture).glob("*.dll"):
            relative = file.relative_to(candidate).as_posix()
            actual = hashlib.sha256(file.read_bytes()).hexdigest()
            assert hashes[relative] == actual
            records.append(dict(path=relative, sha256=actual))
shutil.copyfile(HERE / "Program.cs", WORK / "Program.cs")
ET.indent(project)
ET.ElementTree(project).write(WORK / "Review.csproj", encoding="utf-8", xml_declaration=True)
(WORK / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
with (HERE / "binding.json").open("x", encoding="utf-8") as output:
    json.dump(dict(candidateDirectory=str(candidate), testedSourceBindingSha256=hashlib.sha256(source_binding_path.read_bytes()).hexdigest(),
        programSha256=hashlib.sha256((HERE / "Program.cs").read_bytes()).hexdigest(), assemblies=records,
        semantics="Preparation only. Compilation, execution and visual inspection have not occurred. Product binaries are copied unchanged; results must be recorded separately."), output, indent=2)
print(json.dumps(dict(workDirectory=str(WORK), frozenAssemblies=len(records))))
