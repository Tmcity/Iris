using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Iris.Platform.Windows.Maintenance;

namespace Iris.Maintenance;

internal sealed class DesktopRuntimeDownload(HttpClient http, string cachePath, string workRoot, Func<bool> installed, Action<string> verifySignature)
{
    private const long MaxInstallerBytes = 192L * 1024 * 1024;
    private const int MaxMetadataBytes = 2 * 1024 * 1024;
    private static readonly Uri MetadataUri = new("https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json");

    internal static async Task PrepareAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, DefaultProxyCredentials = null };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        bool Installed() => Directory.Exists(desktop) && Directory.EnumerateDirectories(desktop).Any(path =>
            Version.TryParse(Path.GetFileName(path), out var version) && version.Major == 10 && version.Minor == 0 && version.Build >= 0);
        var download = new DesktopRuntimeDownload(client, Path.Combine(DownloadsDirectory(), "inst-dotnet-10.0.0-X64-WindowsDesktop.exe"),
            MaintenancePaths.StateRoot, Installed, RuntimeInstallerTrust.VerifyMicrosoft);
        await download.PrepareCacheAsync(token).ConfigureAwait(false);
    }

    internal async Task PrepareCacheAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (installed()) return;
        MaintenancePaths.RequireNoLinks(cachePath);
        MaintenancePaths.RequireNoLinks(workRoot);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(3));
        byte[] metadata = await MetadataAsync(budget.Token).ConfigureAwait(false);
        var release = Parse(metadata);
        if (File.Exists(cachePath))
        {
            var info = new FileInfo(cachePath);
            if (info.Length is <= 0 or > MaxInstallerBytes) throw new InvalidDataException("The existing desktop runtime download is invalid.");
            await using var cache = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            byte[] hash = await SHA512.HashDataAsync(cache, budget.Token).ConfigureAwait(false);
            if (!release.AllowedHashes.Any(allowed => CryptographicOperations.FixedTimeEquals(hash, allowed)))
                throw new InvalidDataException("The existing desktop runtime download does not match Microsoft metadata.");
            verifySignature(cachePath);
            budget.Token.ThrowIfCancellationRequested();
            return;
        }
        Directory.CreateDirectory(workRoot);
        string temporary = MaintenancePaths.Child(workRoot, "desktop-runtime-" + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            await DownloadAsync(release.Download, temporary, release.Sha512, budget.Token).ConfigureAwait(false);
            verifySignature(temporary);
            budget.Token.ThrowIfCancellationRequested();
            MaintenancePaths.RequireNoLinks(cachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            // Never overwrite a shared SDK cache that appeared while this download was in flight.
            File.Move(temporary, cachePath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary); // Exact owned newly-created file only.
        }
    }

    private async Task<byte[]> MetadataAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MetadataUri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        ValidateResponse(response, MaxMetadataBytes);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaxMetadataBytes) throw new InvalidDataException("Desktop runtime metadata exceeds its budget.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task DownloadAsync(Uri uri, string path, byte[] expected, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        ValidateResponse(response, MaxInstallerBytes);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        byte[] buffer = new byte[65536];
        long length = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            length += count;
            if (length > MaxInstallerBytes) throw new InvalidDataException("Desktop runtime installer exceeds its budget.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        if (length == 0 || (response.Content.Headers.ContentLength is long declared && declared != length)
            || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
            throw new InvalidDataException("Desktop runtime installer does not match Microsoft metadata.");
    }

    private static void ValidateResponse(HttpResponseMessage response, long maximum)
    {
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Microsoft desktop runtime download was unsuccessful.");
        if (response.Content.Headers.ContentLength is long size && (size <= 0 || size > maximum))
            throw new InvalidDataException("Microsoft desktop runtime response exceeds its budget.");
    }

    private sealed record RuntimeRelease(Uri Download, byte[] Sha512, IReadOnlyList<byte[]> AllowedHashes);

    private static RuntimeRelease Parse(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        string latest = json.RootElement.GetProperty("latest-runtime").GetString() ?? throw new InvalidDataException("Runtime version is missing.");
        if (!Version.TryParse(latest, out var version) || version.Major != 10 || version.Minor != 0 || version.Build < 0
            || version.Revision != -1 || version.ToString() != latest) throw new InvalidDataException("Unsupported desktop runtime version.");
        var allowed = new List<byte[]>();
        byte[]? expected = null;
        foreach (var item in json.RootElement.GetProperty("releases").EnumerateArray())
        {
            if (!item.TryGetProperty("windowsdesktop", out var desktop)) continue;
            string? desktopVersion = desktop.GetProperty("version").GetString();
            if (!Version.TryParse(desktopVersion, out var parsed) || parsed.Major != 10 || parsed.Minor != 0 || parsed.Build < 0 || parsed.Revision != -1) continue;
            foreach (var file in desktop.GetProperty("files").EnumerateArray())
            {
                if (file.GetProperty("rid").GetString() != "win-x64" || file.GetProperty("name").GetString() != "windowsdesktop-runtime-win-x64.exe") continue;
                string text = file.GetProperty("hash").GetString() ?? "";
                if (text.Length != 128 || !text.All(Uri.IsHexDigit)) throw new InvalidDataException("Runtime hash is invalid.");
                var declared = new Uri(file.GetProperty("url").GetString() ?? "", UriKind.Absolute);
                string suffix = "/dotnet/WindowsDesktop/" + desktopVersion + "/windowsdesktop-runtime-" + desktopVersion + "-win-x64.exe";
                if (declared.Scheme != Uri.UriSchemeHttps || declared.UserInfo.Length != 0 || declared.Query.Length != 0 || declared.Fragment.Length != 0
                    || !declared.IsDefaultPort || declared.AbsolutePath != suffix
                    || (declared.Host != "builds.dotnet.microsoft.com" && declared.Host != "dotnetcli.blob.core.windows.net"))
                    throw new InvalidDataException("Runtime metadata contains an unsupported source.");
                byte[] digest = Convert.FromHexString(text);
                allowed.Add(digest);
                if (desktopVersion == latest)
                {
                    if (expected != null) throw new InvalidDataException("Duplicate current runtime installer.");
                    expected = digest;
                }
            }
        }
        if (expected == null) throw new InvalidDataException("Current desktop runtime installer is missing.");
        return new(new Uri("https://dotnetcli.blob.core.windows.net/dotnet/WindowsDesktop/" + latest + "/windowsdesktop-runtime-" + latest + "-win-x64.exe"), expected, allowed);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);

    private static string DownloadsDirectory()
    {
        var folder = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        int result = SHGetKnownFolderPath(in folder, 0, 0, out nint pointer);
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Downloads folder is unavailable."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
}
