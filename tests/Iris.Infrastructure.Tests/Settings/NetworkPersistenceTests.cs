using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Infrastructure.Settings;
using Iris.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class NetworkPersistenceTests
{
    private const string PlainUrl = "http://synthetic%40user:synthetic%3Apassword@proxy.example.test:8080/";
    private const string CleanUrl = "http://proxy.example.test:8080/";
    private const string Id = "pc-00000000000000000000000000000001";
    private static string Document(string url = PlainUrl, string version = "1", string? reference = null) =>
        "{\"schemaVersion\":" + version + ",\"general\":{\"uiLanguage\":\"en-US\",\"unknownUntouched\":42},\"network\":{\"proxyMode\":\"custom\",\"customProxyUrl\":"
        + JsonSerializer.Serialize(url) + ",\"bypassProxyOnLocal\":false,\"proxyCredentialSetId\":" + JsonSerializer.Serialize(reference) + "}}";
    private static string Envelope(string url = CleanUrl) => JsonSerializer.Serialize(new { format = "iris-proxy-credentials", version = 1, proxyUri = url, userName = "fixed-user", password = "fixed-password" });
    private static JsonSettingsService Service(TempDirectory dir, Secrets secrets, IEnumerable<ISettingsMigration>? migrations = null) =>
        new(new FakeAppPaths(dir.Path), migrations ?? [], TimeProvider.System, NullLogger<JsonSettingsService>.Instance, () => secrets);

    [Theory]
    [InlineData("http://proxy.example.test/path/person@example.test")]
    [InlineData("http://proxy.example.test/?contact=person@example.test")]
    [InlineData("http://proxy.example.test/#person@example.test")]
    public async Task InvalidCustomWithoutAuthorityCredentials_PreservesIntentButCaptureStillRejects(string url)
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        string original = Document(url); await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets); await service.LoadAsync(TestContext.Current.CancellationToken);
        service.Current.Network.ProxyMode.ShouldBe(ProxyMode.Custom); service.Current.Network.CustomProxyUrl.ShouldBe(url);
        using var provider = Core(service, secrets);
        (await Should.ThrowAsync<IrisException>(() => provider.GetRequiredService<INetworkProxySnapshotSource>().CaptureCurrentAsync(new("http://origin.example.test/"), TestContext.Current.CancellationToken))).Code.ShouldBe(ErrorCode.InvalidArgument);
        await service.UpdateAsync(s => s with { General = s.General with { LogLevel = "Warning" } }, TestContext.Current.CancellationToken);
        service.Current.Network.CustomProxyUrl.ShouldBe(url); secrets.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("http://synthetic-user@proxy.example.test:8080/")]
    [InlineData("http://synthetic-user:@proxy.example.test:8080/")]
    [InlineData("https://synthetic-user@proxy.example.test:8080/")]
    public async Task LegacyUsernameOnlyAndExplicitEmptyPassword_MigrateAsEmptyHttpPassword(string url)
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        await File.WriteAllTextAsync(path, Document(url), TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets); await service.LoadAsync(TestContext.Current.CancellationToken);
        var value = JsonNode.Parse(secrets.Values[SecretKeys.ProxyCredentialSet(service.Current.Network.ProxyCredentialSetId!)])!;
        value["userName"]!.GetValue<string>().ShouldBe("synthetic-user"); value["password"]!.GetValue<string>().ShouldBe("");
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldNotContain("synthetic-user");
        (await File.ReadAllTextAsync(path + ".bak", TestContext.Current.CancellationToken)).ShouldNotContain("synthetic-user");
    }

    [Theory]
    [InlineData("http://review-user:review-password@bad host.invalid:8080/")]
    [InlineData("http://review-user:review-password@proxy.example.test:bad-port/")]
    [InlineData("socks5://review-user@proxy.example.test:1080/")]
    [InlineData("socks5://review-user:@proxy.example.test:1080/")]
    public async Task MalformedAuthorityOrEmptySocksPassword_FailBeforePublicationAndNewBackup(string url)
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        string original = Document(url); await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets);
        var error = await Should.ThrowAsync<IrisException>(() => service.LoadAsync(TestContext.Current.CancellationToken));
        error.Code.ShouldBe(ErrorCode.StorageFailure); error.ToString().ShouldNotContain("review-user"); error.ToString().ShouldNotContain("review-password");
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
        secrets.Writes.ShouldBe(0); File.Exists(path + ".bak").ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => service.UpdateAsync(s => s with { General = s.General with { LogLevel = "Warning" } }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://review-user:review-password@bad host.invalid:8080/")]
    [InlineData("http://review-user:review-password@proxy.example.test:8080/")]
    public async Task DirectUpdateCannotPersistRawAuthorityCredentialsEvenWhenUriParsingFails(string url)
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        using var service = Service(dir, secrets); await service.LoadAsync(TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var error = await Should.ThrowAsync<IrisException>(() => service.UpdateAsync(s => s with { Network = s.Network with { ProxyMode = ProxyMode.Custom, CustomProxyUrl = url } }, TestContext.Current.CancellationToken));
        error.Code.ShouldBe(ErrorCode.InvalidArgument); error.ToString().ShouldNotContain("review-password");
        File.Exists(path + ".bak").ShouldBeFalse(); (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
        await service.UpdateAsync(s => s with { General = s.General with { LogLevel = "Warning" } }, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path + ".bak", TestContext.Current.CancellationToken)).ShouldNotContain("review-password");
    }

    [Fact]
    public async Task UserInfoImport_StagesBeforeSafeBackupAndCurrent_PreservesFieldsAndUnrelatedFixedKey()
    {
        using var dir = new TempDirectory(); var paths = new FakeAppPaths(dir.Path); var secrets = new Secrets();
        secrets.Values[SecretKeys.LegacyProxyCredentials] = "unrelated-unknown-fixed-value";
        await File.WriteAllTextAsync(paths.SettingsFile, Document(), cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths.SettingsFile + ".old", "preexisting-plaintext-evidence", cancellationToken: TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets);
        secrets.OnSet = () => File.ReadAllText(paths.SettingsFile).ShouldContain("synthetic%3Apassword");
        await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        service.Current.Network.CustomProxyUrl.ShouldBe(CleanUrl);
        var id = service.Current.Network.ProxyCredentialSetId!; id.ShouldStartWith("pc-");
        var envelope = JsonNode.Parse(secrets.Values[SecretKeys.ProxyCredentialSet(id)])!;
        envelope["userName"]!.GetValue<string>().ShouldBe("synthetic@user");
        envelope["password"]!.GetValue<string>().ShouldBe("synthetic:password");
        foreach (string file in new[] { paths.SettingsFile, paths.SettingsFile + ".bak" })
        {
            string text = await File.ReadAllTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);
            text.ShouldNotContain("synthetic"); JsonNode.Parse(text)!["general"]!["unknownUntouched"]!.GetValue<int>().ShouldBe(42);
            JsonNode.Parse(text)!["network"]!["proxyCredentialSetId"]!.GetValue<string>().ShouldBe(id);
        }
        secrets.Values[SecretKeys.LegacyProxyCredentials].ShouldBe("unrelated-unknown-fixed-value");
        (await File.ReadAllTextAsync(paths.SettingsFile + ".old", cancellationToken: TestContext.Current.CancellationToken)).ShouldBe("preexisting-plaintext-evidence");
        await service.UpdateAsync(s => s with { General = s.General with { Theme = AppTheme.Dark } }, cancellationToken: TestContext.Current.CancellationToken);
        foreach (string file in new[] { paths.SettingsFile, paths.SettingsFile + ".bak" }) (await File.ReadAllTextAsync(file, cancellationToken: TestContext.Current.CancellationToken)).ShouldNotContain("synthetic");
    }

    [Theory][InlineData("99")][InlineData("2147483648")][InlineData("99999999999999999999999999999999999999999999")]
    public async Task FutureSchema_IsReadOnlyBeforeUnknownFieldSalvageAndBeforeMutateOrStage(string version)
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        string original = Document(version: version).Replace("\"uiLanguage\":\"en-US\"", "\"uiLanguage\":{\"future\":true}", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, original, cancellationToken: TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets); await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        service.IsReadOnly.ShouldBeTrue(); bool mutated = false;
        var ex = await Should.ThrowAsync<IrisException>(() => service.UpdateAsync(s => { mutated = true; return s; }, cancellationToken: TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.StorageFailure); mutated.ShouldBeFalse();
        using var sp = Core(service, secrets);
        var coordinator = sp.GetRequiredService<ISettingsEditCoordinator>();
        var result = await coordinator.CommitAsync(new(new(LogLevel: new("Information", "Warning")), []), cancellationToken: TestContext.Current.CancellationToken);
        result.Error!.Code.ShouldBe(ErrorCode.StorageFailure);
        await Should.ThrowAsync<IrisException>(() => sp.GetRequiredService<INetworkProxySnapshotSource>().CaptureCurrentAsync(new("http://localhost/"), cancellationToken: TestContext.Current.CancellationToken));
        secrets.Writes.ShouldBe(0); secrets.Reads.ShouldBe(0);
        (await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(original); Directory.GetFiles(dir.Path).Length.ShouldBe(1);
    }

    [Theory][InlineData("\"unknown\"")][InlineData("1.5")][InlineData("1,\"schemaVersion\":999")]
    public async Task UntrustworthySchema_PreservesOriginalCorruptFileWithoutGuessingCredentials(string version)
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        string original = Document(version: version); await File.WriteAllTextAsync(path, original, cancellationToken: TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets);
        var ex = await Should.ThrowAsync<IrisException>(() => service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken)); ex.Code.ShouldBe(ErrorCode.SettingsCorrupted);
        (await File.ReadAllTextAsync(ex.Detail!, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(original); secrets.Reads.ShouldBe(0); secrets.Writes.ShouldBe(0);
        service.Current.Network.ProxyMode.ShouldBe(ProxyMode.System);
    }

    [Fact]
    public async Task BackupFailure_RollsBackStageAndDoesNotPublishCurrent()
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        var original = Document(); await File.WriteAllTextAsync(path, original, cancellationToken: TestContext.Current.CancellationToken); Directory.CreateDirectory(path + ".bak");
        using var service = Service(dir, secrets);
        var ex = await Should.ThrowAsync<IrisException>(() => service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken)); ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.ToString().ShouldNotContain("synthetic"); secrets.Values.ShouldBeEmpty();
        (await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(original); service.Current.Network.ProxyCredentialSetId.ShouldBeNull();
        Directory.Delete(path + ".bak"); await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken); service.Current.Network.ProxyCredentialSetId.ShouldNotBeNull();
    }

    [Fact]
    public async Task RealCurrentReplaceFailure_AfterSafeBackupRetainsReferencedSecretAndAllowsLoadRetry()
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        var original = Document(); await File.WriteAllTextAsync(path, original, cancellationToken: TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            (await Should.ThrowAsync<IrisException>(() => service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken))).Code.ShouldBe(ErrorCode.StorageFailure);
            string backup = await File.ReadAllTextAsync(path + ".bak", cancellationToken: TestContext.Current.CancellationToken); backup.ShouldNotContain("synthetic");
            var id = JsonNode.Parse(backup)!["network"]!["proxyCredentialSetId"]!.GetValue<string>();
            secrets.Values.ShouldContainKey(SecretKeys.ProxyCredentialSet(id));
            service.Current.Network.ProxyCredentialSetId.ShouldBeNull(); (await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(original);
        }
        await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken); service.Current.Network.ProxyCredentialSetId.ShouldNotBeNull();
    }

    [Fact]
    public async Task KnownFixedImport_ConditionalCleanupAndExplicitReferencePriority()
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        secrets.Values[SecretKeys.LegacyProxyCredentials] = Envelope(); await File.WriteAllTextAsync(path, Document(CleanUrl), cancellationToken: TestContext.Current.CancellationToken);
        using (var service = Service(dir, secrets))
        {
            // An external change during stage must not be removed by later conditional legacy cleanup.
            secrets.OnSet = () => secrets.Values[SecretKeys.LegacyProxyCredentials] = "changed-fixed-value";
            await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken); secrets.Values[SecretKeys.LegacyProxyCredentials].ShouldBe("changed-fixed-value");
        }
        secrets.OnSet = null; secrets.OnGet = key => { if (key == SecretKeys.LegacyProxyCredentials) throw new InvalidOperationException("must not read legacy"); };
        using (var reload = Service(dir, secrets)) await reload.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        secrets.OnGet = null;
        await File.WriteAllTextAsync(path, Document(CleanUrl), cancellationToken: TestContext.Current.CancellationToken); secrets.Values[SecretKeys.LegacyProxyCredentials] = Envelope();
        using (var import = Service(dir, secrets)) await import.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        secrets.Values.ShouldNotContainKey(SecretKeys.LegacyProxyCredentials);
    }

    [Theory][InlineData("unknown-format")][InlineData("mismatch")][InlineData("explicit-userinfo")]
    public async Task AmbiguousLegacy_FailsClosedAndPreservesOriginal(string kind)
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        string original = kind == "explicit-userinfo" ? Document(reference: Id) : Document(CleanUrl);
        secrets.Values[SecretKeys.LegacyProxyCredentials] = kind == "mismatch" ? Envelope("http://other.example.test/") : "unknown-private-value";
        await File.WriteAllTextAsync(path, original, cancellationToken: TestContext.Current.CancellationToken); using var service = Service(dir, secrets);
        (await Should.ThrowAsync<IrisException>(() => service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken))).Code.ShouldBe(ErrorCode.StorageFailure);
        (await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(original); secrets.Writes.ShouldBe(0); File.Exists(path + ".bak").ShouldBeFalse();
    }

    [Fact]
    public async Task ExternalEdits_AreDetectedBeforeMutateAndAgainBeforeSafeBackup()
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        using var service = Service(dir, secrets); await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        string safe = await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken); await File.WriteAllTextAsync(path, Document(), cancellationToken: TestContext.Current.CancellationToken);
        bool mutated = false;
        await Should.ThrowAsync<IrisException>(() => service.UpdateAsync(s => { mutated = true; return s; }, cancellationToken: TestContext.Current.CancellationToken)); mutated.ShouldBeFalse();
        File.Exists(path + ".bak").ShouldBeFalse();
        await File.WriteAllTextAsync(path, safe, cancellationToken: TestContext.Current.CancellationToken);
        await Should.ThrowAsync<IrisException>(() => service.UpdateAsync(s =>
        {
            File.WriteAllText(path, Document());
            return s with { History = s.History with { Enabled = false } };
        }, cancellationToken: TestContext.Current.CancellationToken));
        File.Exists(path + ".bak").ShouldBeFalse(); (await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldContain("synthetic%3Apassword");
    }

    [Fact]
    public async Task MigrationVersionBackup_UsesSanitizedLogicalInput()
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        await File.WriteAllTextAsync(path, Document(version: "0"), cancellationToken: TestContext.Current.CancellationToken);
        using var service = Service(dir, secrets, [new Migration()]); await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (string file in Directory.GetFiles(dir.Path))
        {
            string text = await File.ReadAllTextAsync(file, cancellationToken: TestContext.Current.CancellationToken); text.ShouldNotContain("synthetic");
            JsonNode.Parse(text)!["network"]!["proxyCredentialSetId"]!.GetValue<string>().ShouldBe(service.Current.Network.ProxyCredentialSetId);
        }
        File.Exists(Path.Combine(dir.Path, "settings.v0.bak.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task ActualCoreAndJson_OneMixedPublicationClearAndReceiptRetryWithNoRewrite()
    {
        using var dir = new TempDirectory(); var secrets = new Secrets(); using var settings = Service(dir, secrets); await settings.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var observer = new Observer(); using var sp = Core(settings, secrets, observer);
        var coordinator = sp.GetRequiredService<ISettingsEditCoordinator>();
        sp.GetRequiredService<INetworkProxySnapshotSource>().ShouldBeSameAs(coordinator);
        var ocr = settings.Current.Ocr.Providers[0];
        var translation = new TranslationProviderProfile { Id = "tp-0000000000aa", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "synthetic", BaseUrl = "https://origin.example.test/v1", Model = "synthetic" };
        int changes = 0; settings.Changed += (_, _) => changes++;
        observer.Fail = true;
        var plan = new SettingsEditPlan(new(LogLevel: new("Information", "Warning")),
            [new(TranslationProfileChangeKind.Add, null, translation, new(CredentialEditKind.Replace, new("synthetic-provider", null)))], new([], [translation.Id]))
        {
            OcrChanges = [new(OcrProfileChangeKind.Update, ocr, ocr with { DisplayName = "synthetic" }, new(CredentialEditKind.Clear))],
            History = new(MaxEntries: new(settings.Current.History.MaxEntries, 7654)),
            Network = new(new(ProxyMode.System, ProxyMode.Custom), new(null, CleanUrl), null, new(CredentialEditKind.Replace, null, null, new("synthetic-proxy", "synthetic-password")))
        };
        var result = await coordinator.CommitAsync(plan, cancellationToken: TestContext.Current.CancellationToken); result.Status.ShouldBe(SettingsEditStatus.ApplyPending, result.Error?.ToString()); changes.ShouldBe(1);
        string path = new FakeAppPaths(dir.Path).SettingsFile; var bytes = await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken); int writes = secrets.Writes;
        observer.Fail = false; (await coordinator.RetryApplyAsync(result.PendingApply!, cancellationToken: TestContext.Current.CancellationToken)).IsComplete.ShouldBeTrue();
        changes.ShouldBe(1); secrets.Writes.ShouldBe(writes); (await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(bytes);
        (await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldNotContain("synthetic-password"); settings.Current.History.MaxEntries.ShouldBe(7654);
        var baseline = settings.Current.Network;
        secrets.Values[SecretKeys.LegacyProxyCredentials] = Envelope();
        (await coordinator.CommitAsync(new(new(), []) { Network = new(Credentials: new(CredentialEditKind.Clear, baseline.CustomProxyUrl, baseline.ProxyCredentialSetId)) }, cancellationToken: TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        (await sp.GetRequiredService<INetworkProxySnapshotSource>().CaptureCurrentAsync(new("https://origin.example.test/"), cancellationToken: TestContext.Current.CancellationToken)).Credentials.ShouldBeNull();
    }

    private static ServiceProvider Core(ISettingsService settings, ISecretStore secrets, Observer? observer = null)
    {
        var services = new ServiceCollection(); services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance); services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)); services.AddSingleton(settings); services.AddSingleton(secrets);
        services.AddIrisCore(); services.AddSingleton(Substitute.For<ITranslationProviderRegistry>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
        services.AddSingleton<INetworkConfigurationObserver>(observer ?? new());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ActualCoreWithRealFiles_StageWriteCancelApplyAndRetireFailuresKeepCommitFacts()
    {
        using var dir = new TempDirectory(); var path = new FakeAppPaths(dir.Path).SettingsFile; var secrets = new Secrets();
        using var settings = Service(dir, secrets); await settings.LoadAsync(TestContext.Current.CancellationToken);
        var observer = new Observer(); using var services = Core(settings, secrets, observer);
        var coordinator = services.GetRequiredService<ISettingsEditCoordinator>();
        var plan = new SettingsEditPlan(new(), [])
        {
            Network = new(new(ProxyMode.System, ProxyMode.Custom), new(null, CleanUrl), null,
                new(CredentialEditKind.Replace, null, null, new("synthetic", "private-password")))
        };
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        secrets.OnSet = () => throw new IOException("synthetic-stage");
        (await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Uncommitted);
        secrets.OnSet = null; secrets.Values.ShouldBeEmpty();
        using (var locked = new FileStream(path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            (await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Uncommitted);
        secrets.Values.ShouldBeEmpty(); (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
        using (var cancel = new CancellationTokenSource())
        {
            secrets.OnSet = cancel.Cancel;
            await Should.ThrowAsync<OperationCanceledException>(() => coordinator.CommitAsync(plan, cancel.Token));
            secrets.OnSet = null; secrets.Values.ShouldBeEmpty();
        }
        observer.Fail = true;
        using var lateCancel = new CancellationTokenSource();
        settings.Changed += (_, _) => lateCancel.Cancel();
        var pending = await coordinator.CommitAsync(plan, lateCancel.Token);
        pending.Status.ShouldBe(SettingsEditStatus.ApplyPending); lateCancel.IsCancellationRequested.ShouldBeTrue();
        string firstId = settings.Current.Network.ProxyCredentialSetId!;
        observer.Fail = false; (await coordinator.RetryApplyAsync(pending.PendingApply!, TestContext.Current.CancellationToken)).IsComplete.ShouldBeTrue();
        var baseline = settings.Current.Network;
        secrets.OnRemove = _ => throw new IOException("synthetic-retire");
        var second = await coordinator.CommitAsync(new(new(), []) { Network = new(Credentials: new(CredentialEditKind.Clear, baseline.CustomProxyUrl, firstId)) }, TestContext.Current.CancellationToken);
        second.Status.ShouldBe(SettingsEditStatus.ApplyPending); secrets.Values.ShouldContainKey(SecretKeys.ProxyCredentialSet(firstId));
        var committed = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken); int writes = secrets.Writes;
        secrets.OnRemove = null;
        (await coordinator.RetryApplyAsync(second.PendingApply!, TestContext.Current.CancellationToken)).IsComplete.ShouldBeTrue();
        secrets.Values.ShouldNotContainKey(SecretKeys.ProxyCredentialSet(firstId)); secrets.Writes.ShouldBe(writes);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(committed);
    }
    private sealed class Observer : INetworkConfigurationObserver
    {
        internal bool Fail;
        public Task ApplyAsync(NetworkSettings settings, Guid generationId, CancellationToken cancellationToken = default) => Fail ? Task.FromException(new IOException("private")) : Task.CompletedTask;
    }
    private sealed class Migration : ISettingsMigration
    {
        public int FromVersion => 0;
        public JsonObject Migrate(JsonObject oldRoot) { oldRoot["schemaVersion"] = 1; return oldRoot; }
    }
    private sealed class Secrets : ISecretStore
    {
        internal ConcurrentDictionary<string, string> Values { get; } = new();
        internal int Writes, Reads;
        internal Action? OnSet;
        internal Action<string>? OnGet;
        internal Action<string>? OnRemove;
        public event EventHandler<string>? Changed;
        public string? Get(string key) { Reads++; OnGet?.Invoke(key); return Values.GetValueOrDefault(key); }
        public void Set(string key, string value) { Writes++; OnSet?.Invoke(); Values[key] = value; Changed?.Invoke(this, key); }
        public bool Remove(string key) { OnRemove?.Invoke(key); bool removed = Values.TryRemove(key, out _); if (removed) Changed?.Invoke(this, key); return removed; }
    }
}
