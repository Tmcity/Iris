using System.IO;
using System.Net.Http;
using System.Reflection;
using Iris.Platform.Windows.Maintenance;
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
    internal sealed class LatestGithubSource(IFileDownloader downloader) : GithubSource(ReleaseIdentity.OfficialRepository, null, false, downloader)
    {
        protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
        {
            var releases = await base.GetReleases(false).ConfigureAwait(false);
            // Each vpk feed already includes the version history. Do not merge copies of that history from older releases.
            return releases.Where(release => release.Assets.Any(asset => asset.Name == "releases.win.json")).Take(1).ToArray();
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
