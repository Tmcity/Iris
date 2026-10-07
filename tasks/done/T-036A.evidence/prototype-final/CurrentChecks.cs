using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Infrastructure.DependencyInjection;
using Iris.Providers.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class CurrentChecks
{
    internal static ServiceProvider StoreServices(TestPaths paths, LogCapture? log = null)
    {
        var services = new ServiceCollection(); services.AddLogging(b => { if (log != null) b.AddProvider(log); });
        services.AddSingleton<IAppPaths>(paths); services.AddIrisInfrastructure(); return services.BuildServiceProvider();
    }
    internal static async Task Run()
    {
        var state = new MemorySettings(); var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ISettingsService>(state); services.AddIrisProviders();
        using var provider = services.BuildServiceProvider();
        var type = typeof(ProvidersServiceCollectionExtensions).Assembly.GetType("Iris.Providers.Http.DynamicWebProxy")!;
        var proxy = (IWebProxy)provider.GetRequiredService(type); var target = new Uri("http://service.example.test/resource");
        state.Value = new() { Network = new() { ProxyMode = ProxyMode.Custom, CustomProxyUrl = "http://proxy-a.example.test:8080", BypassProxyOnLocal = false } };
        int reads = 0;
        state.OnRead = () => { reads++; return reads == 1 ? state.Value : state.Value with { Network = state.Value.Network with { ProxyMode = ProxyMode.None } }; };
        Check.That(proxy.GetProxy(target) == null && reads == 2, "current GetProxy should demonstrate two independent Current reads");
        Console.WriteLine("COUNTEREXAMPLE current.DynamicWebProxy mixed Current reads=2 selectedDirect=true");
        state.OnRead = null; state.Value = state.Value with { Network = state.Value.Network with { CustomProxyUrl = "not-a-uri" } };
        Check.That(proxy.GetProxy(target) == null, "current invalid custom proxy direct behavior");
        Console.WriteLine("COUNTEREXAMPLE current.InvalidCustomUrl returnsDirect=true");
        HttpClient.DefaultProxy = new WebProxy("http://system-proxy.example.test:8080") { Credentials = CredentialCache.DefaultCredentials };
        state.Value = state.Value with { Network = new() { ProxyMode = ProxyMode.System, BypassProxyOnLocal = false } };
        Check.That(ReferenceEquals(proxy.Credentials, CredentialCache.DefaultCredentials), "system default credentials");
        state.Value = state.Value with { Network = new() { ProxyMode = ProxyMode.None } };
        Check.That(proxy.IsBypassed(target) && proxy.GetProxy(target) == null, "None must be direct");
        Check.Pass("current System credentials and None routing through real DynamicWebProxy; system boundary replaced in owned process");

        using var area = new TempArea(); var logs = new LogCapture();
        using var storeProvider = StoreServices(area.Paths, logs); var store = storeProvider.GetRequiredService<ISettingsService>();
        await store.LoadAsync();
        await store.UpdateAsync(s => s with { Network = new() { ProxyMode = ProxyMode.Custom, CustomProxyUrl = "http://synthetic-user:synthetic-password@proxy.example.test:8080", BypassProxyOnLocal = false } });
        Check.That(new Uri(store.Current.Network.CustomProxyUrl!).UserInfo.Length > 0, "normalizer currently allows userinfo");
        await store.UpdateAsync(s => s with { General = s.General with { LogLevel = "Warning" } });
        Check.That(File.ReadAllText(area.Paths.SettingsFile).Contains("synthetic-password") && File.ReadAllText(area.Paths.SettingsFile + ".bak").Contains("synthetic-password"), "current backup replicates userinfo");
        Check.That(!logs.Messages.Any(m => m.Contains("synthetic-password")), "credentials must not be logged");
        Console.WriteLine("COUNTEREXAMPLE actual JsonSettingsService currentUserinfo=true newBackupUserinfo=true logsContainSecret=false");

        using var futureArea = new TempArea();
        var root = JsonNode.Parse(File.ReadAllText(area.Paths.SettingsFile))!.AsObject(); root["schemaVersion"] = AppSettings.CurrentSchemaVersion + 1;
        await File.WriteAllTextAsync(futureArea.Paths.SettingsFile, root.ToJsonString()); string before = File.ReadAllText(futureArea.Paths.SettingsFile);
        using var futureProvider = StoreServices(futureArea.Paths); var future = futureProvider.GetRequiredService<ISettingsService>(); await future.LoadAsync();
        Check.That(File.ReadAllText(futureArea.Paths.SettingsFile) == before, "future load does not overwrite");
        await future.UpdateAsync(s => s with { History = s.History with { Enabled = !s.History.Enabled } });
        Check.That(File.ReadAllText(futureArea.Paths.SettingsFile) != before, "future update currently overwrites");
        Console.WriteLine("COUNTEREXAMPLE actual future-schema loadPreserved=true updateOverwrote=true currentSchemaWasNormalized=true");

        using var fixedArea = new TempArea(); using var fixedProvider = StoreServices(fixedArea.Paths); var fixedSettings = fixedProvider.GetRequiredService<ISettingsService>(); await fixedSettings.LoadAsync();
        await fixedSettings.UpdateAsync(s => s with { Network = new() { ProxyMode = ProxyMode.Custom, CustomProxyUrl = "http://proxy-a.example.test:8080", BypassProxyOnLocal = false } });
        var secrets = new MemorySecrets(); secrets.Set("network/proxy-credentials", "synthetic-A");
        var stagedGate = new Gate();
        var writer = Task.Run(async () =>
        {
            secrets.Set("network/proxy-credentials", "synthetic-B"); await stagedGate.Pause();
            try { await fixedSettings.UpdateAsync(_ => throw new IOException("synthetic settings failure")); } catch (IOException) { }
        });
        try
        {
            await stagedGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pair = await Task.Run(() => (fixedSettings.Current.Network.CustomProxyUrl, Credential: secrets.Get("network/proxy-credentials")));
            Check.That(pair.CustomProxyUrl!.Contains("proxy-a.") && pair.Credential == "synthetic-B", "fixed key concurrent transient mismatch");
        }
        finally { stagedGate.Open(); await writer; }
        Check.That(fixedSettings.Current.Network.CustomProxyUrl!.Contains("proxy-a.") && secrets.Get("network/proxy-credentials") == "synthetic-B", "fixed key mismatch persists after settings failure");
        Console.WriteLine("COUNTEREXAMPLE fixed-key stage-before-settings route=A credentials=B afterSettingsFailure=true");
        secrets.Set("network/proxy-credentials", "synthetic-A");
        await fixedSettings.UpdateAsync(s => s with { Network = s.Network with { CustomProxyUrl = "http://proxy-b.example.test:8080" } });
        var reversedPair = await Task.Run(() => (fixedSettings.Current.Network.CustomProxyUrl, Credential: secrets.Get("network/proxy-credentials")));
        Check.That(reversedPair.CustomProxyUrl!.Contains("proxy-b.") && reversedPair.Credential == "synthetic-A", "settings-first ordering also exposes mismatched pair");
        Console.WriteLine("COUNTEREXAMPLE fixed-key settings-before-secret route=B credentials=A concurrentReader=true");
    }
}
