using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Shouldly;
using Xunit;

namespace Iris.ArchitectureTests;

public class ContractFidelityTests
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
    public void LandedFiles_MatchContractSpec()
    {
        var root = FindRepoRoot();
        var contractsFile = Path.Combine(root, "specs", "CONTRACTS.cs");
        File.Exists(contractsFile).ShouldBeTrue($"Specs file not found: {contractsFile}");

        var specLines = File.ReadAllLines(contractsFile, Encoding.UTF8);
        var sections = ParseContractSections(specLines);

        sections.Count.ShouldBeGreaterThan(0, "No contract sections found in specs/CONTRACTS.cs");

        foreach (var (relativePath, expectedRawLines) in sections)
        {
            var landedPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(landedPath).ShouldBeTrue($"Landed contract file not found: {relativePath}");

            var landedRawLines = File.ReadAllLines(landedPath, Encoding.UTF8);

            var normalizedExpected = NormalizeLines(expectedRawLines);
            var normalizedActual = NormalizeLines(landedRawLines);

            var minCount = Math.Min(normalizedExpected.Count, normalizedActual.Count);
            for (var i = 0; i < minCount; i++)
            {
                if (!string.Equals(normalizedExpected[i], normalizedActual[i], StringComparison.Ordinal))
                {
                    throw new ShouldAssertException(
                        $"Contract mismatch in {relativePath} at normalized line {i + 1}:\n" +
                        $"Expected: {normalizedExpected[i]}\n" +
                        $"Actual:   {normalizedActual[i]}");
                }
            }

            if (normalizedExpected.Count != normalizedActual.Count)
            {
                var extraLine = normalizedExpected.Count > normalizedActual.Count
                    ? $"Expected extra line {minCount + 1}: {normalizedExpected[minCount]}"
                    : $"Actual extra line {minCount + 1}: {normalizedActual[minCount]}";

                throw new ShouldAssertException(
                    $"Contract line count mismatch in {relativePath}: expected {normalizedExpected.Count} lines, but got {normalizedActual.Count} lines.\n" +
                    $"{extraLine}");
            }
        }
    }

    private static Dictionary<string, List<string>> ParseContractSections(string[] lines)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? currentPath = null;
        List<string>? currentLines = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("// FILE: src/", StringComparison.OrdinalIgnoreCase))
            {
                if (currentPath != null && currentLines != null)
                {
                    sections[currentPath] = currentLines;
                }

                currentPath = trimmed.Substring("// FILE: ".Length).Trim();
                currentLines = new List<string>();
                continue;
            }

            if (currentLines != null)
            {
                currentLines.Add(line);
            }
        }

        if (currentPath != null && currentLines != null)
        {
            sections[currentPath] = currentLines;
        }

        return sections;
    }

    private static List<string> NormalizeLines(IEnumerable<string> rawLines)
    {
        var result = new List<string>();
        foreach (var rawLine in rawLines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith("using ", StringComparison.Ordinal) ||
                line.StartsWith("namespace ", StringComparison.Ordinal) ||
                line == "{" || line == "}" ||
                line.StartsWith("// ===", StringComparison.Ordinal) ||
                line.StartsWith("//  PART", StringComparison.Ordinal) ||
                line.StartsWith("// PART", StringComparison.Ordinal) ||
                line.StartsWith("//  说明", StringComparison.Ordinal) ||
                line.StartsWith("// 说明", StringComparison.Ordinal) ||
                line.StartsWith("//  不定义", StringComparison.Ordinal) ||
                line.StartsWith("// 不定义", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }
}
