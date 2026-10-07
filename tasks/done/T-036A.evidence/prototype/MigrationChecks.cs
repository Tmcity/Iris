using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Core.IO;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

internal static class MigrationChecks
{
    internal static async Task Run()
    {
        foreach (string phase in new[] { "stage", "backup", "current", "success" })
        {
            using var area = new TempArea(); var secrets = new MemorySecrets();
            string raw = "{\"schemaVersion\":1,\"general\":{\"unknownUntouched\":42},\"network\":{\"proxyMode\":\"custom\",\"customProxyUrl\":\"http://synthetic%40user:synthetic%3Apassword@proxy.example.test:8080\",\"bypassProxyOnLocal\":false}}";
            await File.WriteAllTextAsync(area.Paths.SettingsFile, raw); await File.WriteAllTextAsync(area.Paths.SettingsFile + ".old", raw);
            if (phase == "stage") secrets.FailSet = true;
            bool failed = false;
            try { await MigrateAsync(area.Paths.SettingsFile, secrets, phase); } catch (IOException) { failed = true; }
            Check.That(failed == (phase != "success"), "migration failure phase");
            if (failed) Check.That(File.ReadAllText(area.Paths.SettingsFile) == raw, "migration failure keeps current source recoverable");
            if (phase == "stage" || phase == "backup") Check.That(secrets.Values.IsEmpty, "unused migration staging cleaned");
            if (phase == "current")
            {
                Check.That(secrets.Values.Count == 1 && !File.ReadAllText(area.Paths.SettingsFile + ".bak").Contains("synthetic%3Apassword"), "published sanitized backup keeps its staged reference after current-write failure");
                var backup = ReadConfig(File.ReadAllText(area.Paths.SettingsFile + ".bak"));
                Check.That(ProxyFormat.Resolve(backup, secrets)!.Password == "synthetic:password", "sanitized backup reference usable");
                await MigrateAsync(area.Paths.SettingsFile, secrets, "success");
            }
            if (phase is "success" or "current")
            {
                foreach (var path in new[] { area.Paths.SettingsFile, area.Paths.SettingsFile + ".bak" })
                {
                    string data = File.ReadAllText(path); var config = ReadConfig(data);
                    Check.That(new Uri(config.CustomProxyUrl!).UserInfo.Length == 0 && !data.Contains("synthetic:password") && !data.Contains("synthetic%3Apassword"), "new current/backup must contain no userinfo secret");
                    Check.That(ProxyFormat.Resolve(config, secrets)!.UserName == "synthetic@user", "userinfo percent decoding preserved");
                    Check.That(JsonNode.Parse(data)!["general"]!["unknownUntouched"]!.GetValue<int>() == 42, "unrelated JSON retained");
                }
            }
            Check.That(File.ReadAllText(area.Paths.SettingsFile + ".old") == raw, "historical backups are not erased");
            Check.Pass("proposal migration " + phase + ": failureChecks=" + failed + " backupCreated=" + File.Exists(area.Paths.SettingsFile + ".bak") + " historicalBackupUntouched=true");
        }
        using var futureArea = new TempArea(); var futureSecrets = new MemorySecrets();
        string future = "{\"schemaVersion\":999,\"network\":{\"customProxyUrl\":\"http://synthetic:future@proxy.example.test\"},\"unknown\":42}";
        await File.WriteAllTextAsync(futureArea.Paths.SettingsFile, future);
        bool readOnly = await MigrateAsync(futureArea.Paths.SettingsFile, futureSecrets, "success");
        Check.That(readOnly && File.ReadAllText(futureArea.Paths.SettingsFile) == future && futureSecrets.Values.IsEmpty && !File.Exists(futureArea.Paths.SettingsFile + ".bak"), "future schema must not migrate, copy plaintext or stage credentials");
        var guarded = new ProposalStore(futureArea.Paths.SettingsFile) { IsReadOnly = true }; bool denied = false;
        try { await guarded.UpdateAsync(_ => new(ProxyMode.None, null, false)); } catch (IOException) { denied = true; }
        Check.That(denied && File.ReadAllText(futureArea.Paths.SettingsFile) == future, "future read-only update latch");
        Check.Pass("proposal future-schema read-only: zero migration/backup/staging, Update denied");

        using var corruptArea = new TempArea(); string corrupt = "{broken synthetic original"; await File.WriteAllTextAsync(corruptArea.Paths.SettingsFile, corrupt);
        using var actual = CurrentChecks.StoreServices(corruptArea.Paths); var settings = actual.GetRequiredService<ISettingsService>();
        try { await settings.LoadAsync(); } catch (Iris.Core.Errors.IrisException ex) when (ex.Code == Iris.Core.Errors.ErrorCode.SettingsCorrupted) { }
        var moved = Directory.GetFiles(corruptArea.DirectoryPath, "settings.corrupt-*.json");
        Check.That(moved.Length == 1 && File.ReadAllText(moved[0]) == corrupt, "actual corrupt-file preservation must remain exact");
        Check.Pass("actual corrupt-file recovery renames original bytes unchanged; not described as credential erasure");
        await LegacyFixedKey();
    }
    private static async Task LegacyFixedKey()
    {
        using var area = new TempArea(); var secrets = new MemorySecrets();
        var config = new ProxyConfig(ProxyMode.Custom, ProxyFormat.Clean("http://legacy-proxy.example.test:8080"), true);
        await File.WriteAllTextAsync(area.Paths.SettingsFile, ProposalStore.Document(config).ToJsonString(ProxyFormat.Json));
        secrets.Set(ProxyFormat.LegacyKey, ProxyFormat.Serialize(config.CustomProxyUrl, new("synthetic-legacy", "synthetic-secret")));
        await MigrateAsync(area.Paths.SettingsFile, secrets, "success"); var migrated = ReadConfig(File.ReadAllText(area.Paths.SettingsFile));
        Check.That(migrated.ProxyCredentialSetId != null && ProxyFormat.Resolve(migrated, secrets)!.UserName == "synthetic-legacy" && !secrets.Values.ContainsKey(ProxyFormat.LegacyKey), "bound fixed-key import");
        secrets.Set(ProxyFormat.LegacyKey, ProxyFormat.Serialize("http://other-proxy.example.test/", new("wrong", "wrong")));
        string before = File.ReadAllText(area.Paths.SettingsFile); await MigrateAsync(area.Paths.SettingsFile, secrets, "success");
        Check.That(File.ReadAllText(area.Paths.SettingsFile) == before && ProxyFormat.Resolve(migrated, secrets)!.UserName == "synthetic-legacy", "explicit reference must suppress fixed-key fallback");
        await File.WriteAllTextAsync(area.Paths.SettingsFile, ProposalStore.Document(config).ToJsonString(ProxyFormat.Json)); before = File.ReadAllText(area.Paths.SettingsFile);
        bool rejected = false; try { await MigrateAsync(area.Paths.SettingsFile, secrets, "success"); } catch (IOException) { rejected = true; }
        Check.That(rejected && File.ReadAllText(area.Paths.SettingsFile) == before, "unbound/wrong-endpoint legacy key cannot be guessed");
        Check.Pass("proposal bound fixed-key import; explicit references suppress legacy; mismatched legacy preserves file and fails closed");
    }
    private static ProxyConfig ReadConfig(string json) => JsonSerializer.Deserialize<ProxyConfig>(JsonNode.Parse(json)!["network"]!.ToJsonString(), ProxyFormat.Json)!;
    private static async Task<bool> MigrateAsync(string path, MemorySecrets secrets, string fail)
    {
        string original = await File.ReadAllTextAsync(path); var root = JsonNode.Parse(original)!.AsObject();
        if (root["schemaVersion"]!.GetValue<int>() > AppSettings.CurrentSchemaVersion) return true;
        var config = ReadConfig(original);
        if (config.CustomProxyUrl == null) return false;
        var uri = new Uri(config.CustomProxyUrl); string rawInfo = uri.UserInfo; ProxySecret? secret;
        if (rawInfo.Length != 0 && config.ProxyCredentialSetId != null) throw new IOException("Conflicting legacy userinfo and explicit credential reference.");
        if (rawInfo.Length == 0)
        {
            if (config.ProxyCredentialSetId != null) return false;
            string? legacy = secrets.Get(ProxyFormat.LegacyKey); if (legacy == null) return false;
            SecretEnvelope envelope;
            try { envelope = JsonSerializer.Deserialize<SecretEnvelope>(legacy, ProxyFormat.Json)!; } catch (JsonException) { throw new IOException("Legacy credentials require explicit repair."); }
            if (envelope == null || envelope.Format != "iris-proxy-credentials" || envelope.Version != 1 || envelope.ProxyUri != ProxyFormat.Clean(config.CustomProxyUrl) || (envelope.UserName == null) != (envelope.Password == null)) throw new IOException("Legacy credential endpoint mismatch.");
            secret = envelope.UserName == null ? null : new(envelope.UserName, envelope.Password);
        }
        else
        {
            int split = rawInfo.IndexOf(':'); string user = Uri.UnescapeDataString(split < 0 ? rawInfo : rawInfo[..split]);
            string password = split < 0 ? "" : Uri.UnescapeDataString(rawInfo[(split + 1)..]); secret = new(user, password);
        }
        var clean = config with { CustomProxyUrl = ProxyFormat.Clean(config.CustomProxyUrl, allowUserinfo: true), ProxyCredentialSetId = ProxyFormat.Id() };
        bool backupPublished = false;
        try
        {
            await Task.Run(() => secrets.Set(ProxyFormat.Key(clean.ProxyCredentialSetId!), ProxyFormat.Serialize(clean.CustomProxyUrl, secret)));
            root["network"]!["customProxyUrl"] = clean.CustomProxyUrl; root["network"]!["proxyCredentialSetId"] = clean.ProxyCredentialSetId;
            string sanitized = root.ToJsonString(ProxyFormat.Json);
            if (await File.ReadAllTextAsync(path) != original) throw new IOException("Configuration changed during migration.");
            if (fail == "backup") throw new IOException("Synthetic safe-backup failure.");
            await AtomicFile.WriteAllTextAsync(path + ".bak", sanitized, null, CancellationToken.None); backupPublished = true;
            if (fail == "current") throw new IOException("Synthetic current-write failure.");
            await AtomicFile.WriteAllTextAsync(path, sanitized, null, CancellationToken.None);
            try { secrets.Remove(ProxyFormat.LegacyKey); } catch (IOException) { }
            return false;
        }
        catch
        {
            if (!backupPublished) try { secrets.Remove(ProxyFormat.Key(clean.ProxyCredentialSetId!)); } catch (IOException) { }
            throw;
        }
    }
}
