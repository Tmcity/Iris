using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal static class ActualCoordinatorChecks
{
    internal static async Task Run()
    {
        foreach (string phase in new[] { "stage", "settings", "conflict", "apply", "retire", "success" })
        {
            using var area = new TempArea(); using var storage = CurrentChecks.StoreServices(area.Paths);
            var actual = storage.GetRequiredService<ISettingsService>(); await actual.LoadAsync();
            var oldId = "cs-" + Guid.NewGuid().ToString("N");
            var profile = new TranslationProviderProfile { Id = "tp-abcabcabcabc", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "synthetic", Enabled = false, BaseUrl = "http://endpoint.example.test", Model = "model", OpenAIOptions = new(), CredentialSetId = oldId };
            await actual.UpdateAsync(s => s with { Translation = s.Translation with { Providers = [profile] } });
            var secrets = new MemorySecrets(); secrets.Set(SecretKeys.CredentialSet(oldId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"synthetic-old\",\"secretKey\":null}");
            var wrapped = new InterceptStore(actual); var observer = new ActualObserver();
            var services = new ServiceCollection(); services.AddLogging(); services.AddIrisCore();
            services.AddSingleton<ISettingsService>(wrapped); services.AddSingleton<ISecretStore>(secrets);
            services.RemoveAll<ITranslationProviderRegistry>(); services.AddSingleton<ITranslationProviderRegistry, EmptyRegistry>();
            services.RemoveAll<IOcrProviderRegistry>(); services.RemoveAll<IProviderConfigurationObserver>(); services.AddSingleton<IProviderConfigurationObserver>(observer);
            using var provider = services.BuildServiceProvider(); var coordinator = provider.GetRequiredService<ISettingsEditCoordinator>();
            profile = actual.Current.Translation.Providers.Single(p => p.Id == profile.Id);
            if (phase == "stage") secrets.FailSet = true;
            if (phase == "settings") wrapped.BeforeUpdate = () => throw new IOException("synthetic storage failure");
            if (phase == "conflict") wrapped.BeforeUpdate = () => actual.UpdateAsync(s => s with { Translation = s.Translation with { Providers = s.Translation.Providers.Select(p => p.Id == profile.Id ? p with { DisplayName = "external" } : p).ToArray() } });
            if (phase == "apply") observer.Fail = true;
            if (phase == "retire") secrets.FailRemove = true;
            var result = await coordinator.CommitAsync(new(new(), [new(TranslationProfileChangeKind.Update, profile, profile with { DisplayName = "updated" }, new(CredentialEditKind.Replace, new("synthetic-new", null)))]));
            bool precommit = phase is "stage" or "settings" or "conflict";
            Check.That(result.Status == (precommit ? SettingsEditStatus.Uncommitted : phase == "success" ? SettingsEditStatus.Applied : SettingsEditStatus.ApplyPending), "actual coordinator phase status");
            if (precommit) Check.That(secrets.Values.Count == 1 && secrets.Values.ContainsKey(SecretKeys.CredentialSet(oldId)), "actual coordinator rollback retains original credentials");
            else if (result.PendingApply != null)
            {
                int sets = secrets.Sets; string before = File.ReadAllText(area.Paths.SettingsFile);
                observer.Fail = false; secrets.FailRemove = false;
                Check.That((await coordinator.RetryApplyAsync(result.PendingApply)).IsComplete, "actual receipt retry");
                Check.That(secrets.Sets == sets && File.ReadAllText(area.Paths.SettingsFile) == before, "actual retry does not restage or recommit");
            }
            Check.Pass("actual SettingsEditCoordinator + JsonSettingsService Provider protocol " + phase + "; network extension remains prototype-only");
        }
    }
    private sealed class InterceptStore(ISettingsService inner) : ISettingsService
    {
        internal Func<Task>? BeforeUpdate;
        public AppSettings Current => inner.Current;
        public event EventHandler<SettingsChangedEventArgs>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public Task LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        { if (BeforeUpdate != null) await BeforeUpdate(); await inner.UpdateAsync(mutate, cancellationToken); }
    }
    private sealed class ActualObserver : IProviderConfigurationObserver
    {
        internal bool Fail;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Fail ? Task.FromException(new IOException("synthetic apply failure")) : Task.CompletedTask;
    }
    private sealed class EmptyRegistry : ITranslationProviderRegistry
    {
        public event EventHandler? Changed { add { } remove { } }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ITranslationProviderSnapshot> CreateTransientAsync(TranslationProviderProfile profile, ProviderCredentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
