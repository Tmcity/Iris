using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Iris.ArchitectureTests;

public class DependencyRulesTests
{
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Iris.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Iris.slnx not found in parent directories of " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Core_ReferencesNoForbiddenAssemblies()
    {
        var coreAssembly = typeof(Iris.Core.Errors.ErrorCode).Assembly;
        var referenced = coreAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        var forbidden = new[]
        {
            "PresentationFramework",
            "PresentationCore",
            "WindowsBase",
            "System.Windows.Forms",
            "Microsoft.Windows.SDK.NET"
        };

        foreach (var f in forbidden)
        {
            referenced.ShouldNotContain(f);
        }

        referenced.Where(name => name.StartsWith("Iris.", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty();
    }

    [Fact]
    public void Providers_Infrastructure_PlatformWindows_ReferenceOnlyCore()
    {
        var targetAssemblies = new[]
        {
            typeof(Iris.Providers.Http.HttpClientNames).Assembly,
            Assembly.Load("Iris.Infrastructure"),
            Assembly.Load("Iris.Platform.Windows")
        };

        foreach (var assembly in targetAssemblies)
        {
            var irisRefs = assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(name => name.StartsWith("Iris.", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var r in irisRefs)
            {
                r.ShouldBe("Iris.Core", $"Assembly {assembly.GetName().Name} referenced unexpected {r}");
            }
        }
    }

    [Fact]
    public void ProjectReferences_MatchArchitecture()
    {
        var root = FindRepoRoot();
        var projects = new Dictionary<string, string[]>
        {
            ["src/Iris.Core/Iris.Core.csproj"] = [],
            ["src/Iris.Providers/Iris.Providers.csproj"] = ["Iris.Core"],
            ["src/Iris.Infrastructure/Iris.Infrastructure.csproj"] = ["Iris.Core"],
            ["src/Iris.Platform.Windows/Iris.Platform.Windows.csproj"] = ["Iris.Core"],
            ["src/Iris.UiaHost/Iris.UiaHost.csproj"] = ["Iris.Core", "Iris.Platform.Windows"],
            ["src/Iris.App/Iris.App.csproj"] = ["Iris.Core", "Iris.Providers", "Iris.Infrastructure", "Iris.Platform.Windows", "Iris.UiaHost", "Iris.Maintenance"],
            ["src/Iris.Maintenance/Iris.Maintenance.csproj"] = ["Iris.Core", "Iris.Platform.Windows"]
        };

        foreach (var (relPath, expectedRefs) in projects)
        {
            var projPath = Path.Combine(root, relPath);
            File.Exists(projPath).ShouldBeTrue($"Project file not found: {projPath}");
            var doc = XDocument.Load(projPath);
            var actualRefs = doc.Descendants("ProjectReference")
                .Select(x => (string?)x.Attribute("Include"))
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => Path.GetFileNameWithoutExtension(x!))
                .OrderBy(x => x)
                .ToArray();

            actualRefs.ShouldBe(expectedRefs.OrderBy(x => x).ToArray(), $"Mismatch in {relPath}");
        }
    }

    [Fact]
    public void UiaHost_ReferencesOnlyCoreAndPlatformWindows()
    {
        var root = FindRepoRoot();
        var hostDir = Path.Combine(root, "src", "Iris.UiaHost", "bin");
        var dllFiles = Directory.Exists(hostDir)
            ? Directory.GetFiles(hostDir, "Iris.UiaHost.dll", SearchOption.AllDirectories)
            : [];

        if (dllFiles.Length > 0)
        {
            var assembly = Assembly.LoadFrom(dllFiles[0]);
            var irisRefs = assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(name => name.StartsWith("Iris.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name)
                .ToList();

            irisRefs.ShouldBe(["Iris.Core", "Iris.Platform.Windows"]);
        }
    }

    [Fact]
    public void Core_ProjectSettings()
    {
        var root = FindRepoRoot();
        var projPath = Path.Combine(root, "src/Iris.Core/Iris.Core.csproj");
        var doc = XDocument.Load(projPath);
        var tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
        tfm.ShouldBe("net10.0");

        doc.Descendants("UseWPF").ShouldBeEmpty();
        doc.Descendants("UseWindowsForms").ShouldBeEmpty();
    }

    [Fact]
    public void MaintenanceUsesNoWpfOrWinFormsAndAppDoesNotLoadItsAssembly()
    {
        var root = FindRepoRoot();
        var project = XDocument.Load(Path.Combine(root, "src/Iris.Maintenance/Iris.Maintenance.csproj"));
        project.Descendants("TargetFramework").Single().Value.ShouldBe("net10.0-windows10.0.19041.0");
        project.Descendants("UseWPF").ShouldBeEmpty(); project.Descendants("UseWindowsForms").ShouldBeEmpty();
        var app = XDocument.Load(Path.Combine(root, "src/Iris.App/Iris.App.csproj"));
        var reference = app.Descendants("ProjectReference").Single(item => ((string?)item.Attribute("Include"))?.Contains("Iris.Maintenance", StringComparison.Ordinal) == true);
        ((string?)reference.Attribute("ReferenceOutputAssembly")).ShouldBe("false");
        var runtimeReferences = Assembly.Load("Iris.App").GetReferencedAssemblies().Select(item => item.Name);
        runtimeReferences.ShouldNotContain("Iris.Maintenance");
    }

    [Fact]
    public void PlatformWindows_DoesNotUseWpf()
    {
        var root = FindRepoRoot();
        var projPath = Path.Combine(root, "src/Iris.Platform.Windows/Iris.Platform.Windows.csproj");
        var doc = XDocument.Load(projPath);

        doc.Descendants("UseWPF").ShouldBeEmpty();
        doc.Descendants("UseWindowsForms").ShouldBeEmpty();
    }

    [Fact]
    public void UiaHost_ProjectSettings()
    {
        var root = FindRepoRoot();
        var projPath = Path.Combine(root, "src/Iris.UiaHost/Iris.UiaHost.csproj");
        var doc = XDocument.Load(projPath);
        var tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
        tfm.ShouldBe("net10.0-windows10.0.19041.0");

        var outputType = doc.Descendants("OutputType").FirstOrDefault()?.Value;
        outputType.ShouldBe("Exe");

        var useWpf = doc.Descendants("UseWPF").FirstOrDefault()?.Value;
        useWpf.ShouldBe("false");

        var useWinForms = doc.Descendants("UseWindowsForms").FirstOrDefault()?.Value;
        useWinForms.ShouldBe("false");
    }
}
