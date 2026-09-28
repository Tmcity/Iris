using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class TranslationProviderRegistryTests : IDisposable
{
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ISecretStore _secretStore = Substitute.For<ISecretStore>();
    private readonly ILogger<TranslationProviderRegistry> _logger = NullLogger<TranslationProviderRegistry>.Instance;

    private AppSettings _currentSettings = new();

    public TranslationProviderRegistryTests()
    {
        _settingsService.Current.Returns(_ => _currentSettings);
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    private sealed class TestDisposableProvider : ITranslationProvider, IDisposable
    {
        public ProviderDescriptor Descriptor { get; }
        public TranslationProviderProfile Profile { get; }
        public ProviderCredentials Credentials { get; }
        public int DisposeCount { get; private set; }

        public TestDisposableProvider(TranslationProviderProfile profile, ProviderCredentials credentials, bool isConfigured = true)
        {
            Profile = profile;
            Credentials = credentials;
            Descriptor = new ProviderDescriptor(profile.Id, profile.DisplayName, profile.Kind.ToString(), profile.Model, isConfigured);
        }

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TranslationChunk("test");
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(50), "Hello"));

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class NonDisposableProvider : ITranslationProvider
    {
        public ProviderDescriptor Descriptor { get; }

        public NonDisposableProvider(TranslationProviderProfile profile)
        {
            Descriptor = new ProviderDescriptor(profile.Id, profile.DisplayName, profile.Kind.ToString(), profile.Model, true);
        }

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TranslationChunk("test");
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(50)));
    }

    private sealed class DelegatingFactory(TranslationProviderKind kind, Func<TranslationProviderProfile, ProviderCredentials, ITranslationProvider> create)
        : ITranslationProviderFactory
    {
        public TranslationProviderKind Kind => kind;
        public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials) =>
            create(profile, credentials);
    }

    private sealed class TestThrowingDisposeProvider(TranslationProviderProfile profile, string exceptionMessage)
        : ITranslationProvider, IDisposable
    {
        public ProviderDescriptor Descriptor { get; } = new(profile.Id, profile.DisplayName, profile.Kind.ToString(), profile.Model, true);

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TranslationChunk("test");
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(50)));

        public void Dispose()
        {
            throw new InvalidOperationException(exceptionMessage);
        }
    }

    private sealed class TestMemoryLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception != null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }

    #region 1. Selection & Construction

    [Fact]
    public async Task Selection_EmptyConfig_ReturnsEmptySnapshot()
    {
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [] }
        };

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [], _logger);
        using var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        snapshot.Providers.Count.ShouldBe(0);
        snapshot.Settings.ShouldBeSameAs(_currentSettings);
    }

    [Fact]
    public async Task Selection_FiltersDisabled_And_SortsByOrderStably()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "m", Order = 10, Enabled = true };
        var p2 = new TranslationProviderProfile { Id = "tp-2", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P2", Model = "m", Order = 5, Enabled = true };
        var p3 = new TranslationProviderProfile { Id = "tp-3", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P3", Model = "m", Order = 5, Enabled = true };
        var p4 = new TranslationProviderProfile { Id = "tp-4", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P4", Model = "m", Order = 2, Enabled = false }; // disabled

        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1, p2, p3, p4] }
        };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        using var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        snapshot.Providers.Count.ShouldBe(3);
        // Order: p2 (order 5), p3 (order 5, preserved list index), p1 (order 10)
        snapshot.Providers[0].Profile.Id.ShouldBe("tp-2");
        snapshot.Providers[1].Profile.Id.ShouldBe("tp-3");
        snapshot.Providers[2].Profile.Id.ShouldBe("tp-1");
    }

    [Fact]
    public void Selection_DuplicateFactoryKind_ThrowsInvalidOperationException()
    {
        var f1 = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        var f2 = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));

        Should.Throw<InvalidOperationException>(() =>
            new TranslationProviderRegistry(_settingsService, _secretStore, [f1, f2], _logger));
    }

    [Fact]
    public async Task Selection_MissingFactoryForEnabledProfile_ThrowsIrisException()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-anthropic", Kind = TranslationProviderKind.Anthropic, DisplayName = "Anthropic", Model = "claude", Enabled = true };
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1] }
        };

        // Only register OpenAICompatible factory
        var f1 = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [f1], _logger);

        var ex = await Should.ThrowAsync<IrisException>(async () => await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.Unknown);
    }

    [Fact]
    public async Task Selection_LocalFreeProvider_IsMarkedConfigured_ByFactory()
    {
        var pLocal = new TranslationProviderProfile { Id = "tp-local", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "Ollama", Model = "qwen", Enabled = true };
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [pLocal] }
        };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
            new TestDisposableProvider(p, c, isConfigured: true)); // Local factory considers no key as configured

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        using var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        snapshot.Providers[0].Provider.Descriptor.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task Selection_ExplicitBadCredentialReference_UsesUnavailableTranslationProvider()
    {
        var pBad = new TranslationProviderProfile
        {
            Id = "tp-bad-ref",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Bad Ref",
            Model = "gpt-4o",
            CredentialSetId = "cs-invalid-format",
            Enabled = true
        };
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [pBad] }
        };

        var factoryCalled = false;
        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            factoryCalled = true;
            return new TestDisposableProvider(p, c);
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        using var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        factoryCalled.ShouldBeFalse();
        snapshot.Providers.Count.ShouldBe(1);
        var provider = snapshot.Providers[0].Provider;
        provider.Descriptor.IsConfigured.ShouldBeFalse();
        provider.Descriptor.DisplayName.ShouldBe("Bad Ref");

        // TranslateStreamAsync throws NotConfigured on first iteration
        var request = new ProviderTranslateRequest("test", "en", "zh-Hans");
        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var chunk in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
            {
                // Shouldn't reach
            }
        });
        ex.Code.ShouldBe(ErrorCode.NotConfigured);
        ex.ProviderId.ShouldBe("tp-bad-ref");

        // TestAsync returns Success = false
        var testResult = await provider.TestAsync(TestContext.Current.CancellationToken);
        testResult.Success.ShouldBeFalse();
        testResult.Error.ShouldNotBeNull();
        testResult.Error!.Code.ShouldBe(ErrorCode.NotConfigured);
    }

    #endregion

    #region 2. Instance Reuse & Fingerprinting

    [Fact]
    public async Task Reuse_RepeatedAcquireSnapshot_ReusesInstancesAndInstanceIds()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1] }
        };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        using var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        using var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        snap1.Providers[0].InstanceId.ShouldBe(snap2.Providers[0].InstanceId);
        snap1.Providers[0].Provider.ShouldBeSameAs(snap2.Providers[0].Provider);
    }

    [Fact]
    public async Task Reuse_UnrelatedSettingsChange_ReusesInstancesAndInstanceIds()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings
        {
            General = new GeneralSettings { Theme = AppTheme.Light },
            Translation = new TranslationSettings { Providers = [p1] }
        };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        using var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        // Simulate settings update on unrelated setting
        _currentSettings = _currentSettings with
        {
            General = _currentSettings.General with { Theme = AppTheme.Dark }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        using var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        snap2.Settings.General.Theme.ShouldBe(AppTheme.Dark);
        snap2.Providers[0].InstanceId.ShouldBe(snap1.Providers[0].InstanceId);
        snap2.Providers[0].Provider.ShouldBeSameAs(snap1.Providers[0].Provider);
    }

    [Fact]
    public async Task Reuse_ChangingOrderOnly_ReordersView_ButPreservesInstancesAndInstanceIds()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Order = 1, Enabled = true };
        var p2 = new TranslationProviderProfile { Id = "tp-2", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P2", Model = "gpt-4o", Order = 2, Enabled = true };
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1, p2] }
        };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        using var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        var id1Before = snap1.Providers[0].InstanceId;
        var id2Before = snap1.Providers[1].InstanceId;
        var prov1Before = snap1.Providers[0].Provider;
        var prov2Before = snap1.Providers[1].Provider;

        // Swap order
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = [p1 with { Order = 10 }, p2 with { Order = 5 }]
            }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        using var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        // View reordered: tp-2 (order 5), tp-1 (order 10)
        snap2.Providers[0].Profile.Id.ShouldBe("tp-2");
        snap2.Providers[0].InstanceId.ShouldBe(id2Before);
        snap2.Providers[0].Provider.ShouldBeSameAs(prov2Before);

        snap2.Providers[1].Profile.Id.ShouldBe("tp-1");
        snap2.Providers[1].InstanceId.ShouldBe(id1Before);
        snap2.Providers[1].Provider.ShouldBeSameAs(prov1Before);
    }

    [Fact]
    public async Task Reuse_ChangingModelOrUrlOrHeadersOrKey_ReconstructsInstance_WithNewInstanceId()
    {
        var headers1 = new Dictionary<string, string> { ["X-Header"] = "v1" };
        var p1 = new TranslationProviderProfile
        {
            Id = "tp-1",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "P1",
            Model = "gpt-4o",
            BaseUrl = "https://api.openai.com/v1",
            OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers1 },
            Enabled = true
        };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        using var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        var instId1 = snap1.Providers[0].InstanceId;

        // Change Model
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1 with { Model = "gpt-4o-mini" }] }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);
        using var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap2.Providers[0].InstanceId.ShouldNotBe(instId1);

        // Change BaseUrl
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1 with { BaseUrl = "https://custom.com/v1" }] }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);
        using var snap3 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap3.Providers[0].InstanceId.ShouldNotBe(snap2.Providers[0].InstanceId);

        // Change ExtraHeaders
        var headers2 = new Dictionary<string, string> { ["X-Header"] = "v2" };
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = [p1 with { OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers2 } }]
            }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);
        using var snap4 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap4.Providers[0].InstanceId.ShouldNotBe(snap3.Providers[0].InstanceId);
    }

    #endregion

    #region 3. Leases, Retirement & Disposal

    [Fact]
    public async Task Lease_OldSnapshotRemainsValid_AcrossRebuild_AndDisposedWhenLastLeaseReleased()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        TestDisposableProvider? firstProvider = null;
        TestDisposableProvider? secondProvider = null;

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            var provider = new TestDisposableProvider(p, c);
            if (firstProvider == null) firstProvider = provider;
            else secondProvider = provider;
            return provider;
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        // Consumer acquires snapshot 1
        var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        firstProvider.ShouldNotBeNull();
        firstProvider!.DisposeCount.ShouldBe(0);

        // Settings change -> Rebuild creates second provider and retires first provider
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1 with { Model = "gpt-4o-mini" }] }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        // firstProvider must NOT be disposed yet because snap1 still holds lease!
        firstProvider.DisposeCount.ShouldBe(0);

        // Consumer acquires snapshot 2
        using var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        secondProvider.ShouldNotBeNull();
        snap2.Providers[0].Provider.ShouldBeSameAs(secondProvider);

        // Old snapshot 1 is still fully usable
        snap1.Providers[0].Provider.ShouldBeSameAs(firstProvider);

        // Consumer finishes subtasks and disposes snapshot 1
        snap1.Dispose();
        firstProvider.DisposeCount.ShouldBe(1);

        // Double dispose of snapshot 1 is idempotent
        snap1.Dispose();
        firstProvider.DisposeCount.ShouldBe(1);

        // secondProvider is still active and undisposed
        secondProvider!.DisposeCount.ShouldBe(0);
    }

    [Fact]
    public async Task Lease_RegistryDispose_RetiresInstances_AndDisposesAfterSnapshotRelease()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        TestDisposableProvider? provider = null;
        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            provider = new TestDisposableProvider(p, c);
            return provider;
        });

        var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        provider.ShouldNotBeNull();
        provider!.DisposeCount.ShouldBe(0);

        // Dispose Registry while snapshot is still out
        registry.Dispose();

        // Provider is NOT disposed yet because snapshot lease is active
        provider.DisposeCount.ShouldBe(0);

        // Registry rejects new operations
        await Should.ThrowAsync<ObjectDisposedException>(async () => await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await registry.RefreshAsync(TestContext.Current.CancellationToken));

        // Snapshot is disposed by consumer
        snapshot.Dispose();

        // Now provider is disposed!
        provider.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task Lease_NonDisposableProvider_SupportedWithoutError()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-nondisp", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P", Model = "m", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new NonDisposableProvider(p));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        using (var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken))
        {
            snap.Providers.Count.ShouldBe(1);
        }

        // Retiring non-disposable provider succeeds cleanly
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [] } };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lease_CreateTransientAsync_IsFullyIndependent_AndFreezesDraftHeaders()
    {
        var draftHeaders = new Dictionary<string, string> { ["X-Draft"] = "initial" };
        var draftProfile = new TranslationProviderProfile
        {
            Id = "tp-transient",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Transient",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = draftHeaders },
            Enabled = false // Disabled profile can still be tested via transient
        };

        TestDisposableProvider? transientProvider = null;
        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            transientProvider = new TestDisposableProvider(p, c);
            return transientProvider;
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        var creds = new ProviderCredentials("test-key");
        var transientSnap = await registry.CreateTransientAsync(draftProfile, creds, TestContext.Current.CancellationToken);

        // Caller mutates their draft after CreateTransientAsync
        draftHeaders["X-Draft"] = "mutated";
        draftHeaders["X-New"] = "bad";

        transientProvider.ShouldNotBeNull();
        transientProvider!.Profile.OpenAIOptions!.ExtraHeaders!["X-Draft"].ShouldBe("initial");
        transientProvider.Profile.OpenAIOptions!.ExtraHeaders!.ContainsKey("X-New").ShouldBeFalse();
        transientProvider.DisposeCount.ShouldBe(0);

        // Transient snapshot disposal disposes provider
        transientSnap.Dispose();
        transientProvider.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task Snapshot_AccessingPropertiesAfterDisposal_ThrowsObjectDisposedException()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap.Dispose();

        Should.Throw<ObjectDisposedException>(() => _ = snap.Settings);
        Should.Throw<ObjectDisposedException>(() => _ = snap.Providers);
    }

    #endregion

    #region 4. Concurrency, Events & Privacy

    [Fact]
    public async Task Concurrency_MultipleConcurrentAcquireAndRefresh_SucceedsWithoutDeadlockOrCorruption()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        var ct = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            if (i % 3 == 0)
            {
                await registry.RefreshAsync(ct);
            }
            using var snap = await registry.AcquireSnapshotAsync(ct);
            snap.Providers.Count.ShouldBe(1);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Events_Changed_FiredOnReconstruction_NotFiredOnUnrelatedChanges()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings
        {
            General = new GeneralSettings { Theme = AppTheme.Light },
            Translation = new TranslationSettings { Providers = [p1] }
        };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        var changedCount = 0;
        registry.Changed += (_, _) => changedCount++;

        // 1. Unrelated setting change (Theme) -> No Changed event
        _currentSettings = _currentSettings with
        {
            General = _currentSettings.General with { Theme = AppTheme.Dark }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);
        changedCount.ShouldBe(0);

        // 2. Model change -> Reconstructed -> Fires Changed
        _currentSettings = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [p1 with { Model = "gpt-4o-mini" }] }
        };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);
        changedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Events_SubscriberException_DoesNotBlockOtherSubscribers()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "gpt-4o", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        var sub2Called = false;
        registry.Changed += (_, _) => throw new InvalidOperationException("Subscriber 1 boom");
        registry.Changed += (_, _) => sub2Called = true;

        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "gpt-4o-mini" }] } };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        sub2Called.ShouldBeTrue();
    }

    [Fact]
    public async Task Privacy_WhenFactoryThrowsWithSensitiveKey_DoesNotLeakInExceptionOrLog()
    {
        const string secretKey = "sk-secret-leaked-key";
        var p1 = new TranslationProviderProfile { Id = "tp-leak", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P", Model = "m", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };
        _secretStore.Get(SecretKeys.ApiKey("tp-leak")).Returns(secretKey);

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
            throw new InvalidOperationException($"Direct secret leak: {c.ApiKey}"));

        var memoryLogger = new TestMemoryLogger<TranslationProviderRegistry>();
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], memoryLogger);

        var ex = await Should.ThrowAsync<IrisException>(async () => await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldNotContain(secretKey);
        ex.InnerException.ShouldBeNull(); // Inner exception chain disconnected

        memoryLogger.Messages.ShouldNotBeEmpty();
        foreach (var msg in memoryLogger.Messages)
        {
            msg.ShouldNotContain(secretKey);
            msg.ShouldNotContain("Direct secret leak");
        }
    }

    [Fact]
    public async Task Privacy_WhenDisposeThrowsWithSensitiveKey_DoesNotLeakInLog()
    {
        const string secretKey = "sk-secret-leak-dispose";
        var p1 = new TranslationProviderProfile { Id = "tp-leak-disp", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P", Model = "m", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };
        _secretStore.Get(SecretKeys.ApiKey("tp-leak-disp")).Returns(secretKey);

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
            new TestThrowingDisposeProvider(p, $"Dispose leak: {c.ApiKey}"));

        var memoryLogger = new TestMemoryLogger<TranslationProviderRegistry>();
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], memoryLogger);

        using (var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken))
        {
            snap.Providers.Count.ShouldBe(1);
        }

        // Retiring triggers dispose
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [] } };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        memoryLogger.Messages.ShouldNotBeEmpty();
        foreach (var msg in memoryLogger.Messages)
        {
            msg.ShouldNotContain(secretKey);
            msg.ShouldNotContain("Dispose leak");
        }
    }

    [Fact]
    public async Task Rollback_WhenFactoryFailsMidway_CleansUpNewlyCreatedInstances_AndPreservesOldState()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "m", Order = 1, Enabled = true };
        var p2 = new TranslationProviderProfile { Id = "tp-2", Kind = TranslationProviderKind.Anthropic, DisplayName = "P2", Model = "m", Order = 2, Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        TestDisposableProvider? firstInstance = null;
        var f1 = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            firstInstance = new TestDisposableProvider(p, c);
            return firstInstance;
        });
        var f2 = new DelegatingFactory(TranslationProviderKind.Anthropic, (p, c) =>
            throw new InvalidOperationException("Factory 2 failed"));

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [f1, f2], _logger);

        // Initial success with p1
        using var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap1.Providers.Count.ShouldBe(1);
        firstInstance.ShouldNotBeNull();
        firstInstance!.DisposeCount.ShouldBe(0);

        // Update settings to add p2 (which will fail in factory)
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1, p2] } };

        await Should.ThrowAsync<IrisException>(async () => await registry.RefreshAsync(TestContext.Current.CancellationToken));

        // Previous state remains active and intact
        using var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap2.Providers.Count.ShouldBe(1);
        snap2.Providers[0].Provider.ShouldBeSameAs(firstInstance);
        firstInstance.DisposeCount.ShouldBe(0);
    }

    #endregion
}
