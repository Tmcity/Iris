using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core.Errors;

namespace Iris.Platform.Windows.Maintenance;

internal sealed record ReleaseIdentity(string PackageId, string Channel, string RuntimeId, string Repository, string Version)
{
    internal const string FileName = "release-source.json";
    internal const string OfficialRepository = "https://github.com/Tmcity/Iris";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12
    };
    internal bool IsValid => PackageId == "Iris" && Channel == "win" && RuntimeId == "win-x64"
        && Repository == OfficialRepository && IsVersion(Version);
    internal static bool IsVersion(string value) => System.Version.TryParse(value, out var parsed)
        && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString() == value;
    internal static ReleaseIdentity Read(string path)
    {
        MaintenancePaths.RequireNoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 4096) throw Invalid();
        var value = Parse<ReleaseIdentity>(File.ReadAllBytes(path));
        return value is { IsValid: true } ? value : throw Invalid();
    }
    internal static T Parse<T>(byte[] bytes) where T : class
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw Invalid();
        return document.Deserialize<T>(Json) ?? throw Invalid();
    }
    internal static IrisException Invalid() => new(ErrorCode.InvalidResponse, ErrorMessages.GetDefault(ErrorCode.InvalidResponse));
    public override string ToString() => "ReleaseIdentity";
}

internal static class MaintenancePaths
{
    internal static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris");
    internal static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris.Maintenance");
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Iris");
    internal static bool Same(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    internal static string Child(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root), result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw ReleaseIdentity.Invalid();
        return result;
    }
    internal static void RequireNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IrisException(ErrorCode.PermissionDenied, ErrorMessages.GetDefault(ErrorCode.PermissionDenied));
        }
    }
    internal static bool IsInstalledApp(string directory) => Same(directory, Path.Combine(InstallRoot, "current"))
        && File.Exists(Path.Combine(InstallRoot, "Update.exe")) && File.Exists(Path.Combine(directory, "sq.version"))
        && !File.Exists(Path.Combine(directory, "portable.txt"));
    internal static void DeleteData(string directory, string expected)
    {
        if (!Same(directory, expected)) throw ReleaseIdentity.Invalid();
        RequireNoLinks(directory);
        if (!Directory.Exists(directory)) return;
        // Preflight the complete tree before deleting any entry. Never traverse a reparse point.
        var pending = new Stack<string>(); pending.Push(directory); var dirs = new List<string>(); var files = new List<string>();
        while (pending.TryPop(out var current))
        {
            RequireNoLinks(current); dirs.Add(current);
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                RequireNoLinks(child);
                if (Directory.Exists(child)) pending.Push(child); else files.Add(child);
            }
        }
        foreach (var file in files) { RequireNoLinks(file); File.Delete(file); }
        foreach (var dir in dirs.AsEnumerable().Reverse()) { RequireNoLinks(dir); Directory.Delete(dir); }
    }
}
