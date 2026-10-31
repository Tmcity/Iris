using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Iris.Platform.Windows.Maintenance;

namespace Iris.Maintenance;

internal enum DesktopRuntimeStep { Metadata, MetadataValidation, CacheValidation, Download, Signature, Publish }
internal enum RuntimeTransferStep { Response, ResponseValidation, OpenInput, OpenOutput, Read, Write, Flush, Hash }

internal sealed class DesktopRuntimeDownload(HttpClient http, string cachePath, string workRoot, Func<bool> installed, Action<string> verifySignature)
{
    internal const string FailureStepKey = "Iris.DesktopRuntimeStep";
    internal const string HttpStatusKey = "Iris.DesktopRuntimeHttpStatus";
    internal const string TransferStepKey = "Iris.RuntimeTransferStep";
    internal const string TransferBytesKey = "Iris.RuntimeTransferBytes";
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
        byte[] metadata = await AtStepAsync(DesktopRuntimeStep.Metadata, () => MetadataAsync(budget.Token)).ConfigureAwait(false);
        var release = AtStep(DesktopRuntimeStep.MetadataValidation, () => Parse(metadata));
        if (File.Exists(cachePath))
        {
            await using var cache = AtStep(DesktopRuntimeStep.CacheValidation,
                () => new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous));
            await AtStepAsync(DesktopRuntimeStep.CacheValidation, async () =>
            {
                if (cache.Length is <= 0 or > MaxInstallerBytes) throw new InvalidDataException("The existing desktop runtime download is invalid.");
                byte[] hash = await SHA512.HashDataAsync(cache, budget.Token).ConfigureAwait(false);
                if (!release.AllowedHashes.Any(allowed => CryptographicOperations.FixedTimeEquals(hash, allowed)))
                    throw new InvalidDataException("The existing desktop runtime download does not match Microsoft metadata.");
                return true;
            }).ConfigureAwait(false);
            AtStep(DesktopRuntimeStep.Signature, () => { verifySignature(cachePath); return true; });
            budget.Token.ThrowIfCancellationRequested();
            return;
        }
        Directory.CreateDirectory(workRoot);
        string temporary = MaintenancePaths.Child(workRoot, "desktop-runtime-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await AtStepAsync(DesktopRuntimeStep.Download, async () => { await DownloadAsync(release.Download, temporary, release.Sha512, budget.Token).ConfigureAwait(false); return true; }).ConfigureAwait(false);
            AtStep(DesktopRuntimeStep.Signature, () => { verifySignature(temporary); return true; });
            budget.Token.ThrowIfCancellationRequested();
            MaintenancePaths.RequireNoLinks(cachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            // Never overwrite a shared SDK cache that appeared while this download was in flight.
            AtStep(DesktopRuntimeStep.Publish, () => { File.Move(temporary, cachePath, overwrite: false); return true; });
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary); // Exact owned newly-created file only.
        }
    }

    private static T AtStep<T>(DesktopRuntimeStep step, Func<T> action)
    {
        try { return action(); }
        catch (Exception error) { error.Data[FailureStepKey] = step; throw; }
    }

    private static async Task<T> AtStepAsync<T>(DesktopRuntimeStep step, Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) { error.Data[FailureStepKey] = step; throw; }
    }

    private async Task<byte[]> MetadataAsync(CancellationToken token)
    {
        using var response = await ResponseAsync(MetadataUri, token).ConfigureAwait(false);
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
        var step = RuntimeTransferStep.Response;
        long length = 0;
        try
        {
            step = RuntimeTransferStep.OpenOutput;
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            long? total = null;
            const int blockBytes = 8 * 1024 * 1024;
            do
            {
                byte[]? block = null;
                for (int attempt = 0; block == null; attempt++)
                {
                    try
                    {
                        step = RuntimeTransferStep.Response;
                        using var response = await ResponseAsync(uri, token, length, length + blockBytes - 1).ConfigureAwait(false);
                        step = RuntimeTransferStep.ResponseValidation;
                        long expectedLength;
                        long maximum;
                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            var range = response.Content.Headers.ContentRange;
                            if (range?.Unit != "bytes" || range.From != length || range.To == null || range.Length == null
                                || range.To < range.From || range.To > length + blockBytes - 1 || range.Length > MaxInstallerBytes
                                || range.Length <= range.To || (total.HasValue && total != range.Length))
                                throw new InvalidDataException("Desktop runtime response range is invalid.");
                            total = range.Length;
                            expectedLength = range.To.Value - length + 1;
                            maximum = blockBytes;
                        }
                        else
                        {
                            ValidateResponse(response, MaxInstallerBytes);
                            if (length != 0) throw new InvalidDataException("Desktop runtime source ignored a continuation range.");
                            expectedLength = response.Content.Headers.ContentLength ?? -1;
                            maximum = MaxInstallerBytes;
                        }
                        step = RuntimeTransferStep.OpenInput;
                        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        using var buffer = new MemoryStream();
                        byte[] bytes = new byte[65536];
                        int count;
                        step = RuntimeTransferStep.Read;
                        while ((count = await input.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
                        {
                            if (buffer.Length + count > maximum) throw new InvalidDataException("Desktop runtime response exceeds its range budget.");
                            buffer.Write(bytes, 0, count);
                        }
                        if (buffer.Length == 0 || (expectedLength >= 0 && expectedLength != buffer.Length))
                            throw new InvalidDataException("Desktop runtime response length differs.");
                        block = buffer.ToArray();
                        if (response.StatusCode == HttpStatusCode.OK) total = block.Length;
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), token).ConfigureAwait(false);
                    }
                    catch (HttpRequestException) when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), token).ConfigureAwait(false);
                    }
                }
                hash.AppendData(block);
                step = RuntimeTransferStep.Write;
                await output.WriteAsync(block, token).ConfigureAwait(false);
                length += block.Length;
            }
            while (length < total);
            step = RuntimeTransferStep.Flush;
            await output.FlushAsync(token).ConfigureAwait(false);
            step = RuntimeTransferStep.Hash;
            if (length == 0 || length != total || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                throw new InvalidDataException("Desktop runtime installer does not match Microsoft metadata.");
        }
        catch (Exception error)
        {
            error.Data[TransferStepKey] = step;
            error.Data[TransferBytesKey] = length;
            throw;
        }
    }

    private static void ValidateResponse(HttpResponseMessage response, long maximum)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var error = new IOException("Microsoft desktop runtime download was unsuccessful.");
            error.Data[HttpStatusKey] = (int)response.StatusCode;
            throw error;
        }
        if (response.Content.Headers.ContentLength is long size && (size <= 0 || size > maximum))
            throw new InvalidDataException("Microsoft desktop runtime response exceeds its budget.");
    }

    private async Task<HttpResponseMessage> ResponseAsync(Uri uri, CancellationToken token, long? from = null, long? to = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (from.HasValue) request.Headers.Range = new RangeHeaderValue(from, to);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (attempt >= 2 || status is not (429 or 500 or 502 or 503 or 504)) return response;
            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), token).ConfigureAwait(false);
        }
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
