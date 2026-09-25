using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Platform.Windows.Security;
using Iris.Platform.Windows.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Security;

public sealed class DpapiSecretStoreTests
{
    private static readonly byte[] Entropy = "Iris.Secrets.v1"u8.ToArray();

    private static DpapiSecretStore CreateStore(
        TempDirectory dir,
        FakeTimeProvider? timeProvider = null,
        ListLogger<DpapiSecretStore>? logger = null)
    {
        var paths = new FakeAppPaths(dir.Path);
        var tp = timeProvider ?? new FakeTimeProvider();
        var log = logger ?? new ListLogger<DpapiSecretStore>();
        return new DpapiSecretStore(paths, tp, log);
    }

    [Fact]
    public void S1_Set_Then_Get_ReturnsOriginalValue()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        store.Set("provider/p1/api-key", "secret-token-123");
        store.Get("provider/p1/api-key").ShouldBe("secret-token-123");
    }

    [Fact]
    public void S2_NewInstance_ReadsExistingFile_Persists()
    {
        using var dir = new TempDirectory();
        var store1 = CreateStore(dir);
        store1.Set("key1", "val1");

        var store2 = CreateStore(dir);
        store2.Get("key1").ShouldBe("val1");
    }

    [Fact]
    public void S3_FileContent_DoesNotContainPlaintextOrPlaintextBase64()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var store = CreateStore(dir);

        const string secret = "super-secret-password-12345";
        store.Set("secret-key", secret);

        var fileBytes = File.ReadAllBytes(paths.SecretsFile);

        // UTF-8 bytes of secret
        fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)).ShouldBe(-1);

        // UTF-16 bytes of secret
        fileBytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)).ShouldBe(-1);

        // Base64 of plaintext
        var plainB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plainB64)).ShouldBe(-1);
    }

    [Fact]
    public void S4_Remove_ExistingReturnsTrue_NonExistingReturnsFalseAndDoesNotModifyFileOrTriggerEvent()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var store = CreateStore(dir);

        store.Set("key-to-remove", "some-val");
        var changedFired = false;
        store.Changed += (_, key) =>
        {
            if (key == "key-to-remove") changedFired = true;
        };

        store.Remove("key-to-remove").ShouldBeTrue();
        store.Get("key-to-remove").ShouldBeNull();
        changedFired.ShouldBeTrue();

        // Non-existent key
        var fileBytesBefore = File.ReadAllBytes(paths.SecretsFile);
        var nonExistentFired = false;
        store.Changed += (_, _) => nonExistentFired = true;

        store.Remove("non-existent-key").ShouldBeFalse();
        File.ReadAllBytes(paths.SecretsFile).ShouldBe(fileBytesBefore);
        nonExistentFired.ShouldBeFalse();
    }

    [Fact]
    public void S5_SetEmptyString_EquivalentToRemove()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        store.Set("key1", "val1");
        store.Get("key1").ShouldBe("val1");

        store.Set("key1", "");
        store.Get("key1").ShouldBeNull();
    }

    [Fact]
    public void S6_ParameterValidation_ThrowsExpectedExceptions()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        Should.Throw<ArgumentNullException>(() => store.Set("k", null!));
        Should.Throw<ArgumentException>(() => store.Set("", "v"));
        Should.Throw<ArgumentException>(() => store.Set("   ", "v"));

        Should.Throw<ArgumentException>(() => store.Get(""));
        Should.Throw<ArgumentException>(() => store.Get("   "));

        Should.Throw<ArgumentException>(() => store.Remove(""));
        Should.Throw<ArgumentException>(() => store.Remove("   "));
    }

    [Fact]
    public void S7_WrongEntropy_GetReturnsNull_LogsWarningOnlyOnce_SubsequentSetSucceeds()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var logger = new ListLogger<DpapiSecretStore>();

        // Encrypt with different entropy
        var wrongEntropy = "Wrong.Entropy"u8.ToArray();
        var cipherWithWrongEntropy = ProtectedData.Protect(Encoding.UTF8.GetBytes("secret-value"), wrongEntropy, DataProtectionScope.CurrentUser);
        var fileBytes = SecretsFileCodec.Serialize(new Dictionary<string, byte[]>
        {
            ["wrong-key"] = cipherWithWrongEntropy,
        });
        File.WriteAllBytes(paths.SecretsFile, fileBytes);

        var store = new DpapiSecretStore(paths, new FakeTimeProvider(), logger);

        // First Get: returns null, logs Warning
        store.Get("wrong-key").ShouldBeNull();
        // Second Get: returns null, does NOT log duplicate warning
        store.Get("wrong-key").ShouldBeNull();

        var warnings = logger.Entries.Where(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("wrong-key")).ToList();
        warnings.Count.ShouldBe(1);

        // Now overwrite with new value
        store.Set("wrong-key", "new-correct-value");
        store.Get("wrong-key").ShouldBe("new-correct-value");
    }

    [Fact]
    public void S8_TamperedCipher_GetReturnsNullWithoutThrowing()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        var validCipher = ProtectedData.Protect(Encoding.UTF8.GetBytes("good-value"), Entropy, DataProtectionScope.CurrentUser);
        // Tamper with cipher byte
        validCipher[validCipher.Length / 2] ^= 0xFF;

        var fileBytes = SecretsFileCodec.Serialize(new Dictionary<string, byte[]>
        {
            ["tampered-key"] = validCipher,
        });
        File.WriteAllBytes(paths.SecretsFile, fileBytes);

        var store = CreateStore(dir);
        store.Get("tampered-key").ShouldBeNull();
    }

    [Fact]
    public void S9_CorruptedFile_BacksUpToCorruptDat_AndStoreCanWriteNormally()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        const string corruptJson = "{ corrupted json file syntax !";
        File.WriteAllText(paths.SecretsFile, corruptJson);

        var fixedTime = new DateTimeOffset(2026, 9, 25, 15, 30, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);
        var logger = new ListLogger<DpapiSecretStore>();

        var store = new DpapiSecretStore(paths, timeProvider, logger);

        var expectedBackup = Path.Combine(dir.Path, "secrets.corrupt-20260925153000.dat");
        File.Exists(expectedBackup).ShouldBeTrue();
        File.ReadAllText(expectedBackup).ShouldBe(corruptJson);

        // Store is writable
        store.Set("new-key", "new-val");
        store.Get("new-key").ShouldBe("new-val");
        File.Exists(paths.SecretsFile).ShouldBeTrue();
    }

    [Fact]
    public void S10_CorruptEntry_DropsOnlyBadEntry_LogsWarningWithKey()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        var goodCipher = ProtectedData.Protect(Encoding.UTF8.GetBytes("good-val"), Entropy, DataProtectionScope.CurrentUser);
        var goodB64 = Convert.ToBase64String(goodCipher);
        var json = $$"""
        {
          "format": "iris-secrets",
          "version": 1,
          "entries": {
            "good-key": "{{goodB64}}",
            "bad-b64-key": "not-valid-base64!"
          }
        }
        """;
        File.WriteAllText(paths.SecretsFile, json);

        var logger = new ListLogger<DpapiSecretStore>();
        var store = new DpapiSecretStore(paths, new FakeTimeProvider(), logger);

        store.Get("good-key").ShouldBe("good-val");
        store.Get("bad-b64-key").ShouldBeNull();

        logger.Entries.ShouldContain(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("bad-b64-key"));
    }

    [Fact]
    public void S11_FileLockedAtConstruction_DegradedMode_SetThrowsSecretUnavailable_AfterReleaseSetSucceedsWithBoth()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        // Pre-create file with initial key
        var initialCipher = ProtectedData.Protect(Encoding.UTF8.GetBytes("initial-val"), Entropy, DataProtectionScope.CurrentUser);
        var initialBytes = SecretsFileCodec.Serialize(new Dictionary<string, byte[]>
        {
            ["initial-key"] = initialCipher,
        });
        File.WriteAllBytes(paths.SecretsFile, initialBytes);

        DpapiSecretStore store;
        using (var lockStream = new FileStream(paths.SecretsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var logger = new ListLogger<DpapiSecretStore>();
            store = new DpapiSecretStore(paths, new FakeTimeProvider(), logger);

            // In degraded mode, Get returns null
            store.Get("initial-key").ShouldBeNull();

            // Set throws SecretUnavailable
            var ex = Should.Throw<IrisException>(() => store.Set("new-key", "new-val"));
            ex.Code.ShouldBe(ErrorCode.SecretUnavailable);
        }

        // Lock released: Set succeeds and recovers
        store.Set("new-key", "new-val");
        store.Get("new-key").ShouldBe("new-val");
        store.Get("initial-key").ShouldBe("initial-val");

        // Verify with brand new instance
        var store2 = CreateStore(dir);
        store2.Get("initial-key").ShouldBe("initial-val");
        store2.Get("new-key").ShouldBe("new-val");
    }

    [Fact]
    public void S12_WriteFailure_TmpLocked_ThrowsStorageFailure_MemoryUnchanged_NoEventFired()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var store = CreateStore(dir);

        store.Set("key1", "old-val");

        var eventFired = false;
        store.Changed += (_, _) => eventFired = true;

        var tmpFile = paths.SecretsFile + ".tmp";
        using (new FileStream(tmpFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Should.Throw<IrisException>(() => store.Set("key1", "new-val"));
            ex.Code.ShouldBe(ErrorCode.StorageFailure);
        }

        store.Get("key1").ShouldBe("old-val");
        eventFired.ShouldBeFalse();
    }

    [Fact]
    public void S13_Changed_TriggersOnValidChange_AndIsolatesHandlerExceptions()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        var handledKeys = new List<string>();
        var secondHandlerCalled = false;

        store.Changed += (_, _) => throw new InvalidOperationException("Handler 1 failure");
        store.Changed += (_, key) =>
        {
            handledKeys.Add(key);
            secondHandlerCalled = true;
        };

        store.Set("provider/p1/api-key", "val1");

        secondHandlerCalled.ShouldBeTrue();
        handledKeys.ShouldContain("provider/p1/api-key");
    }

    [Fact]
    public async Task S14_ReentrantCallInsideChangedHandler_DoesNotDeadlock()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        var reentrantTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        store.Changed += (_, key) =>
        {
            if (key == "trigger-key")
            {
                // Directly call Get and Set from within Changed handler
                var readVal = store.Get("trigger-key");
                readVal.ShouldBe("initial");

                store.Set("secondary-key", "secondary-val");
                reentrantTcs.TrySetResult(true);
            }
        };

        store.Set("trigger-key", "initial");

        await reentrantTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        store.Get("secondary-key").ShouldBe("secondary-val");
    }

    [Fact]
    public async Task S15_ConcurrentUpdates_20Tasks_AllPersisted()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            store.Set($"key-{i}", $"val-{i}");
        })).ToArray();

        await Task.WhenAll(tasks);

        var store2 = CreateStore(dir);
        for (var i = 0; i < 20; i++)
        {
            store2.Get($"key-{i}").ShouldBe($"val-{i}");
        }
    }

    [Fact]
    public void S16_NoBakFileCreated_AtAnyTime()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var store = CreateStore(dir);

        store.Set("k1", "v1");
        store.Set("k1", "v2");
        store.Remove("k1");

        File.Exists(paths.SecretsFile + ".bak").ShouldBeFalse();
        File.Exists(Path.Combine(dir.Path, "secrets.dat.bak")).ShouldBeFalse();
    }

    [Fact]
    public void S17_Privacy_SecretValueNeverAppearsInLogsOrExceptions()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var logger = new ListLogger<DpapiSecretStore>();
        var store = new DpapiSecretStore(paths, new FakeTimeProvider(), logger);

        const string sensitiveValue = "sk-SECRET-7f3a";

        // S1: Set
        store.Set("test-key", sensitiveValue);
        store.Get("test-key").ShouldBe(sensitiveValue);

        // S4: Remove
        store.Remove("test-key");

        // S7: Decryption failure
        var wrongEntropy = "Wrong.Entropy"u8.ToArray();
        var cipherWithWrongEntropy = ProtectedData.Protect(Encoding.UTF8.GetBytes(sensitiveValue), wrongEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(paths.SecretsFile, SecretsFileCodec.Serialize(new Dictionary<string, byte[]>
        {
            ["fail-key"] = cipherWithWrongEntropy,
        }));
        var storeWithFail = new DpapiSecretStore(paths, new FakeTimeProvider(), logger);
        storeWithFail.Get("fail-key");

        // S12: Write failure
        var tmpFile = paths.SecretsFile + ".tmp";
        using (new FileStream(tmpFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Should.Throw<IrisException>(() => storeWithFail.Set("another-key", sensitiveValue));
            ex.Message.ShouldNotContain(sensitiveValue);
            ex.Detail?.ShouldNotContain(sensitiveValue);
        }

        // Verify logger entries
        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain(sensitiveValue);
            entry.Exception?.Message.ShouldNotContain(sensitiveValue);
        }
    }

    [Fact]
    public void AC5_ManualInspection_TwoSecretsFileContent()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);
        var store = CreateStore(dir);

        store.Set("provider/deepseek/api-key", "sk-ds-abcdef1234567890");
        store.Set("provider/baidu/secret-key", "bd-sec-9876543210abcdef");

        var text = File.ReadAllText(paths.SecretsFile);
        text.ShouldContain("\"format\": \"iris-secrets\"");
        text.ShouldContain("\"version\": 1");
        text.ShouldContain("\"provider/deepseek/api-key\": \"");
        text.ShouldContain("\"provider/baidu/secret-key\": \"");
    }
}
