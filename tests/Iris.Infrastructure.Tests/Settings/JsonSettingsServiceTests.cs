using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Infrastructure.Settings;
using Iris.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class JsonSettingsServiceTests
{
    private static JsonSettingsService CreateService(
        TempDirectory dir,
        IEnumerable<ISettingsMigration>? migrations = null,
        TimeProvider? timeProvider = null)
    {
        var paths = new FakeAppPaths(dir.Path);
        var migrationList = migrations ?? Array.Empty<ISettingsMigration>();
        var time = timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        return new JsonSettingsService(paths, migrationList, time, NullLogger<JsonSettingsService>.Instance);
    }

    [Fact]
    public async Task L1_FileDoesNotExist_WritesDefaults_Deserializable_NoException()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        var paths = new FakeAppPaths(dir.Path);

        File.Exists(paths.SettingsFile).ShouldBeFalse();

        await service.LoadAsync(TestContext.Current.CancellationToken);

        File.Exists(paths.SettingsFile).ShouldBeTrue();
        service.Current.ShouldNotBeNull();
        service.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        SettingsTestInvariants.AssertInvariants(service.Current);

        var fileText = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(fileText, IrisJson.Options);
        deserialized.ShouldNotBeNull();
    }

    [Fact]
    public async Task L2_ValidFile_MatchesCurrent_FileBytesUnchanged()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var initialSettings = new AppSettings
        {
            General = new GeneralSettings { UiLanguage = "en-US" },
            History = new HistorySettings { MaxEntries = 2000 },
        };
        var initialJson = JsonSerializer.Serialize(initialSettings, IrisJson.Options) + "\n";
        await File.WriteAllTextAsync(paths.SettingsFile, initialJson, TestContext.Current.CancellationToken);
        var initialBytes = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.General.UiLanguage.ShouldBe("en-US");
        service.Current.History.MaxEntries.ShouldBe(2000);
        SettingsTestInvariants.AssertInvariants(service.Current);

        var currentBytes = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        currentBytes.ShouldBe(initialBytes);
    }

    [Fact]
    public async Task L3_CorruptedJson_ThrowsSettingsCorrupted_BacksUp_WritesDefaults_BakUnchanged()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string corruptContent = "{ invalid json syntax !!";
        const string oldBakContent = "existing bak file content";

        await File.WriteAllTextAsync(paths.SettingsFile, corruptContent, TestContext.Current.CancellationToken);
        var bakFile = paths.SettingsFile + ".bak";
        await File.WriteAllTextAsync(bakFile, oldBakContent, TestContext.Current.CancellationToken);

        var fixedTime = new DateTimeOffset(2026, 9, 25, 12, 34, 56, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        using var service = CreateService(dir, timeProvider: timeProvider);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
            await service.LoadAsync(TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.SettingsCorrupted);
        ex.Detail.ShouldNotBeNull();

        // Backup file exists and equals corrupt content
        File.Exists(ex.Detail).ShouldBeTrue();
        (await File.ReadAllTextAsync(ex.Detail, TestContext.Current.CancellationToken)).ShouldBe(corruptContent);

        // settings.json is reset to defaults
        File.Exists(paths.SettingsFile).ShouldBeTrue();
        service.Current.ShouldNotBeNull();
        service.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        SettingsTestInvariants.AssertInvariants(service.Current);

        // settings.json.bak remains untouched
        (await File.ReadAllTextAsync(bakFile, TestContext.Current.CancellationToken)).ShouldBe(oldBakContent);
    }

    [Fact]
    public async Task L4_RootIsArrayOrEmpty_TreatedAsCorrupted()
    {
        using var dir1 = new TempDirectory();
        var paths1 = new FakeAppPaths(dir1.Path);
        await File.WriteAllTextAsync(paths1.SettingsFile, "[1, 2, 3]", TestContext.Current.CancellationToken);
        using var service1 = CreateService(dir1);

        var ex1 = await Should.ThrowAsync<IrisException>(async () =>
            await service1.LoadAsync(TestContext.Current.CancellationToken));
        ex1.Code.ShouldBe(ErrorCode.SettingsCorrupted);
        SettingsTestInvariants.AssertInvariants(service1.Current);

        using var dir2 = new TempDirectory();
        var paths2 = new FakeAppPaths(dir2.Path);
        await File.WriteAllTextAsync(paths2.SettingsFile, "   ", TestContext.Current.CancellationToken);
        using var service2 = CreateService(dir2);

        var ex2 = await Should.ThrowAsync<IrisException>(async () =>
            await service2.LoadAsync(TestContext.Current.CancellationToken));
        ex2.Code.ShouldBe(ErrorCode.SettingsCorrupted);
        SettingsTestInvariants.AssertInvariants(service2.Current);
    }

    [Fact]
    public async Task L5_CorruptedBackupCollision_AppendsCounterSuffix()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var fixedTime = new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        // Pre-create the first backup file
        var timestamp = fixedTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var existingBackup = Path.Combine(dir.Path, $"settings.corrupt-{timestamp}.json");
        await File.WriteAllTextAsync(existingBackup, "first collision", TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(paths.SettingsFile, "corrupted content", TestContext.Current.CancellationToken);

        using var service = CreateService(dir, timeProvider: timeProvider);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
            await service.LoadAsync(TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.SettingsCorrupted);
        ex.Detail.ShouldBe(Path.Combine(dir.Path, $"settings.corrupt-{timestamp}-1.json"));
        File.Exists(ex.Detail).ShouldBeTrue();
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L6_BadProviderAndInvalidTheme_Salvaged_FileRewritten()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var json = """
        {
          "schemaVersion": 1,
          "general": {
            "theme": "InvalidThemeName"
          },
          "translation": {
            "providers": [
              {
                "id": "p1",
                "kind": "OpenAICompatible",
                "displayName": "Valid Provider",
                "model": "gpt-4o"
              },
              {
                "id": "p2_bad_missing_model",
                "kind": "OpenAICompatible",
                "displayName": "Bad Provider"
              }
            ]
          }
        }
        """;
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        // Bad provider removed, p1 kept
        service.Current.Translation.Providers.Count.ShouldBe(1);
        service.Current.Translation.Providers[0].Id.ShouldBe("p1");

        // general theme reset to default (System)
        service.Current.General.Theme.ShouldBe(AppTheme.System);
        SettingsTestInvariants.AssertInvariants(service.Current);

        // File is rewritten
        var rewrittenText = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        rewrittenText.ShouldNotContain("p2_bad_missing_model");
        rewrittenText.ShouldNotContain("InvalidThemeName");
    }

    [Fact]
    public async Task L7_SchemaVersion0_WithMigration_AppliesMigrationAndBacksUp()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var json = """
        {
          "schemaVersion": 0,
          "history": {
            "maxEntries": 1000
          }
        }
        """;
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        var migration0To1 = new TestMigration(0, root =>
        {
            root["history"]!["maxEntries"] = 2500;
            return root;
        });

        using var service = CreateService(dir, migrations: [migration0To1]);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.SchemaVersion.ShouldBe(1);
        service.Current.History.MaxEntries.ShouldBe(2500);
        SettingsTestInvariants.AssertInvariants(service.Current);

        var bakPath = Path.Combine(dir.Path, "settings.v0.bak.json");
        File.Exists(bakPath).ShouldBeTrue();

        var rewritten = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        rewritten.ShouldContain("\"maxEntries\": 2500");
    }

    [Fact]
    public async Task L8_SchemaVersion99_LoadsSuccessfully_OriginalBytesUnchanged()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var json = """
        {
          "schemaVersion": 99,
          "history": {
            "maxEntries": 3000
          },
          "futureField": "futureValue"
        }
        """;
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);
        var originalBytes = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.History.MaxEntries.ShouldBe(3000);
        SettingsTestInvariants.AssertInvariants(service.Current);

        var v99File = Path.Combine(dir.Path, "settings.v99.json");
        File.Exists(v99File).ShouldBeTrue();

        var currentBytes = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        currentBytes.ShouldBe(originalBytes);
    }

    [Fact]
    public async Task L9_SchemaVersionString_LoadsAndCorrects()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var json = """
        {
          "schemaVersion": "abc",
          "history": {
            "maxEntries": 4000
          }
        }
        """;
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        service.Current.History.MaxEntries.ShouldBe(4000);
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L10_FileWithBomCommentsAndTrailingCommas_LoadsSuccessfully()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var jsonWithComments = """
        // Header comment
        {
          /* inline comment */
          "schemaVersion": 1,
          "history": {
            "maxEntries": 1200,
          },
        }
        """;
        var bytesWithBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(jsonWithComments)).ToArray();
        await File.WriteAllBytesAsync(paths.SettingsFile, bytesWithBom, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.History.MaxEntries.ShouldBe(1200);
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L11_CallingLoadAsyncTwice_ThrowsInvalidOperationException()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);

        await service.LoadAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task L12_WhenFileLockedWithFileShareNone_ThrowsStorageFailure_RetryingAfterUnlockSucceeds()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        await File.WriteAllTextAsync(paths.SettingsFile, "{}", TestContext.Current.CancellationToken);

        using var service = CreateService(dir);

        // Lock file with FileShare.None
        using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await service.LoadAsync(TestContext.Current.CancellationToken));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
        }

        // After releasing the lock, calling LoadAsync again succeeds
        await service.LoadAsync(TestContext.Current.CancellationToken);
        service.Current.ShouldNotBeNull();
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L13_RootExactDuplicateKeys_TreatedAsCorrupted_BacksUp_SubsequentUpdateSucceeds()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """{"general":{"theme":"Dark"},"general":{"theme":"Light"}}""";
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        var ex = await Should.ThrowAsync<IrisException>(async () =>
            await service.LoadAsync(TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.SettingsCorrupted);

        File.Exists(ex.Detail).ShouldBeTrue();
        (await File.ReadAllTextAsync(ex.Detail!, TestContext.Current.CancellationToken)).ShouldBe(json);

        // Default settings loaded and invariants hold
        SettingsTestInvariants.AssertInvariants(service.Current);

        // Subsequent UpdateAsync succeeds
        await service.UpdateAsync(s => s with
        {
            History = s.History with { MaxEntries = 9999 },
        }, TestContext.Current.CancellationToken);

        service.Current.History.MaxEntries.ShouldBe(9999);
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L14_RootCaseInsensitiveDuplicateKeys_TreatedAsCorrupted()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """{"general":{"theme":"Dark"},"General":{"theme":"Light"}}""";
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        var ex = await Should.ThrowAsync<IrisException>(async () =>
            await service.LoadAsync(TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.SettingsCorrupted);

        File.Exists(ex.Detail).ShouldBeTrue();
        (await File.ReadAllTextAsync(ex.Detail!, TestContext.Current.CancellationToken)).ShouldBe(json);
        SettingsTestInvariants.AssertInvariants(service.Current);

        // Subsequent UpdateAsync succeeds
        await service.UpdateAsync(s => s with
        {
            History = s.History with { MaxEntries = 8888 },
        }, TestContext.Current.CancellationToken);

        service.Current.History.MaxEntries.ShouldBe(8888);
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L15_NestedProviderDuplicateKeys_TreatedAsCorrupted()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """{"ocr":{"providers":[{"id":"ocr-1","id":"ocr-2","kind":"WindowsLocal","displayName":"w"}]}}""";
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        var ex = await Should.ThrowAsync<IrisException>(async () =>
            await service.LoadAsync(TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.SettingsCorrupted);

        File.Exists(ex.Detail).ShouldBeTrue();
        (await File.ReadAllTextAsync(ex.Detail!, TestContext.Current.CancellationToken)).ShouldBe(json);
        SettingsTestInvariants.AssertInvariants(service.Current);

        // Subsequent UpdateAsync succeeds
        await service.UpdateAsync(s => s with
        {
            History = s.History with { MaxEntries = 7777 },
        }, TestContext.Current.CancellationToken);

        service.Current.History.MaxEntries.ShouldBe(7777);
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L16_SchemaVersion0_WithoutMigrations_SkipsMigrationAndLoadsCurrent()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """{"schemaVersion":0,"history":{"maxEntries":1234}}""";
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir, migrations: []);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        service.Current.History.MaxEntries.ShouldBe(1234);
        SettingsTestInvariants.AssertInvariants(service.Current);

        var rewritten = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        rewritten.ShouldContain("\"schemaVersion\": 1");
    }

    [Fact]
    public async Task L17_SchemaVersionNegative_WithoutMigrations_SkipsMigrationAndLoadsCurrent()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """{"schemaVersion":-3,"history":{"maxEntries":5678}}""";
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir, migrations: []);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        service.Current.History.MaxEntries.ShouldBe(5678);
        SettingsTestInvariants.AssertInvariants(service.Current);

        var rewritten = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        rewritten.ShouldContain("\"schemaVersion\": 1");
    }

    [Fact]
    public async Task L18_CorruptedFileLockedWithoutDelete_ThrowsStorageFailure_RetryingAfterUnlockSucceeds()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string corruptContent = "{ bad json syntax !!";
        await File.WriteAllTextAsync(paths.SettingsFile, corruptContent, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);

        // Lock file with FileShare.ReadWrite (which does not allow Delete/Move)
        using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await service.LoadAsync(TestContext.Current.CancellationToken));
            ex.Code.ShouldBe(ErrorCode.StorageFailure);

            // Service is unloaded; UpdateAsync throws InvalidOperationException
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await service.UpdateAsync(s => s, TestContext.Current.CancellationToken));
        }

        // Releasing handle and calling LoadAsync again throws SettingsCorrupted and completes backup
        var corruptEx = await Should.ThrowAsync<IrisException>(async () =>
            await service.LoadAsync(TestContext.Current.CancellationToken));
        corruptEx.Code.ShouldBe(ErrorCode.SettingsCorrupted);
        File.Exists(corruptEx.Detail).ShouldBeTrue();

        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task L19_MigrationBackupLocked_ThrowsStorageFailure_RetryingAfterUnlockSucceeds()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """{"schemaVersion":0,"history":{"maxEntries":1000}}""";
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        var migration0To1 = new TestMigration(0, root =>
        {
            root["history"]!["maxEntries"] = 2500;
            return root;
        });

        var bakPath = Path.Combine(dir.Path, "settings.v0.bak.json");
        await File.WriteAllTextAsync(bakPath, "old-backup", TestContext.Current.CancellationToken);

        using var service = CreateService(dir, migrations: [migration0To1]);

        // Lock bakPath with FileShare.None
        using (new FileStream(bakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await service.LoadAsync(TestContext.Current.CancellationToken));
            ex.Code.ShouldBe(ErrorCode.StorageFailure);

            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await service.UpdateAsync(s => s, TestContext.Current.CancellationToken));
        }

        // After unlocking, calling LoadAsync again succeeds with migration applied
        await service.LoadAsync(TestContext.Current.CancellationToken);
        service.Current.SchemaVersion.ShouldBe(1);
        service.Current.History.MaxEntries.ShouldBe(2500);

        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task U1_UpdateBeforeLoad_ThrowsInvalidOperationException()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.UpdateAsync(s => s, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task U2_NormalUpdate_UpdatesFile_CreatesBak_FiresChangedOnce()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var initialText = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);

        SettingsChangedEventArgs? eventArgs = null;
        var eventCount = 0;
        service.Changed += (_, e) =>
        {
            eventArgs = e;
            eventCount++;
        };

        await service.UpdateAsync(s => s with
        {
            General = s.General with { Theme = AppTheme.Dark },
        }, TestContext.Current.CancellationToken);

        eventCount.ShouldBe(1);
        eventArgs.ShouldNotBeNull();
        eventArgs.OldSettings.General.Theme.ShouldBe(AppTheme.System);
        eventArgs.NewSettings.General.Theme.ShouldBe(AppTheme.Dark);

        service.Current.General.Theme.ShouldBe(AppTheme.Dark);

        var currentText = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        currentText.ShouldContain("\"theme\": \"Dark\"");

        var bakText = await File.ReadAllTextAsync(paths.SettingsFile + ".bak", TestContext.Current.CancellationToken);
        bakText.ShouldBe(initialText);
    }

    [Fact]
    public async Task U3_MutateReturnsSameReference_NoWrite_NoEvent()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var initialBytes = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        var eventFired = false;
        service.Changed += (_, _) => eventFired = true;

        await service.UpdateAsync(s => s, TestContext.Current.CancellationToken);

        eventFired.ShouldBeFalse();
        (await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken)).ShouldBe(initialBytes);
    }

    [Fact]
    public async Task U4_MutateReturnsCloneWithNoChanges_NoWrite_NoEvent()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var initialBytes = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        var eventFired = false;
        service.Changed += (_, _) => eventFired = true;

        await service.UpdateAsync(s => s with { }, TestContext.Current.CancellationToken);

        eventFired.ShouldBeFalse();
        (await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken)).ShouldBe(initialBytes);
    }

    [Fact]
    public async Task U5_MaxConcurrentProvidersZero_PersistsAsOne()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        await service.UpdateAsync(s => s with
        {
            Translation = s.Translation with { MaxConcurrentProviders = 0 },
        }, TestContext.Current.CancellationToken);

        service.Current.Translation.MaxConcurrentProviders.ShouldBe(1);

        var fileText = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        fileText.ShouldContain("\"maxConcurrentProviders\": 1");
    }

    [Fact]
    public async Task U6_WhenTmpLocked_ThrowsStorageFailure_CurrentUnchanged_SubsequentUpdateSucceeds()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var originalTheme = service.Current.General.Theme;
        var tmpPath = paths.SettingsFile + ".tmp";
        var eventFired = false;
        service.Changed += (_, _) => eventFired = true;

        using (new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await service.UpdateAsync(s => s with
                {
                    General = s.General with { Theme = AppTheme.Dark },
                }, TestContext.Current.CancellationToken));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
        }

        service.Current.General.Theme.ShouldBe(originalTheme);
        eventFired.ShouldBeFalse();

        // Subsequent update after releasing lock
        await service.UpdateAsync(s => s with
        {
            General = s.General with { Theme = AppTheme.Dark },
        }, TestContext.Current.CancellationToken);

        service.Current.General.Theme.ShouldBe(AppTheme.Dark);
    }

    [Fact]
    public async Task U7_MutateThrows_ExceptionPropagates_LockReleased()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.UpdateAsync(_ => throw new InvalidOperationException("Mutate error"), TestContext.Current.CancellationToken));

        // Subsequent update succeeds (verifying lock was released)
        await service.UpdateAsync(s => s with
        {
            History = s.History with { MaxEntries = 999 },
        }, TestContext.Current.CancellationToken);

        service.Current.History.MaxEntries.ShouldBe(999);
    }

    [Fact]
    public async Task U8_MutateReturnsNull_ThrowsInvalidOperationException()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.UpdateAsync(_ => null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task U9_TokenPreCancelled_ThrowsOperationCanceledException_NoChange()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var initialBytes = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await service.UpdateAsync(s => s with { History = s.History with { MaxEntries = 9999 } }, cts.Token));

        (await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken)).ShouldBe(initialBytes);
    }

    [Fact]
    public async Task U10_ConcurrentUpdates_HistoryIncremented50Times_SequentialEvents()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        // Initial setup: MaxEntries = 1000
        await service.UpdateAsync(s => s with
        {
            History = s.History with { MaxEntries = 1000 },
        }, TestContext.Current.CancellationToken);

        var eventSequence = new List<(int Old, int New)>();
        var eventLock = new object();
        service.Changed += (_, e) =>
        {
            lock (eventLock)
            {
                eventSequence.Add((e.OldSettings.History.MaxEntries, e.NewSettings.History.MaxEntries));
            }
        };

        const int concurrency = 50;
        var tasks = new Task[concurrency];

        for (var i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await service.UpdateAsync(s => s with
                {
                    History = s.History with { MaxEntries = s.History.MaxEntries + 1 },
                }, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);

        service.Current.History.MaxEntries.ShouldBe(1050);

        var fileText = await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        fileText.ShouldContain("\"maxEntries\": 1050");

        eventSequence.Count.ShouldBe(concurrency);
        for (var i = 0; i < concurrency; i++)
        {
            eventSequence[i].Old.ShouldBe(1000 + i);
            eventSequence[i].New.ShouldBe(1001 + i);
        }
    }

    [Fact]
    public async Task U11_FirstHandlerThrows_SecondHandlerStillInvoked_UpdateSucceeds()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var secondHandlerInvoked = false;

        service.Changed += (_, _) => throw new InvalidOperationException("First handler failure");
        service.Changed += (_, _) => secondHandlerInvoked = true;

        await service.UpdateAsync(s => s with
        {
            History = s.History with { MaxEntries = 3333 },
        }, TestContext.Current.CancellationToken);

        secondHandlerInvoked.ShouldBeTrue();
        service.Current.History.MaxEntries.ShouldBe(3333);
    }

    [Fact]
    public async Task U12_HandlerLaunchesUnawaitedUpdate_DoesNotDeadlock_BothAppliedWithin5Seconds()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var secondUpdateDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var triggerSecondUpdate = true;
        service.Changed += (_, e) =>
        {
            if (e.NewSettings.History.MaxEntries == 2000 && triggerSecondUpdate)
            {
                triggerSecondUpdate = false;
                // Directly invoke UpdateAsync from within the handler without Task.Run
                _ = service.UpdateAsync(s => s with
                {
                    History = s.History with { MaxEntries = 3000 },
                }, TestContext.Current.CancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        secondUpdateDone.TrySetException(t.Exception.InnerExceptions);
                    }
                    else if (t.IsCanceled)
                    {
                        secondUpdateDone.TrySetCanceled();
                    }
                    else
                    {
                        secondUpdateDone.TrySetResult(true);
                    }
                }, TaskScheduler.Default);
            }
        };

        // First update
        var firstUpdateTask = service.UpdateAsync(s => s with
        {
            History = s.History with { MaxEntries = 2000 },
        }, TestContext.Current.CancellationToken);

        // Wait both with 5 seconds timeout
        await Task.WhenAll(firstUpdateTask, secondUpdateDone.Task)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        service.Current.History.MaxEntries.ShouldBe(3000);
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public void Current_BeforeLoad_ReturnsDefaults()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);

        service.Current.ShouldNotBeNull();
        service.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        service.Current.General.Theme.ShouldBe(AppTheme.System);
    }

    [Fact]
    public async Task R1_1_HotkeyModifiersWithUndefinedBits_RetainsCustomKeyAndMasksModifiers()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """
        {
          "schemaVersion": 1,
          "hotkeys": {
            "InputTranslate": {
              "modifiers": 22,
              "key": "F"
            }
          }
        }
        """;
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        // 22 (0x16) masked with 0x0F (Alt, Control, Shift, Win) -> 6 (0x06 = Control | Shift)
        service.Current.Hotkeys.ContainsKey(IrisAction.InputTranslate).ShouldBeTrue();
        var binding = service.Current.Hotkeys[IrisAction.InputTranslate];
        binding.Key.ShouldBe("F");
        binding.Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Shift);
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task R1_2_ExtraHeadersWithNullValue_RemovesNullEntry()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string json = """
        {
          "schemaVersion": 1,
          "translation": {
            "providers": [
              {
                "id": "tp-custom",
                "kind": "OpenAICompatible",
                "displayName": "Custom OpenAI",
                "model": "gpt-4o",
                "openAIOptions": {
                  "extraHeaders": {
                    "X-A": null,
                    "X-B": "b"
                  }
                }
              }
            ]
          }
        }
        """;
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var p = service.Current.Translation.Providers.First(p => p.Id == "tp-custom");
        p.OpenAIOptions.ShouldNotBeNull();
        p.OpenAIOptions.ExtraHeaders.ShouldNotBeNull();
        p.OpenAIOptions.ExtraHeaders.ContainsKey("X-A").ShouldBeFalse();
        p.OpenAIOptions.ExtraHeaders["X-B"].ShouldBe("b");
        SettingsTestInvariants.AssertInvariants(service.Current);
    }

    [Fact]
    public async Task T010B_Baseline_InputAliasLeak_WhenCallerMutatesAfterUpdate_FailsOnBaseline()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var headers = new Dictionary<string, string> { ["X-Header"] = "initial" };
        var profile = new TranslationProviderProfile
        {
            Id = "tp-probe",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Probe",
            Model = "gpt-4o",
            BaseUrl = "https://example.com/v1",
            OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers }
        };
        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>(service.Current.Hotkeys)
        {
            [IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K")
        };
        var excluded = new List<string> { "proc1.exe" };

        await service.UpdateAsync(s => s with
        {
            Hotkeys = hotkeys,
            Translation = s.Translation with { Providers = [profile] },
            Selection = s.Selection with { ExcludedProcesses = excluded }
        }, TestContext.Current.CancellationToken);

        // Caller mutates their collections after UpdateAsync completes
        headers["X-Header"] = "mutated";
        hotkeys[IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.None, "Z");
        excluded.Add("hacked.exe");

        // On deeply immutable snapshot, Current must be isolated from caller's mutations
        service.Current.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!["X-Header"].ShouldBe("initial");
        service.Current.Hotkeys[IrisAction.InputTranslate].Key.ShouldBe("K");
        service.Current.Selection.ExcludedProcesses.Count.ShouldBe(1);
    }

    [Fact]
    public async Task T010B_Baseline_InPlaceMutation_HotkeysAndHeaders_ThrowsNotSupported_FailsOnBaseline()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var headers = new Dictionary<string, string> { ["X-Header"] = "initial" };
        var profile = new TranslationProviderProfile
        {
            Id = "tp-probe",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Probe",
            Model = "gpt-4o",
            BaseUrl = "https://example.com/v1",
            OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers }
        };

        await service.UpdateAsync(s => s with
        {
            Translation = s.Translation with { Providers = [profile] }
        }, TestContext.Current.CancellationToken);

        // On deeply immutable snapshot, in-place mutations through collection interfaces must throw NotSupportedException
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)service.Current.Hotkeys).Clear());

        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<string, string>)service.Current.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!).Clear());

        Should.Throw<NotSupportedException>(() =>
            ((IList<SelectionReadMethod>)service.Current.Selection.ReadOrder).Clear());
    }

    [Fact]
    public async Task T010B_Lifecycle_AllFivePublishingLocations_ProduceDeeplyImmutableSnapshots()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        // Location 1: Before LoadAsync (Constructor default)
        using var service = CreateService(dir);
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)service.Current.Hotkeys).Clear());
        Should.Throw<NotSupportedException>(() =>
            ((IList<SelectionReadMethod>)service.Current.Selection.ReadOrder).Clear());

        // Location 2: First load when file does not exist
        await service.LoadAsync(TestContext.Current.CancellationToken);
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)service.Current.Hotkeys).Clear());
        Should.Throw<NotSupportedException>(() =>
            ((IList<SelectionReadMethod>)service.Current.Selection.ReadOrder).Clear());

        // Location 5: UpdateAsync
        var headers = new Dictionary<string, string> { ["H1"] = "V1" };
        await service.UpdateAsync(s => s with
        {
            Translation = s.Translation with
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "P1",
                        Model = "m1",
                        OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers }
                    }
                ]
            }
        }, TestContext.Current.CancellationToken);

        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<string, string>)service.Current.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!).Clear());

        // Location 3: Normal load with existing valid file
        using (var service2 = CreateService(dir))
        {
            await service2.LoadAsync(TestContext.Current.CancellationToken);
            Should.Throw<NotSupportedException>(() =>
                ((IDictionary<IrisAction, HotkeyBinding>)service2.Current.Hotkeys).Clear());
            Should.Throw<NotSupportedException>(() =>
                ((IDictionary<string, string>)service2.Current.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!).Clear());
        }

        // Location 3 (Higher version branch):
        var highVersionJson = """
        {
          "schemaVersion": 999,
          "general": { "uiLanguage": "en-US" }
        }
        """;
        await File.WriteAllTextAsync(paths.SettingsFile, highVersionJson, TestContext.Current.CancellationToken);
        using (var service3 = CreateService(dir))
        {
            await service3.LoadAsync(TestContext.Current.CancellationToken);
            Should.Throw<NotSupportedException>(() =>
                ((IDictionary<IrisAction, HotkeyBinding>)service3.Current.Hotkeys).Clear());
        }

        // Location 4: Corruption recovery
        await File.WriteAllTextAsync(paths.SettingsFile, "{ broken json !!", TestContext.Current.CancellationToken);
        using (var service4 = CreateService(dir))
        {
            await Should.ThrowAsync<IrisException>(async () =>
                await service4.LoadAsync(TestContext.Current.CancellationToken));

            Should.Throw<NotSupportedException>(() =>
                ((IDictionary<IrisAction, HotkeyBinding>)service4.Current.Hotkeys).Clear());
            Should.Throw<NotSupportedException>(() =>
                ((IList<SelectionReadMethod>)service4.Current.Selection.ReadOrder).Clear());
        }
    }

    [Fact]
    public async Task T010B_OldVersion_RemainsStable_AcrossSubsequentUpdates()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var firstCurrent = service.Current;
        var firstHotkeyKey = firstCurrent.Hotkeys[IrisAction.InputTranslate].Key;

        SettingsChangedEventArgs? capturedArgs = null;
        service.Changed += (_, e) => capturedArgs = e;

        await service.UpdateAsync(s => s with
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(s.Hotkeys)
            {
                [IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "X")
            }
        }, TestContext.Current.CancellationToken);

        // Old version retained original value and remains frozen
        firstCurrent.Hotkeys[IrisAction.InputTranslate].Key.ShouldBe(firstHotkeyKey);
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)firstCurrent.Hotkeys).Clear());

        // New version has new value and is frozen
        service.Current.Hotkeys[IrisAction.InputTranslate].Key.ShouldBe("X");
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)service.Current.Hotkeys).Clear());

        // Event args
        capturedArgs.ShouldNotBeNull();
        ReferenceEquals(capturedArgs!.OldSettings, firstCurrent).ShouldBeTrue();
        ReferenceEquals(capturedArgs!.NewSettings, service.Current).ShouldBeTrue();
    }

    [Fact]
    public async Task T010B_NoOpUpdate_SameReference_And_SameJson_RetainsCurrentReference_NoDiskNoEvent()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var originalCurrent = service.Current;
        var eventCount = 0;
        service.Changed += (_, _) => eventCount++;

        var diskBytesBefore = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);

        // 1. Same reference
        await service.UpdateAsync(s => s, TestContext.Current.CancellationToken);
        eventCount.ShouldBe(0);
        ReferenceEquals(service.Current, originalCurrent).ShouldBeTrue();

        // 2. Different reference but identical serialized JSON
        await service.UpdateAsync(s => s with
        {
            Translation = s.Translation with
            {
                MaxConcurrentProviders = s.Translation.MaxConcurrentProviders
            }
        }, TestContext.Current.CancellationToken);
        eventCount.ShouldBe(0);
        ReferenceEquals(service.Current, originalCurrent).ShouldBeTrue();

        var diskBytesAfter = await File.ReadAllBytesAsync(paths.SettingsFile, TestContext.Current.CancellationToken);
        diskBytesAfter.ShouldBe(diskBytesBefore);
    }

    [Fact]
    public async Task T010B_FailurePath_MutateThrowsOrMutatesOldCollection_CurrentRemainsUnchanged()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var currentBefore = service.Current;

        // Mutate tries to modify frozen old collection
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await service.UpdateAsync(s =>
            {
                ((IDictionary<IrisAction, HotkeyBinding>)s.Hotkeys).Clear();
                return s;
            }, TestContext.Current.CancellationToken);
        });

        ReferenceEquals(service.Current, currentBefore).ShouldBeTrue();

        // Mutate throws custom exception
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await service.UpdateAsync(_ => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken);
        });

        ReferenceEquals(service.Current, currentBefore).ShouldBeTrue();

        // Subsequent valid update succeeds
        await service.UpdateAsync(s => s with
        {
            General = s.General with { Theme = AppTheme.Dark }
        }, TestContext.Current.CancellationToken);

        service.Current.General.Theme.ShouldBe(AppTheme.Dark);
    }

    [Fact]
    public async Task T010B_ReadOnlyDictionary_Wrapper_Input_IsIsolated_InUpdateAsync()
    {
        using var dir = new TempDirectory();
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        var callerDict = new Dictionary<string, string> { ["X-Caller"] = "original" };
        var wrapped = new ReadOnlyDictionary<string, string>(callerDict);

        await service.UpdateAsync(s => s with
        {
            Translation = s.Translation with
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-wrapped-caller",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "P",
                        Model = "m",
                        OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = wrapped }
                    }
                ]
            }
        }, TestContext.Current.CancellationToken);

        // Caller mutates the dictionary backing their ReadOnlyDictionary
        callerDict["X-Caller"] = "hacked";
        callerDict["X-New"] = "bad";

        // Current must be completely isolated
        service.Current.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!["X-Caller"].ShouldBe("original");
        service.Current.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!.ContainsKey("X-New").ShouldBeFalse();
    }
}


