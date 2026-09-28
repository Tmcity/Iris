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
        public Action? OnDispose { get; set; }

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
            OnDispose?.Invoke();
        }
    }

    private sealed class TestGate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();

        public void Pause(TimeSpan? timeout = null)
        {
            Entered.TrySetResult();
            if (!_release.Wait(timeout ?? TimeSpan.FromSeconds(8)))
            {
                throw new TimeoutException("TestGate wait timed out.");
            }
        }

        public void Open() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    private sealed class TestQueueSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<Action> _actions = new();
        public int Count => _actions.Count;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _actions.Enqueue(() => d(state));
        }

        public void Drain()
        {
            while (_actions.TryDequeue(out var action))
            {
                action();
            }
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

        // Existing lease remains active and intact
        snap1.Providers.Count.ShouldBe(1);
        snap1.Providers[0].Provider.ShouldBeSameAs(firstInstance);
        firstInstance.DisposeCount.ShouldBe(0);

        // Subsequent Acquire after failure throws IrisException rather than silently leasing stale generation
        await Should.ThrowAsync<IrisException>(async () => await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken));
    }

    #endregion

    #region 5. R1 Rework Verification

    [Fact]
    public async Task R1_1_ImmutableCollections_ProvidersCannotBeMutatedViaIListCast_AndSubsequentLeaseIntact()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "m", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));
        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        using (var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken))
        {
            var list = (System.Collections.IList)snap.Providers;
            Should.Throw<NotSupportedException>(() => list.Clear());
            Should.Throw<NotSupportedException>(() => list.Add(snap.Providers[0]));

            var genericList = snap.Providers as IList<TranslationProviderRegistration>;
            if (genericList != null)
            {
                Should.Throw<NotSupportedException>(() => genericList.Clear());
            }
        }

        // Subsequent lease is intact and has count 1
        using (var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken))
        {
            snap2.Providers.Count.ShouldBe(1);
        }

        // Transient snapshot also cannot be mutated
        var draft = new TranslationProviderProfile { Id = "tp-t", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "T", Model = "m", Enabled = true };
        using (var transSnap = await registry.CreateTransientAsync(draft, ProviderCredentials.None, TestContext.Current.CancellationToken))
        {
            var list = (System.Collections.IList)transSnap.Providers;
            Should.Throw<NotSupportedException>(() => list.Clear());
        }
    }

    [Fact]
    public async Task R1_2_Barrier_AcquireSnapshotAsync_WaitsForInProgressRebuild_AndReceivesNewModel()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        using var factoryGate = new TestGate();

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            if (p.Model == "model-b")
            {
                factoryGate.Pause();
            }
            return new TestDisposableProvider(p, c);
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        // Initial state: model-a published
        using (var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken))
        {
            snap1.Providers[0].Profile.Model.ShouldBe("model-a");
        }

        // Update settings to model-b
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };

        // Start refresh in background
        var refreshTask = registry.RefreshAsync(TestContext.Current.CancellationToken);

        // Wait until factory for model-b is entered
        await factoryGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // While refresh is paused on gate, acquire snapshot
        var acquireTask = registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

        // Verify acquireTask does NOT complete while rebuild is running
        acquireTask.IsCompleted.ShouldBeFalse();

        // Release factory gate
        factoryGate.Open();

        await refreshTask;
        using var snapB = await acquireTask;

        snapB.Providers[0].Profile.Model.ShouldBe("model-b");
    }

    [Fact]
    public async Task R1_2_Barrier_RebuildFailure_PreservesOldLease_AndNewAcquiresThrow_AndRecoverySucceeds()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        TestDisposableProvider? provA = null;
        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            if (p.Model == "model-b")
            {
                throw new InvalidOperationException("Model B factory failure");
            }
            var prov = new TestDisposableProvider(p, c);
            if (p.Model == "model-a") provA = prov;
            return prov;
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        // Initial snapshot for Model A
        var snapA = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snapA.Providers[0].Profile.Model.ShouldBe("model-a");
        provA.ShouldNotBeNull();
        provA!.DisposeCount.ShouldBe(0);

        // Update settings to Model B (which fails)
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };

        await Should.ThrowAsync<IrisException>(async () => await registry.RefreshAsync(TestContext.Current.CancellationToken));

        // Old lease snapA remains valid and operable
        snapA.Providers[0].Profile.Model.ShouldBe("model-a");
        provA.DisposeCount.ShouldBe(0);

        // Subsequent new AcquireSnapshotAsync calls throw IrisException rather than silently leasing Model A
        await Should.ThrowAsync<IrisException>(async () => await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken));
        await Should.ThrowAsync<IrisException>(async () => await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken));

        // Recovery: Settings updated to valid Model C
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-c" }] } };
        _settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            _settingsService, new SettingsChangedEventArgs(new AppSettings(), _currentSettings));

        using (var snapC = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken))
        {
            snapC.Providers[0].Profile.Model.ShouldBe("model-c");
        }

        // Old lease released: provA is disposed
        snapA.Dispose();
        provA.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task R1_2_Barrier_SettingEventsDuringRebuild_InitialSuccess_DoesNotDropLatestEvent()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        using var bGate = new TestGate();
        var cCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            if (p.Model == "model-b")
            {
                bGate.Pause();
            }
            if (p.Model == "model-c")
            {
                cCreated.TrySetResult();
            }
            return new TestDisposableProvider(p, c);
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        using var old = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        old.Providers[0].Profile.Model.ShouldBe("model-a");

        // Start B rebuild
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };
        var bTask = registry.RefreshAsync(TestContext.Current.CancellationToken);

        await bGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // While B is paused, queue an acquire, verify waiting on B, then cancel it to clear waiters
        using var acquireCts = new CancellationTokenSource();
        var acquireTask = registry.AcquireSnapshotAsync(acquireCts.Token);
        acquireTask.IsCompleted.ShouldBeFalse();
        acquireCts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await acquireTask);

        // While B is paused, new event C arrives via SettingsChanged
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-c" }] } };
        _settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            _settingsService, new SettingsChangedEventArgs(new AppSettings(), _currentSettings));

        // Release B gate
        bGate.Open();
        await bTask;

        // CRITICAL: Wait for background worker to automatically construct C without rescue from Acquire/Refresh
        await cCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Acquire snapshot: must observe C, while old lease remains A
        using var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap.Providers[0].Profile.Model.ShouldBe("model-c");
        old.Providers[0].Profile.Model.ShouldBe("model-a");
    }

    [Fact]
    public async Task R1_2_Barrier_SettingEventsDuringRebuild_InitialFailure_DoesNotDropLatestEvent()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        using var bGate = new TestGate();
        var cCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            if (p.Model == "model-b")
            {
                bGate.Pause();
                throw new InvalidOperationException("B failed");
            }
            if (p.Model == "model-c")
            {
                cCreated.TrySetResult();
            }
            return new TestDisposableProvider(p, c);
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        using var old = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        old.Providers[0].Profile.Model.ShouldBe("model-a");

        // Start B rebuild (will fail)
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };
        var bTask = registry.RefreshAsync(TestContext.Current.CancellationToken);

        await bGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // While B is paused, queue an acquire, verify waiting on B, then cancel it to clear waiters
        using var acquireCts = new CancellationTokenSource();
        var acquireTask = registry.AcquireSnapshotAsync(acquireCts.Token);
        acquireTask.IsCompleted.ShouldBeFalse();
        acquireCts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await acquireTask);

        // While B is paused, new event C arrives via SettingsChanged
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-c" }] } };
        _settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            _settingsService, new SettingsChangedEventArgs(new AppSettings(), _currentSettings));

        // Release B gate
        bGate.Open();
        await Should.ThrowAsync<IrisException>(async () => await bTask);

        // CRITICAL: Wait for background worker to automatically construct C without rescue from Acquire/Refresh
        await cCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Acquire snapshot: must observe C, while old lease remains A
        using var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap.Providers[0].Profile.Model.ShouldBe("model-c");
        old.Providers[0].Profile.Model.ShouldBe("model-a");
    }

    [Fact]
    public async Task R1_2_Barrier_BackgroundRefreshFailure_DoesNotBusyLoopOnSameFailingTarget()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "m", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var failingAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failingAttempts = 0;
        var firstSuccess = true;
        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            if (firstSuccess)
            {
                firstSuccess = false;
                return new TestDisposableProvider(p, c);
            }
            failingAttemptStarted.TrySetResult();
            Interlocked.Increment(ref failingAttempts);
            throw new InvalidOperationException("Persistent failure");
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        // Fire setting change to failing config
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "failing" }] } };
        _settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            _settingsService, new SettingsChangedEventArgs(new AppSettings(), _currentSettings));

        // 证明失败配置确实被后台尝试且失败
        await failingAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<IrisException>(async () => await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken));
        Volatile.Read(ref failingAttempts).ShouldBe(1);

        // 有界观察窗口检查没有重复尝试（不忙循环）
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Volatile.Read(ref failingAttempts).ShouldBe(1);
    }

    [Fact]
    public async Task R1_3_Ownership_CancellationDuringFactory_DisposesNewlyCreatedInstance_AndDoesNotPublish()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        using var bGate = new TestGate();
        TestDisposableProvider? provB = null;

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            var prov = new TestDisposableProvider(p, c);
            if (p.Model == "model-b")
            {
                provB = prov;
                bGate.Pause();
            }
            return prov;
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };

        using var cts = new CancellationTokenSource();
        var refreshTask = registry.RefreshAsync(cts.Token);

        await bGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Cancel while factory is paused
        cts.Cancel();
        bGate.Open();

        await Should.ThrowAsync<OperationCanceledException>(async () => await refreshTask);

        // provB must be disposed!
        provB.ShouldNotBeNull();
        provB!.DisposeCount.ShouldBe(1);

        // Model B was NOT published: state remains model-a
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };
        using var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap.Providers[0].Profile.Model.ShouldBe("model-a");
    }

    [Fact]
    public async Task R1_3_Ownership_DisposeDuringFactory_DisposesNewlyCreatedInstance()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        using var bGate = new TestGate();
        TestDisposableProvider? provB = null;

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            var prov = new TestDisposableProvider(p, c);
            if (p.Model == "model-b")
            {
                provB = prov;
                bGate.Pause();
            }
            return prov;
        });

        var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };

        var refreshTask = registry.RefreshAsync(TestContext.Current.CancellationToken);
        await bGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Dispose registry while factory is paused
        registry.Dispose();
        bGate.Open();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await refreshTask);

        // provB must be disposed!
        provB.ShouldNotBeNull();
        provB!.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task R1_3_Ownership_WaitingOnBarrierCancelled_DoesNotBreakHoldingThread()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        using var bGate = new TestGate();

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            if (p.Model == "model-b")
            {
                bGate.Pause();
            }
            return new TestDisposableProvider(p, c);
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };

        // Thread 1 starts refresh
        var refreshTask = registry.RefreshAsync(TestContext.Current.CancellationToken);
        await bGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Thread 2 calls Acquire with a cancellable token
        using var cts2 = new CancellationTokenSource();
        var acquireTask = registry.AcquireSnapshotAsync(cts2.Token);

        // Cancel Thread 2 while waiting for barrier
        cts2.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await acquireTask);

        // Thread 1 releases gate and finishes successfully
        bGate.Open();
        await refreshTask;

        // Model B is published and usable
        using var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap.Providers[0].Profile.Model.ShouldBe("model-b");
    }

    [Fact]
    public async Task R1_3_Ownership_Transient_CancellationDuringFactory_DisposesProvider()
    {
        var draft = new TranslationProviderProfile { Id = "tp-t", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "T", Model = "m", Enabled = true };
        using var gate = new TestGate();
        TestDisposableProvider? prov = null;

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            prov = new TestDisposableProvider(p, c);
            gate.Pause();
            return prov;
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        using var cts = new CancellationTokenSource();
        var transientTask = registry.CreateTransientAsync(draft, ProviderCredentials.None, cts.Token);

        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cts.Cancel();
        gate.Open();

        await Should.ThrowAsync<OperationCanceledException>(async () => await transientTask);

        prov.ShouldNotBeNull();
        prov!.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task R1_3_Ownership_Transient_DisposeDuringFactory_DisposesProvider()
    {
        var draft = new TranslationProviderProfile { Id = "tp-t", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "T", Model = "m", Enabled = true };
        using var gate = new TestGate();
        TestDisposableProvider? prov = null;

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            prov = new TestDisposableProvider(p, c);
            gate.Pause();
            return prov;
        });

        var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        var transientTask = registry.CreateTransientAsync(draft, ProviderCredentials.None, TestContext.Current.CancellationToken);

        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        registry.Dispose();
        gate.Open();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await transientTask);

        prov.ShouldNotBeNull();
        prov!.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task R1_4_BackgroundExecution_RefreshAndTransient_ExecuteOnThreadPoolThread()
    {
        foreach (var operation in new[] { "refresh", "first-acquire", "transient" })
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var callerThreadId = Environment.CurrentManagedThreadId;
                    var factoryOnPool = false;
                    var secretOnPool = true;
                    var secretReads = 0;

                    var p1 = new TranslationProviderProfile
                    {
                        Id = "tp-bg",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "Bg",
                        Model = "m",
                        CredentialSetId = "cs-0123456789abcdef0123456789abcdef",
                        Enabled = true
                    };

                    var settingsService = Substitute.For<ISettingsService>();
                    var currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };
                    settingsService.Current.Returns(_ => currentSettings);

                    var secretStore = Substitute.For<ISecretStore>();
                    secretStore.Get(Arg.Any<string>()).Returns(callInfo =>
                    {
                        Interlocked.Increment(ref secretReads);
                        secretOnPool &= Thread.CurrentThread.IsThreadPoolThread && Environment.CurrentManagedThreadId != callerThreadId;
                        return "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"key\",\"secretKey\":null}";
                    });

                    var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
                    {
                        factoryOnPool = Thread.CurrentThread.IsThreadPoolThread && Environment.CurrentManagedThreadId != callerThreadId;
                        return new TestDisposableProvider(p, c);
                    });

                    using var registry = new TranslationProviderRegistry(settingsService, secretStore, [factory], _logger);

                    if (operation == "refresh")
                    {
                        registry.RefreshAsync().GetAwaiter().GetResult();
                        factoryOnPool.ShouldBeTrue();
                        secretOnPool.ShouldBeTrue();
                        secretReads.ShouldBeGreaterThan(0);
                    }
                    else if (operation == "first-acquire")
                    {
                        using var snap = registry.AcquireSnapshotAsync().GetAwaiter().GetResult();
                        factoryOnPool.ShouldBeTrue();
                        secretOnPool.ShouldBeTrue();
                        secretReads.ShouldBeGreaterThan(0);
                    }
                    else
                    {
                        using var snap = registry.CreateTransientAsync(p1, ProviderCredentials.None).GetAwaiter().GetResult();
                        factoryOnPool.ShouldBeTrue();
                        secretReads.ShouldBe(0); // 瞬态不应读取 SecretStore
                    }

                    finished.TrySetResult();
                }
                catch (Exception ex)
                {
                    finished.TrySetException(ex);
                }
            }) { IsBackground = true };

            thread.Start();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task R1_4_DraftProfile_ExtraHeadersFrozen_BeforeBackgroundScheduling()
    {
        var headers = new Dictionary<string, string> { ["X-Probe"] = "before" };
        using var gate = new TestGate();
        var draft = new TranslationProviderProfile
        {
            Id = "tp-draft",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Draft",
            Model = "m",
            OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers },
            Enabled = true
        };

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            gate.Pause();
            return new TestDisposableProvider(p, c);
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        // 调度 CreateTransientAsync
        var pending = registry.CreateTransientAsync(draft, ProviderCredentials.None, TestContext.Current.CancellationToken);

        // 调用方在调度后、后台任务完成前修改草稿字典
        headers["X-Probe"] = "after";

        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gate.Open();

        using var snapshot = await pending;
        snapshot.Providers[0].Profile.OpenAIOptions!.ExtraHeaders!["X-Probe"].ShouldBe("before");
    }

    [Fact]
    public async Task R1_4_Metadata_OrderOnlyChange_ReusesInstance_UpdatesRegistrationProfile_AndFiresChanged()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "m", Order = 1, Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        var factoryCallCount = 0;
        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            factoryCallCount++;
            return new TestDisposableProvider(p, c);
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);

        // Initial snapshot with Order = 1
        var snap1 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap1.Providers[0].Profile.Order.ShouldBe(1);
        var instanceId = snap1.Providers[0].InstanceId;
        var providerInstance = snap1.Providers[0].Provider;
        factoryCallCount.ShouldBe(1);

        var changedFired = 0;
        registry.Changed += (_, _) => changedFired++;

        // Update settings: only Order changes 1 -> 9
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Order = 9 }] } };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        // Reused instance, factory not called again
        factoryCallCount.ShouldBe(1);
        // Changed event was fired because Order changed in registration
        changedFired.ShouldBe(1);

        // Snapshot 2 has Order = 9 and same InstanceId
        using var snap2 = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap2.Providers[0].Profile.Order.ShouldBe(9);
        snap2.Settings.Translation.Providers[0].Order.ShouldBe(9);
        snap2.Providers[0].InstanceId.ShouldBe(instanceId);
        snap2.Providers[0].Provider.ShouldBeSameAs(providerInstance);

        // Old snapshot 1 remains intact with Order = 1
        snap1.Providers[0].Profile.Order.ShouldBe(1);
        snap1.Dispose();
    }

    [Fact]
    public async Task R1_5_UnavailableProvider_TranslateStreamAsync_ThrowsOperationCanceledException_WhenCancelledAfterYield()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "tp-unavail",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Unavail",
            Model = "m",
            CredentialSetId = "cs-0123456789abcdef0123456789abcdef",
            Enabled = true
        };

        var provider = new UnavailableTranslationProvider(profile);
        var prior = SynchronizationContext.Current;
        var queue = new TestQueueSynchronizationContext();

        try
        {
            using var cts = new CancellationTokenSource();
            SynchronizationContext.SetSynchronizationContext(queue);

            var request = new ProviderTranslateRequest("test", "en", "zh-Hans");
            var enumerator = provider.TranslateStreamAsync(request, cts.Token).GetAsyncEnumerator(cts.Token);
            var move = enumerator.MoveNextAsync().AsTask();

            SynchronizationContext.SetSynchronizationContext(prior);

            // 确认 MoveNextAsync 已到达异步挂起点且尚未完成
            move.IsCompleted.ShouldBeFalse();
            queue.Count.ShouldBe(1);

            // 取消令牌并放行挂起后的续体
            cts.Cancel();
            queue.Drain();

            await Should.ThrowAsync<OperationCanceledException>(async () => await move);
            await enumerator.DisposeAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    [Fact]
    public async Task R1_5_UnavailableProvider_TranslateStreamAsync_ThrowsOperationCanceledException_WhenCancelledBeforeFirstMoveNext()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "tp-unavail",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Unavail",
            Model = "m",
            CredentialSetId = "cs-0123456789abcdef0123456789abcdef",
            Enabled = true
        };

        var provider = new UnavailableTranslationProvider(profile);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new ProviderTranslateRequest("test", "en", "zh-Hans");
        var enumerator = provider.TranslateStreamAsync(request, cts.Token).GetAsyncEnumerator(cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task R2_1_LockBoundary_RetiredProviderDispose_AndChangedHandler_ExecuteOutsideRebuildLock()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        int? disposeRebuildCount = null;
        int? changedRebuildCount = null;
        string? changedObservedModel = null;
        TestDisposableProvider? provA = null;

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            var prov = new TestDisposableProvider(p, c);
            if (p.Model == "model-a")
            {
                provA = prov;
            }
            return prov;
        });

        using var registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        provA.ShouldNotBeNull();
        provA!.OnDispose = () =>
        {
            disposeRebuildCount = registry.RebuildLockCurrentCount;
        };

        var changedInvoked = 0;
        registry.Changed += (_, _) =>
        {
            changedInvoked++;
            changedRebuildCount = registry.RebuildLockCurrentCount;

            // 订阅者在 Changed 回调中调用 AcquireSnapshotAsync 能够立即取得新快照，不发生死锁
            using var snap = registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            changedObservedModel = snap.Providers[0].Profile.Model;
        };

        // 更新为 model-b，触发 model-a 退休与 Changed 通知
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1 with { Model = "model-b" }] } };
        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        provA.DisposeCount.ShouldBe(1);
        disposeRebuildCount.ShouldBe(1); // 退休 Dispose 在锁外执行，CurrentCount 为 1
        changedInvoked.ShouldBe(1);
        changedRebuildCount.ShouldBe(1); // Changed 委托在锁外执行，CurrentCount 为 1
        changedObservedModel.ShouldBe("model-b");
    }

    [Fact]
    public async Task R2_1_LockBoundary_RollbackCleanup_ExecutesOutsideRebuildLock()
    {
        var p1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-a", Order = 1, Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };

        int? rollbackDisposeRebuildCount = null;
        TestDisposableProvider? provB1 = null;

        TranslationProviderRegistry? registry = null;
        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) =>
        {
            if (p.Model == "model-b1")
            {
                provB1 = new TestDisposableProvider(p, c)
                {
                    OnDispose = () =>
                    {
                        rollbackDisposeRebuildCount = registry?.RebuildLockCurrentCount;
                    }
                };
                return provB1;
            }
            if (p.Model == "model-b2")
            {
                throw new InvalidOperationException("Controlled B2 failure");
            }
            return new TestDisposableProvider(p, c);
        });

        registry = new TranslationProviderRegistry(_settingsService, _secretStore, [factory], _logger);
        using var reg = registry;

        await registry.RefreshAsync(TestContext.Current.CancellationToken);

        // p1 (model-b1, 成功) 与 p2 (model-b2, 失败)
        var pB1 = new TranslationProviderProfile { Id = "tp-1", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P1", Model = "model-b1", Order = 1, Enabled = true };
        var pB2 = new TranslationProviderProfile { Id = "tp-2", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "P2", Model = "model-b2", Order = 2, Enabled = true };
        _currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [pB1, pB2] } };

        await Should.ThrowAsync<IrisException>(async () => await registry.RefreshAsync(TestContext.Current.CancellationToken));

        provB1.ShouldNotBeNull();
        provB1!.DisposeCount.ShouldBe(1);
        rollbackDisposeRebuildCount.ShouldBe(1); // 回滚新建实例 Dispose 在锁外执行，CurrentCount 为 1
    }

    [Fact]
    public async Task R2_1_Barrier_ExplicitRefresh_WaitsForInProgressCredentialResolution()
    {
        var p1 = new TranslationProviderProfile
        {
            Id = "tp-1",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "P1",
            Model = "model-a",
            CredentialSetId = "cs-0123456789abcdef0123456789abcdef",
            Enabled = true
        };
        var settingsService = Substitute.For<ISettingsService>();
        var currentSettings = new AppSettings { Translation = new TranslationSettings { Providers = [p1] } };
        settingsService.Current.Returns(_ => currentSettings);

        using var secretGate = new TestGate();
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.Get(Arg.Any<string>()).Returns(callInfo =>
        {
            secretGate.Pause();
            return "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"key\",\"secretKey\":null}";
        });

        var factory = new DelegatingFactory(TranslationProviderKind.OpenAICompatible, (p, c) => new TestDisposableProvider(p, c));

        using var registry = new TranslationProviderRegistry(settingsService, secretStore, [factory], _logger);

        // 第一次刷新在解析凭据时暂停在门闩
        var refreshTask1 = registry.RefreshAsync(TestContext.Current.CancellationToken);
        await secretGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 第二次刷新发起，必须在重建信号量上等待凭据解析退出
        var refreshTask2 = registry.RefreshAsync(TestContext.Current.CancellationToken);
        refreshTask2.IsCompleted.ShouldBeFalse();

        // 放行第一次凭据解析
        secretGate.Open();

        await refreshTask1;
        await refreshTask2;

        using var snap = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snap.Providers[0].Profile.Model.ShouldBe("model-a");
    }

    #endregion
}
