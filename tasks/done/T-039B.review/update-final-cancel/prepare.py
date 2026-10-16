"""Freeze prebuilt review inputs without building/running Iris or changing the working tree."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--assemblies", required=True)
parser.add_argument("--destination", required=True)
args = parser.parse_args()
source = Path(args.assemblies).resolve()
destination = Path(args.destination).resolve()
review = Path(__file__).resolve().parent
if destination.exists():
    raise RuntimeError("Review destination already exists; original candidates must not be overwritten")
names = ["Iris.App", "Iris.Core", "Iris.Platform.Windows", "Velopack",
         "Microsoft.Extensions.Http", "Microsoft.Extensions.Logging.Abstractions",
         "Microsoft.Extensions.DependencyInjection.Abstractions", "WinRT.Runtime",
         "Microsoft.Windows.SDK.NET"]
if (source / "System.Security.Cryptography.ProtectedData.dll").is_file():
    names.append("System.Security.Cryptography.ProtectedData")
for name in names:
    if not (source / (name + ".dll")).is_file():
        raise FileNotFoundError(name + ".dll")
destination.mkdir(parents=True)
candidate = destination / "candidate"
candidate.mkdir()
records = []
for name in names:
    before = (source / (name + ".dll")).read_bytes()
    target = candidate / (name + ".dll")
    target.write_bytes(before)
    if (source / (name + ".dll")).read_bytes() != before:
        raise RuntimeError("Assembly changed during capture")
    records.append({"source": str(source / (name + ".dll")), "copy": str(target),
                    "sha256": hashlib.sha256(before).hexdigest()})
project = ET.Element("Project", {"Sdk": "Microsoft.NET.Sdk"})
properties = ET.SubElement(project, "PropertyGroup")
for name, value in {"OutputType": "Exe", "TargetFramework": "net10.0-windows10.0.19041.0",
                    "UseWPF": "true", "AssemblyName": "Iris.App.Tests", "ImplicitUsings": "enable",
                    "Nullable": "enable", "EnableNETAnalyzers": "false", "TreatWarningsAsErrors": "true"}.items():
    ET.SubElement(properties, name).text = value
references = ET.SubElement(project, "ItemGroup")
for name in names:
    reference = ET.SubElement(references, "Reference", {"Include": name})
    ET.SubElement(reference, "HintPath").text = str(candidate / (name + ".dll"))
ET.indent(project)
ET.ElementTree(project).write(destination / "Review.csproj", encoding="utf-8", xml_declaration=True)
for name in ["Program.cs", "CrossProcessSdkLock.cs"]:
    shutil.copyfile(review / name, destination / name)
(destination / "NuGet.Config").write_text("<configuration><packageSources><clear/></packageSources></configuration>", encoding="utf-8")
record = {"capturedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
          "assemblies": records, "probeSources": [{"name": name, "sha256": hashlib.sha256((review / name).read_bytes()).hexdigest()}
                                                   for name in ["Program.cs", "CrossProcessSdkLock.cs"]],
          "sourceToBinaryBuildBinding": "Must be established separately against the final stable build; this record binds binary inputs only.",
          "productRebuilt": False, "probeCompiledOrRun": False}
(destination / "binding.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
print(json.dumps({"destination": str(destination), "assemblyCount": len(records), "status": "prepared only"}))
