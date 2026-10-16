using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Iris.Platform.Windows.Maintenance;

internal static class ReleasePackage
{
    internal const long MaxBytes = 1024L * 1024 * 1024;
    internal static async Task ValidateAsync(string path, long size, string sha256, string version, CancellationToken cancellationToken)
    {
        MaintenancePaths.RequireNoLinks(path);
        if (!ReleaseIdentity.IsVersion(version) || size is <= 0 or > MaxBytes || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw ReleaseIdentity.Invalid();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != size) throw ReleaseIdentity.Invalid();
        var digest = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(digest).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw ReleaseIdentity.Invalid();
        cancellationToken.ThrowIfCancellationRequested(); file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 10000) throw ReleaseIdentity.Invalid();
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (!names.Add(name) || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or ".")) throw ReleaseIdentity.Invalid();
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw ReleaseIdentity.Invalid();
            total = checked(total + entry.Length);
            if (total > 3 * MaxBytes) throw ReleaseIdentity.Invalid();
        }
        var manifests = zip.Entries.Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1 || manifests[0].Length > 64 * 1024) throw ReleaseIdentity.Invalid();
        using var stream = manifests[0].Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 64 * 1024, XmlResolver = null });
        var document = XDocument.Load(reader);
        string? Value(string key) => document.Descendants().SingleOrDefault(node => node.Name.LocalName == key)?.Value;
        if (Value("id") != "Iris" || Value("version") != version || Value("channel") != "win" || Value("rid") != "win-x64"
            || Value("mainExe") != "Iris.App.exe") throw ReleaseIdentity.Invalid();
        foreach (string name in new[] { "lib/app/Iris.App.exe", "lib/app/Iris.App.runtimeconfig.json", "lib/app/uia/Iris.UiaHost.exe", "lib/app/en-US/Iris.App.resources.dll", "lib/app/maintenance/Iris.Maintenance.exe" })
            if (!names.Contains(name)) throw ReleaseIdentity.Invalid();
    }
}
