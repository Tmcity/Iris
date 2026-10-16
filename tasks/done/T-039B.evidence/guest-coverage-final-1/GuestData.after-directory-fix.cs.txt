using System.Security.Cryptography;
using System.Text.Json;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Iris.Platform.Windows.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

internal static class GuestData
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("seed" or "verify")) throw new InvalidOperationException("Data scenario arguments.");
        string output = Path.GetFullPath(args[1]);
        if (!output.StartsWith(@"C:\IrisBOutput\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned output only.");
        var paths = new DataPaths();
        if (args[0] == "seed") Directory.CreateDirectory(paths.DataDirectory);
        else if (!Directory.Exists(paths.DataDirectory)) throw new DirectoryNotFoundException("Expected retained guest data is missing.");
        var services = new ServiceCollection(); services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>)); services.AddSingleton<IAppPaths>(paths);
        services.AddIrisInfrastructure(); services.AddIrisPlatformWindows();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettingsService>(); await settings.LoadAsync();
        var secrets = provider.GetRequiredService<ISecretStore>();
        var history = provider.GetRequiredService<ITranslationHistoryRepository>();
        const string key = "provider/t039b-owned/api-key", value = "synthetic-task-secret-with-no-service-access";
        if (args[0] == "seed")
        {
            await settings.UpdateAsync(s => s with { General = s.General with { CheckForUpdates = false, UiLanguage = "en-US", ShowStartupNotification = false }, History = s.History with { Enabled = true } });
            secrets.Set(key, value);
            await history.AddAsync(new() { CreatedAt = DateTimeOffset.UtcNow, SourceText = "Owned T039B retained source", SourceLanguage = "en", TargetLanguage = "zh-Hans", Origin = TextOrigin.Input, IsFavorite = true, Results = [new("synthetic", "Owned provider", "owned-model", "Owned retained result")] });
            Directory.CreateDirectory(paths.LogDirectory); await File.WriteAllTextAsync(Path.Combine(paths.LogDirectory, "owned-retention.log"), "Owned T039B log record\n");
        }
        var found = await history.QueryAsync(new("Owned T039B retained source", FavoritesOnly: true));
        bool settingsOk = !settings.Current.General.CheckForUpdates && settings.Current.General.UiLanguage == "en-US";
        bool secretsOk = secrets.Get(key) == value;
        bool historyOk = found.TotalCount == 1 && (await history.GetAsync(found.Items[0].Id))?.Results.Single().Text == "Owned retained result";
        bool logOk = File.Exists(Path.Combine(paths.LogDirectory, "owned-retention.log")) && await File.ReadAllTextAsync(Path.Combine(paths.LogDirectory, "owned-retention.log")) == "Owned T039B log record\n";
        var hashes = new Dictionary<string, string>();
        foreach (string path in new[] { paths.SettingsFile, paths.SecretsFile, paths.DatabaseFile, Path.Combine(paths.LogDirectory, "owned-retention.log") })
        { await using var input = File.OpenRead(path); hashes[Path.GetRelativePath(paths.DataDirectory, path)] = Convert.ToHexString(await SHA256.HashDataAsync(input)); }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { SettingsReadable = settingsOk, SecretDecrypts = secretsOk, HistoryReadable = historyOk, LogPreserved = logOk, Hashes = hashes }));
        return settingsOk && secretsOk && historyOk && logOk ? 0 : 2;
    }
    private sealed class DataPaths : IAppPaths
    {
        public bool IsPortable => false;
        public string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Iris");
        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
        public string DatabaseFile => Path.Combine(DataDirectory, "history.db");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
    }
}
