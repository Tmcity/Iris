using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Infrastructure.DependencyInjection;
using Iris.Platform.Windows.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

if (!string.Equals(Environment.UserName, "WDAGUtilityAccount", StringComparison.Ordinal))
    throw new InvalidOperationException("Owned T-040 Sandbox only.");
bool isolated = args.Length == 1 && string.Equals(args[0], "--validate-isolated", StringComparison.Ordinal);
if (args.Length != 0 && !isolated) throw new ArgumentException("Only the explicit isolated validation option is supported.");
var liveData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Iris");
var data = isolated ? Path.Combine(@"C:\IrisT040Output", "fixture-isolated-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), "data") : liveData;
if (isolated) { Directory.CreateDirectory(data); File.Copy(Path.Combine(liveData, "settings.json"), Path.Combine(data, "settings.json")); }
var paths = new FixturePaths(data);
if (!File.Exists(paths.SettingsFile) || File.Exists(paths.SecretsFile))
    throw new InvalidOperationException("Expected this new session's settings and absent secret store; no unknown credentials will be read.");
var output = Path.Combine(@"C:\IrisT040Output", "fixture-config-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture));
Directory.CreateDirectory(output);
byte[] before = await File.ReadAllBytesAsync(paths.SettingsFile).ConfigureAwait(false);
await File.WriteAllBytesAsync(Path.Combine(output, "settings-before.json"), before).ConfigureAwait(false);
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IAppPaths>(paths);
services.AddIrisInfrastructure();
services.AddIrisPlatformWindows();
using var provider = services.BuildServiceProvider();
var settings = provider.GetRequiredService<ISettingsService>();
await settings.LoadAsync().ConfigureAwait(false);
if (settings.IsReadOnly || settings.Current.Translation.Providers.Count != 0)
    throw new InvalidOperationException("Fixture initialization requires the current empty provider list.");
var id = "tp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
var setId = "cs-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
var profile = new TranslationProviderProfile
{
    Id = id, CredentialSetId = setId, Kind = TranslationProviderKind.OpenAICompatible,
    DisplayName = "T040 OpenAI fixture", Enabled = true, Order = 0,
    BaseUrl = "http://127.0.0.1:18040/v1", Model = "iris-fixture", OpenAIOptions = new()
};
var secrets = provider.GetRequiredService<ISecretStore>();
string encoded = JsonSerializer.Serialize(new { format = "iris-provider-credentials", version = 1, apiKey = "iris-t040-not-a-real-key", secretKey = (string?)null });
secrets.Set(SecretKeys.CredentialSet(setId), encoded);
await settings.UpdateAsync(current => current with { Translation = current.Translation with { Providers = [profile] } }).ConfigureAwait(false);
byte[] after = await File.ReadAllBytesAsync(paths.SettingsFile).ConfigureAwait(false);
await File.WriteAllBytesAsync(Path.Combine(output, "settings-after.json"), after).ConfigureAwait(false);
var record = new
{
    Utc = DateTimeOffset.UtcNow, ProductOperationPerformed = !isolated, IsolatedValidation = isolated, DataDirectory = data, OnlyOwnedSyntheticConfigurationAdded = true,
    SettingsConfiguredThroughPublicServiceApi = true, ConfigSaveThroughUiProved = false,
    ProviderId = id, CredentialSetId = setId, profile.BaseUrl, profile.Model,
    BeforeSettingsSha256 = Convert.ToHexStringLower(SHA256.HashData(before)),
    AfterSettingsSha256 = Convert.ToHexStringLower(SHA256.HashData(after)),
    SecretsFileBytes = new FileInfo(paths.SecretsFile).Length,
    SecretsFileSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(paths.SecretsFile).ConfigureAwait(false))),
    UnknownCredentialsRead = false, SecurityPolicyChanged = false, IrisStarted = false, InstallerExecuted = false
};
await File.WriteAllTextAsync(Path.Combine(output, "state.json"), JsonSerializer.Serialize(record)).ConfigureAwait(false);
Console.WriteLine(output);
return 0;

internal sealed class FixturePaths(string root) : IAppPaths
{
    public bool IsPortable => false;
    public string DataDirectory => root;
    public string SettingsFile => Path.Combine(root, "settings.json");
    public string SecretsFile => Path.Combine(root, "secrets.dat");
    public string DatabaseFile => Path.Combine(root, "history.db");
    public string LogDirectory => Path.Combine(root, "logs");
}
