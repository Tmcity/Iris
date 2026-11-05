using System.IO;
using System.Net.Http;
using System.Reflection;
using Iris.Platform.Windows.Maintenance;
using Iris.Core.Errors;
using Iris.Providers.Http;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Iris.App.Updates;

internal class UpdateRuntime(IHttpClientFactory clients)
{
    internal virtual string Version => typeof(UpdateRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
    internal virtual ReleaseIdentity? Identity()
    {
        if (!MaintenancePaths.IsInstalledApp(AppContext.BaseDirectory)) return null;
        var identity = ReleaseIdentity.Read(Path.Combine(AppContext.BaseDirectory, ReleaseIdentity.FileName));
        return identity.Version == Version ? identity : null;
    }
    internal virtual UpdateOperation Create(CancellationToken token)
    {
        if (Identity() == null) throw ReleaseIdentity.Invalid();
        var locator = new WindowsVelopackLocator(null, new NullVelopackLogger());
        if (locator.IsPortable || locator.AppId != "Iris" || locator.Channel != "win"
            || !MaintenancePaths.Same(locator.RootAppDir!, MaintenancePaths.InstallRoot)) throw ReleaseIdentity.Invalid();
        var downloader = new UpdateHttpDownloader(clients.CreateClient(HttpClientNames.Updates), token);
        // The operation owns the downloader before any SDK/source initialization can fail.
        return new(() => new UpdateManager(new ValidatedUpdateSource(new LatestGithubSource(downloader), token),
            new UpdateOptions { MaximumDeltasBeforeFallback = -1 }, locator), downloader, locator.PackagesDir!);
    }
    internal virtual Task ValidateAsync(UpdateCandidate candidate, CancellationToken token)
        => ReleasePackage.ValidateAsync(candidate.Path, candidate.Size, candidate.Sha256, candidate.Version, token);
    internal sealed class LatestGithubSource(IFileDownloader downloader) : IUpdateSource
    {
        private const string LatestFeedUri = ReleaseIdentity.OfficialRepository + "/releases/latest/download/releases.win.json";
        private readonly CompatibleGithubSource _compatible = new(downloader);

        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            try
            {
                // Published feeds can be read without spending the shared anonymous REST quota.
                var bytes = await downloader.DownloadBytes(LatestFeedUri).ConfigureAwait(false);
                return VelopackAssetFeed.FromJson(System.Text.Encoding.UTF8.GetString(bytes));
            }
            catch (IrisException error) when (error.Code == ErrorCode.EndpointNotFound)
            {
                // Preserve selection of the newest compatible release if the latest release has no desktop feed.
                return await _compatible.GetReleaseFeed(logger, "Iris", "win", null, null).ConfigureAwait(false);
            }
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default)
        {
            cancelToken.ThrowIfCancellationRequested();
            ValidatedUpdateSource.Validate(releaseEntry);
            // Latest may change after the offer. Keep this download bound to its validated version.
            string url = $"{ReleaseIdentity.OfficialRepository}/releases/download/v{releaseEntry.Version}/{releaseEntry.FileName}";
            return downloader.DownloadFile(url, localFile, progress, cancelToken: cancelToken);
        }

        private sealed class CompatibleGithubSource(IFileDownloader transport) : GithubSource(ReleaseIdentity.OfficialRepository, null, false, transport)
        {
            protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
            {
                var releases = await base.GetReleases(false).ConfigureAwait(false);
                // Each feed already contains its history; do not merge copies from older releases.
                return releases.Where(release => release.Assets.Any(asset => asset.Name == "releases.win.json")).Take(1).ToArray();
            }
        }
    }
}

internal sealed class UpdateOperation(Func<UpdateManager> createSdk, IAsyncDisposable resources, string packages)
{
    private UpdateManager? _sdk;
    internal UpdateOperation(UpdateManager sdk, IAsyncDisposable resources, string packages) : this(() => sdk, resources, packages) { }
    internal UpdateManager Sdk => _sdk ??= createSdk();
    internal string PackagePath(VelopackAsset asset) => MaintenancePaths.Child(packages, asset.FileName);
    internal ValueTask DisposeAsync() => resources.DisposeAsync();
}
