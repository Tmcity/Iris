using System.IO;
using Iris.Platform.Windows.Maintenance;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Iris.App.Updates;

internal sealed class ValidatedUpdateSource(IUpdateSource inner, CancellationToken token) : IUpdateSource
{
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        token.ThrowIfCancellationRequested();
        // No staging identifier, machine identity or local payload is sent to the release source.
        var feed = await inner.GetReleaseFeed(logger, "Iris", "win", null, null).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (feed.Assets.Length > 256) throw ReleaseIdentity.Invalid();
        var full = feed.Assets.Where(asset => asset.Type == VelopackAssetType.Full).ToArray();
        if (full.Length == 0) throw new NoCompatibleReleaseException();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in full)
        {
            Validate(asset);
            if (!names.Add(asset.FileName) || !versions.Add(asset.Version.ToString())) throw ReleaseIdentity.Invalid();
        }
        return new VelopackAssetFeed { Assets = full };
    }
    internal static void Validate(VelopackAsset asset)
    {
        if (asset.PackageId != "Iris" || asset.Type != VelopackAssetType.Full || !ReleaseIdentity.IsVersion(asset.Version.ToString())
            || string.IsNullOrEmpty(asset.FileName) || asset.FileName != Path.GetFileName(asset.FileName)
            || asset.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || !asset.FileName.Equals($"Iris-{asset.Version}-full.nupkg", StringComparison.Ordinal)
            || asset.Size is <= 0 or > ReleasePackage.MaxBytes || asset.SHA256?.Length != 64 || !asset.SHA256.All(Uri.IsHexDigit)) throw ReleaseIdentity.Invalid();
    }
    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        token.ThrowIfCancellationRequested(); cancelToken.ThrowIfCancellationRequested(); Validate(releaseEntry);
        return inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, token);
    }
}
