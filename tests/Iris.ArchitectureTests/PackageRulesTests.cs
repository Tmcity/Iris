using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Iris.ArchitectureTests;

public class PackageRulesTests
{
    // 新增依赖须经架构师批准（ARCH §4.2）
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedPackages = new Dictionary<string, IReadOnlySet<string>>
    {
        ["Iris.Core"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ["Iris.Providers"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.Extensions.Http",
            "Microsoft.Extensions.Http.Resilience",
            "Anthropic"
        },
        ["Iris.Infrastructure"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.Data.Sqlite",
            "Dapper"
        },
        ["Iris.Platform.Windows"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.Windows.CsWin32",
            "System.Security.Cryptography.ProtectedData"
        },
        ["Iris.App"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) // 不限制（组合根）
    };

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

    private static bool IsAbstractionsPackage(string package)
    {
        return package.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase)
            && package.EndsWith(".Abstractions", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageReferences_AreInAllowList()
    {
        var root = FindRepoRoot();
        var srcDir = Path.Combine(root, "src");

        foreach (var (projectName, allowedSet) in AllowedPackages)
        {
            if (projectName == "Iris.App")
            {
                // 不限制（组合根）
                continue;
            }

            var projPath = Path.Combine(srcDir, projectName, $"{projectName}.csproj");
            File.Exists(projPath).ShouldBeTrue($"Project file not found: {projPath}");

            var doc = XDocument.Load(projPath);
            var packageRefs = doc.Descendants("PackageReference")
                .Select(x => (string?)x.Attribute("Include"))
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .ToList();

            foreach (var pkg in packageRefs)
            {
                var isAllowed = allowedSet.Contains(pkg) || IsAbstractionsPackage(pkg);
                isAllowed.ShouldBeTrue(
                    $"Package '{pkg}' in project '{projectName}' is not in the allowlist. 新增依赖须经架构师批准（ARCH §4.2）");
            }
        }
    }
}
