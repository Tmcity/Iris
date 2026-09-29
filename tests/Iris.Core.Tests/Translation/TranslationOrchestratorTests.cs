using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class TranslationOrchestratorTests
{
    private readonly LanguageCatalog _languageCatalog = new();
    private readonly ScriptLanguageDetector _languageDetector = new();
    private readonly TargetLanguageResolver _languageResolver;
    private readonly TextPreprocessor _preprocessor;
    private readonly PromptTemplateProvider _templateProvider;
    private readonly PromptRenderer _renderer;
    private readonly FakeTimeProvider _timeProvider = new();

    public TranslationOrchestratorTests()
    {
        var defaultSettings = new AppSettings();
        var settingsMock = Substitute.For<ISettingsService>();
        settingsMock.Current.Returns(defaultSettings);

        _languageResolver = new TargetLanguageResolver(settingsMock, _languageDetector, _languageCatalog);
        _preprocessor = new TextPreprocessor(settingsMock);
        _templateProvider = new PromptTemplateProvider(settingsMock, NullLogger<PromptTemplateProvider>.Instance);
        _renderer = new PromptRenderer(_languageCatalog);
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    }

    #region Test Doubles and Helpers

    private sealed class TestSnapshot : ITranslationProviderSnapshot
    {
        private readonly AppSettings _settings;
        private readonly IReadOnlyList<TranslationProviderRegistration> _providers;
        private int _disposed;

        public Action? OnDispose { get; set; }
        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        public TestSnapshot(AppSettings settings, IReadOnlyList<TranslationProviderRegistration> providers)
        {
            _settings = settings;
            _providers = providers;
        }

        public AppSettings Settings => IsDisposed
            ? throw new ObjectDisposedException(nameof(TestSnapshot))
            : _settings;

        public IReadOnlyList<TranslationProviderRegistration> Providers => IsDisposed
            ? throw new ObjectDisposedException(nameof(TestSnapshot))
            : _providers;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                OnDispose?.Invoke();
            }
        }
    }

    private sealed class TestRegistry : ITranslationProviderRegistry
    {
        public Func<CancellationToken, Task<ITranslationProviderSnapshot>>? AcquireSnapshotHandler { get; set; }
        public Func<ITranslationProviderSnapshot>? SnapshotFactory { get; set; }
        public ITranslationProviderSnapshot? CurrentSnapshot { get; set; }
        public int AcquireCount { get; private set; }
        public event EventHandler? Changed { add { } remove { } }

        public Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            if (AcquireSnapshotHandler != null)
            {
                return AcquireSnapshotHandler(cancellationToken);
            }

            if (SnapshotFactory != null)
            {
                return Task.FromResult(SnapshotFactory());
            }

            return Task.FromResult(CurrentSnapshot ?? throw new InvalidOperationException("No snapshot configured."));
        }

        public Task<ITranslationProviderSnapshot> CreateTransientAsync(
            TranslationProviderProfile profile,
            ProviderCredentials credentials,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeTranslationProvider : ITranslationProvider
    {
        public ProviderDescriptor Descriptor { get; }
        public List<ProviderTranslateRequest> Invocations { get; } = new();
        public Func<ProviderTranslateRequest, CancellationToken, IAsyncEnumerable<TranslationChunk>>? StreamHandler { get; set; }
        public int ActiveCalls => _activeCalls;
        public int MaxObservedConcurrency => _maxConcurrency;

        private int _activeCalls;
        private int _maxConcurrency;

        public FakeTranslationProvider(string id, string displayName = "Fake", string model = "fake-model", bool isConfigured = true)
        {
            Descriptor = new ProviderDescriptor(id, displayName, "OpenAICompatible", model, isConfigured);
        }

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (Invocations)
            {
                Invocations.Add(request);
            }

            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaxConcurrency(active);
            try
            {
                if (StreamHandler != null)
                {
                    await foreach (var chunk in StreamHandler(request, cancellationToken).WithCancellation(cancellationToken))
                    {
                        yield return chunk;
                    }
                }
                else
                {
                    yield return new TranslationChunk("translated text", new TokenUsage(10, 20), TranslationFinishReason.Completed);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private void UpdateMaxConcurrency(int current)
        {
            int initial, computed;
            do
            {
                initial = _maxConcurrency;
                computed = Math.Max(initial, current);
            } while (Interlocked.CompareExchange(ref _maxConcurrency, computed, initial) != initial);
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(10), "ok"));
    }

    private sealed class FakeHistoryRepository : ITranslationHistoryRepository
    {
        public List<HistoryEntry> SavedRecords { get; } = new();
        public Func<HistoryEntry, CancellationToken, Task<long>>? SaveHandler { get; set; }
        public CancellationToken LastReceivedCancellationToken { get; private set; }

        public Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
        {
            LastReceivedCancellationToken = cancellationToken;
            lock (SavedRecords)
            {
                SavedRecords.Add(entry);
            }

            if (SaveHandler != null)
            {
                return SaveHandler(entry, cancellationToken);
            }

            return Task.FromResult((long)SavedRecords.Count);
        }

        public Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<PagedResult<HistorySummary>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(bool keepFavorites, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ExplodingCache : ITranslationCache
    {
        public bool ThrowOnGet { get; set; }
        public bool ThrowOnSet { get; set; }
        public long Generation => 1;

        public bool TryGet(in TranslationCacheKey key, [NotNullWhen(true)] out string? text)
        {
            if (ThrowOnGet)
            {
                throw new InvalidOperationException("Cache get failure");
            }

            text = null;
            return false;
        }

        public bool TrySet(in TranslationCacheKey key, string text, long generation)
        {
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("Cache set failure");
            }

            return true;
        }

        public void Clear() { }
        public void Dispose() { }
    }

    private sealed class TrackingCache(ITranslationCache inner) : ITranslationCache
    {
        private readonly ITranslationCache _inner = inner;
        public int TryGetCount { get; private set; }
        public int TrySetCount { get; private set; }
        public long Generation => _inner.Generation;
        public bool TryGet(in TranslationCacheKey key, [NotNullWhen(true)] out string? text)
        {
            TryGetCount++;
            return _inner.TryGet(key, out text);
        }
        public bool TrySet(in TranslationCacheKey key, string text, long generation)
        {
            TrySetCount++;
            return _inner.TrySet(key, text, generation);
        }
        public void Clear() => _inner.Clear();
        public void Dispose() => _inner.Dispose();
    }

    private static LruTranslationCache CreateCache(AppSettings? settings = null)
    {
        var settingsMock = Substitute.For<ISettingsService>();
        settingsMock.Current.Returns(settings ?? new AppSettings());
        return new LruTranslationCache(settingsMock);
    }

    private static AppSettings CreateDefaultSettings(params TranslationProviderProfile[] profiles)
    {
        return new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = profiles,
            }
        };
    }

    private static (TranslationProviderRegistration Registration, FakeTranslationProvider Provider) CreateRegisteredProvider(
        string id,
        string displayName = "Fake",
        string model = "fake-model",
        bool enabled = true,
        bool isConfigured = true,
        int order = 1)
    {
        var profile = new TranslationProviderProfile
        {
            Id = id,
            DisplayName = displayName,
            Kind = TranslationProviderKind.OpenAICompatible,
            Model = model,
            Enabled = enabled,
            Order = order,
        };
        var provider = new FakeTranslationProvider(id, displayName, model, isConfigured);
        var reg = new TranslationProviderRegistration(profile, provider, Guid.NewGuid());
        return (reg, provider);
    }

    private TranslationOrchestrator CreateOrchestrator(
        ITranslationProviderRegistry registry,
        ITranslationCache? cache = null,
        ITranslationHistoryRepository? history = null,
        TimeProvider? timeProvider = null,
        ILogger<TranslationOrchestrator>? logger = null)
    {
        return new TranslationOrchestrator(
            registry,
            _preprocessor,
            _languageResolver,
            _templateProvider,
            _renderer,
            cache ?? CreateCache(),
            history ?? new FakeHistoryRepository(),
            timeProvider ?? _timeProvider,
            logger ?? NullLogger<TranslationOrchestrator>.Instance);
    }

    #endregion

    #region Group A: Input, Selection & Frozen Config

    [Fact]
    public void TranslateAsync_IsLazy_DoesNotInvokeDependenciesUntilFirstMoveNext()
    {
        var registry = new TestRegistry();
        var cache = new ExplodingCache { ThrowOnGet = true };
        var history = new FakeHistoryRepository();
        var orchestrator = CreateOrchestrator(registry, cache, history);

        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);

        // Calling TranslateAsync should not trigger any dependency calls
        registry.AcquireCount.ShouldBe(0);
        history.SavedRecords.Count.ShouldBe(0);

        var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        registry.AcquireCount.ShouldBe(0);
    }

    [Fact]
    public async Task TranslateAsync_WithNullRequest_ThrowsInvalidArgumentOnFirstMoveNext()
    {
        var registry = new TestRegistry();
        var orchestrator = CreateOrchestrator(registry);

        var enumerable = orchestrator.TranslateAsync(null!, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task TranslateAsync_WithEmptyRequestId_ThrowsInvalidArgumentOnFirstMoveNext()
    {
        var registry = new TestRegistry();
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { RequestId = Guid.Empty, Text = "Hello", Origin = TextOrigin.Input };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task TranslateAsync_WithInvalidOrigin_ThrowsInvalidArgumentOnFirstMoveNext()
    {
        var registry = new TestRegistry();
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { RequestId = Guid.NewGuid(), Text = "Hello", Origin = (TextOrigin)999 };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task TranslateAsync_WithNullOrBlankProviderIdInExplicitList_ThrowsInvalidArgumentOnFirstMoveNext(string? badId)
    {
        var registry = new TestRegistry();
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input, ProviderIds = ["valid", badId!] };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task TranslateAsync_WithEmptyInput_ThrowsInputEmptyOnFirstMoveNext(string text)
    {
        var (reg, _) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg.Profile);
        var snapshot = new TestSnapshot(settings, [reg]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { Text = text, Origin = TextOrigin.Input };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.InputEmpty);
        registry.AcquireCount.ShouldBe(1);
    }

    [Fact]
    public async Task TranslateAsync_WithInputExceedingMaxInputChars_ThrowsInputTooLongOnFirstMoveNext()
    {
        var (reg, _) = CreateRegisteredProvider("prov-1");
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = [reg.Profile],
                MaxInputChars = 10,
            }
        };
        var snapshot = new TestSnapshot(settings, [reg]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { Text = "This text is definitely longer than ten chars", Origin = TextOrigin.Input };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.InputTooLong);
    }

    [Fact]
    public async Task TranslateAsync_WithNullProviderIds_SelectsConfiguredEnabledProvidersInRegistryOrder()
    {
        var (reg1, _) = CreateRegisteredProvider("prov-1", "P1", isConfigured: true, order: 1);
        var (reg2, _) = CreateRegisteredProvider("prov-2", "P2", isConfigured: false, order: 2);
        var (reg3, _) = CreateRegisteredProvider("prov-3", "P3", isConfigured: true, order: 3);
        var settings = CreateDefaultSettings(reg1.Profile, reg2.Profile, reg3.Profile);
        var snapshot = new TestSnapshot(settings, [reg1, reg2, reg3]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input, ProviderIds = null };
        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var started = events.OfType<TranslationStarted>().Single();
        started.Providers.Count.ShouldBe(2);
        started.Providers[0].Id.ShouldBe("prov-1");
        started.Providers[1].Id.ShouldBe("prov-3");
    }

    [Fact]
    public async Task TranslateAsync_WithNullProviderIds_WhenNoConfiguredProviders_ThrowsNotConfiguredOnFirstMoveNext()
    {
        var (reg1, _) = CreateRegisteredProvider("prov-1", isConfigured: false);
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input, ProviderIds = null };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.NotConfigured);
    }

    [Fact]
    public async Task TranslateAsync_WithEmptyProviderIds_ThrowsNotConfiguredOnFirstMoveNext()
    {
        var (reg1, _) = CreateRegisteredProvider("prov-1", isConfigured: true);
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var orchestrator = CreateOrchestrator(registry);

        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input, ProviderIds = [] };
        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.NotConfigured);
    }

    [Fact]
    public async Task TranslateAsync_WithExplicitProviderIds_PreservesFirstOccurrenceOrder_AndOrdinalExactDeduplication()
    {
        var (regA, _) = CreateRegisteredProvider("Prov-B");
        var (regB, _) = CreateRegisteredProvider("prov-b");
        var (regC, _) = CreateRegisteredProvider("Prov-A");
        var settings = CreateDefaultSettings(regA.Profile, regB.Profile, regC.Profile);
        var snapshot = new TestSnapshot(settings, [regA, regB, regC]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var orchestrator = CreateOrchestrator(registry);

        // "Prov-B" appears first, then "prov-b" (distinct ordinal), then "Prov-A", then duplicate "Prov-B"
        var request = new TranslationRequest
        {
            Text = "Hello world",
            Origin = TextOrigin.Input,
            ProviderIds = ["Prov-B", "prov-b", "Prov-A", "Prov-B"],
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var started = events.OfType<TranslationStarted>().Single();
        started.Providers.Select(p => p.Id).ShouldBe(["Prov-B", "prov-b", "Prov-A"]);
    }

    [Fact]
    public async Task TranslateAsync_WithMissingDisabledOrUnconfiguredExplicitIds_GeneratesFailedCards_WithoutCallingProviderOrCache()
    {
        var (validReg, validProv) = CreateRegisteredProvider("valid-id", displayName: "Valid Provider", isConfigured: true);
        var (unconfReg, unconfProv) = CreateRegisteredProvider("unconf-id", displayName: "Unconfigured Provider", isConfigured: false);

        var disabledProfile = new TranslationProviderProfile
        {
            Id = "disabled-id",
            DisplayName = "Disabled Provider",
            Kind = TranslationProviderKind.OpenAICompatible,
            Model = "gpt-model",
            Enabled = false,
        };

        var settings = CreateDefaultSettings(validReg.Profile, unconfReg.Profile, disabledProfile);
        var snapshot = new TestSnapshot(settings, [validReg, unconfReg]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = CreateCache(settings);
        var orchestrator = CreateOrchestrator(registry, cache);

        var request = new TranslationRequest
        {
            Text = "Hello world",
            Origin = TextOrigin.Input,
            ProviderIds = ["missing-id", "disabled-id", "unconf-id", "valid-id"],
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var started = events.OfType<TranslationStarted>().Single();
        started.Providers.Count.ShouldBe(4);

        // Check descriptors
        started.Providers[0].Id.ShouldBe("missing-id");
        started.Providers[0].DisplayName.ShouldBe("missing-id");
        started.Providers[0].KindName.ShouldBe("Unknown");
        started.Providers[0].IsConfigured.ShouldBeFalse();

        started.Providers[1].Id.ShouldBe("disabled-id");
        started.Providers[1].DisplayName.ShouldBe("Disabled Provider");
        started.Providers[1].Model.ShouldBe("gpt-model");
        started.Providers[1].IsConfigured.ShouldBeFalse();

        started.Providers[2].Id.ShouldBe("unconf-id");
        started.Providers[2].DisplayName.ShouldBe("Unconfigured Provider");
        started.Providers[2].IsConfigured.ShouldBeFalse();

        started.Providers[3].Id.ShouldBe("valid-id");

        // Failures
        var failures = events.OfType<ProviderFailed>().ToList();
        failures.Count.ShouldBe(3);
        failures.Select(f => f.ProviderId).ShouldBe(["missing-id", "disabled-id", "unconf-id"], ignoreOrder: true);
        foreach (var f in failures)
        {
            f.Error.Code.ShouldBe(ErrorCode.NotConfigured);
        }

        // Unconfigured provider was never invoked
        unconfProv.Invocations.Count.ShouldBe(0);
        validProv.Invocations.Count.ShouldBe(1);

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(1);
        finished.FailedCount.ShouldBe(3);
    }

    [Fact]
    public async Task TranslateAsync_ModifyingProviderIdsListAfterFirstMoveNext_DoesNotAffectCapturedSelection()
    {
        var (reg1, _) = CreateRegisteredProvider("prov-1");
        var (reg2, _) = CreateRegisteredProvider("prov-2");
        var (reg3, _) = CreateRegisteredProvider("prov-3");
        var settings = CreateDefaultSettings(reg1.Profile, reg2.Profile, reg3.Profile);
        var snapshot = new TestSnapshot(settings, [reg1, reg2, reg3]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var orchestrator = CreateOrchestrator(registry);

        var externalList = new List<string> { "prov-1", "prov-2" };
        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input, ProviderIds = externalList };

        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        var started = (TranslationStarted)enumerator.Current;
        started.Providers.Select(p => p.Id).ShouldBe(["prov-1", "prov-2"]);

        // Mutate original list
        externalList.Clear();
        externalList.Add("prov-3");

        var events = new List<TranslationEvent> { started };
        while (await enumerator.MoveNextAsync())
        {
            events.Add(enumerator.Current);
        }

        var completed = events.OfType<ProviderCompleted>().Select(c => c.ProviderId).ToList();
        completed.ShouldBe(["prov-1", "prov-2"], ignoreOrder: true);
        events.OfType<TranslationFinished>().Single().SucceededCount.ShouldBe(2);
    }

    [Fact]
    public async Task TranslateAsync_CacheClearDuringRegistryAcquisition_RejectsStaleGenerationWrite()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var rawCache = CreateCache(settings);
        var cache = new TrackingCache(rawCache);

        var pauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAcquisition = true;

        var registry = new TestRegistry
        {
            AcquireSnapshotHandler = async ct =>
            {
                if (firstAcquisition)
                {
                    firstAcquisition = false;
                    pauseGate.TrySetResult();
                    await resumeGate.Task.WaitAsync(ct);
                }
                return new TestSnapshot(settings, [reg1]);
            }
        };

        var orchestrator = CreateOrchestrator(registry, cache);
        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input };

        var readTask = Task.Run(async () =>
        {
            var list = new List<TranslationEvent>();
            await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
            {
                list.Add(evt);
            }
            return list;
        }, TestContext.Current.CancellationToken);

        await pauseGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Clear cache while orchestrator is waiting for registry snapshot!
        var genBefore = cache.Generation;
        cache.Clear();
        var genAfter = cache.Generation;
        genAfter.ShouldBeGreaterThan(genBefore);

        resumeGate.SetResult();
        var events = await readTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        events.OfType<TranslationFinished>().Single().SucceededCount.ShouldBe(1);

        // Verify that subsequent request still calls provider because stale generation write was rejected
        var secondEvents = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            secondEvents.Add(evt);
        }

        prov1.Invocations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TranslateAsync_UsesFrozenSnapshotSettings_IgnoresSubsequentGlobalSettingsChanges()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1", model: "model-v1");
        var settingsA = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = [reg1.Profile],
                DefaultPromptTemplateId = "custom-template",
                MaxConcurrentProviders = 1,
                CustomPrompts =
                [
                    new PromptTemplate
                    {
                        Id = "custom-template",
                        Name = "Template A",
                        System = "System Template A",
                        User = "Translate A: {{text}}",
                    }
                ]
            }
        };

        var snapshotA = new TestSnapshot(settingsA, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshotA };
        var cache = CreateCache(settingsA);
        var orchestrator = CreateOrchestrator(registry, cache);

        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input };
        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        prov1.Invocations.Count.ShouldBe(1);
        var inv = prov1.Invocations[0];
        inv.PreparedPrompt.ShouldNotBeNull();
        inv.PreparedPrompt.System.ShouldBe("System Template A");
        inv.PreparedPrompt.User.ShouldBe("Translate A: Hello world");

        // Verify cached key uses Template A's prepared prompt
        var keyA = TranslationCacheKeyFactory.Create(reg1, inv);
        cache.TryGet(keyA, out var text).ShouldBeTrue();
        text.ShouldBe("translated text");
    }

    #endregion

    #region Group B: Multi-Provider & Events

    [Fact]
    public async Task TranslateAsync_LatchControlledInterleaving_MaintainsCorrectAttributionAndEventSequencing()
    {
        var (regA, provA) = CreateRegisteredProvider("prov-a");
        var (regB, provB) = CreateRegisteredProvider("prov-b");
        var settings = CreateDefaultSettings(regA.Profile, regB.Profile);
        var snapshot = new TestSnapshot(settings, [regA, regB]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        var aStartedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bYieldedDeltaGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aFinishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        provA.StreamHandler = (req, ct) => StreamA(aStartedGate, bYieldedDeltaGate, aFinishGate, ct);
        provB.StreamHandler = (req, ct) => StreamB(aStartedGate, bYieldedDeltaGate, aFinishGate, ct);

        static async IAsyncEnumerable<TranslationChunk> StreamA(
            TaskCompletionSource aStartedGate,
            TaskCompletionSource bYieldedDeltaGate,
            TaskCompletionSource aFinishGate,
            [EnumeratorCancellation] CancellationToken ct)
        {
            aStartedGate.SetResult();
            await bYieldedDeltaGate.Task.WaitAsync(ct);
            yield return new TranslationChunk("A-1 ");
            await aFinishGate.Task.WaitAsync(ct);
            yield return new TranslationChunk("A-2", new TokenUsage(5, 5), TranslationFinishReason.Completed);
        }

        static async IAsyncEnumerable<TranslationChunk> StreamB(
            TaskCompletionSource aStartedGate,
            TaskCompletionSource bYieldedDeltaGate,
            TaskCompletionSource aFinishGate,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await aStartedGate.Task.WaitAsync(ct);
            yield return new TranslationChunk("B-1 ");
            bYieldedDeltaGate.SetResult();
            yield return new TranslationChunk("B-2", new TokenUsage(10, 10), TranslationFinishReason.Completed);
            aFinishGate.SetResult();
        }

        var orchestrator = CreateOrchestrator(registry);
        var reqId = Guid.NewGuid();
        var request = new TranslationRequest
        {
            RequestId = reqId,
            Text = "Hello",
            Origin = TextOrigin.Input,
            ProviderIds = ["prov-a", "prov-b"],
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        events.First().ShouldBeOfType<TranslationStarted>();
        events.Last().ShouldBeOfType<TranslationFinished>();

        foreach (var evt in events)
        {
            evt.RequestId.ShouldBe(reqId);
        }

        var completedA = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-a");
        var completedB = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-b");
        completedA.Text.ShouldBe("A-1 A-2");
        completedB.Text.ShouldBe("B-1 B-2");

        var finished = (TranslationFinished)events.Last();
        finished.SucceededCount.ShouldBe(2);
        finished.FailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task TranslateAsync_ConcurrencyLimit_EnforcesMaxConcurrentProviders()
    {
        var (reg1, p1) = CreateRegisteredProvider("p1");
        var (reg2, p2) = CreateRegisteredProvider("p2");
        var (reg3, p3) = CreateRegisteredProvider("p3");
        var (reg4, p4) = CreateRegisteredProvider("p4");

        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = [reg1.Profile, reg2.Profile, reg3.Profile, reg4.Profile],
                MaxConcurrentProviders = 2,
            }
        };

        var snapshot = new TestSnapshot(settings, [reg1, reg2, reg3, reg4]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        var allProviders = new[] { p1, p2, p3, p4 };
        var activeCount = 0;
        var maxActive = 0;

        foreach (var p in allProviders)
        {
            var providerId = p.Descriptor.Id;
            p.StreamHandler = (req, ct) => ExecuteWithConcurrencyTracking(providerId, ct);
        }

        async IAsyncEnumerable<TranslationChunk> ExecuteWithConcurrencyTracking(string providerId, [EnumeratorCancellation] CancellationToken ct)
        {
            var count = Interlocked.Increment(ref activeCount);
            int curMax, newMax;
            do
            {
                curMax = maxActive;
                newMax = Math.Max(curMax, count);
            } while (Interlocked.CompareExchange(ref maxActive, newMax, curMax) != curMax);

            await Task.Delay(20, ct);

            if (providerId == "p2")
            {
                Interlocked.Decrement(ref activeCount);
                throw new IrisException(ErrorCode.NetworkUnavailable, "Simulated network failure");
            }

            Interlocked.Decrement(ref activeCount);
            yield return new TranslationChunk($"Text from {providerId}", new TokenUsage(1, 1), TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest
        {
            Text = "Hello world",
            Origin = TextOrigin.Input,
            ProviderIds = ["p1", "p2", "p3", "p4"],
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        maxActive.ShouldBeLessThanOrEqualTo(2);

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(3);
        finished.FailedCount.ShouldBe(1);

        var failed = events.OfType<ProviderFailed>().Single();
        failed.ProviderId.ShouldBe("p2");
        failed.Error.Code.ShouldBe(ErrorCode.NetworkUnavailable);
    }

    [Fact]
    public async Task TranslateAsync_CacheHit_OutputsCompletedWithoutDeltas_AndDoesNotInvokeProvider()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1", model: "model-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var registry = new TestRegistry { SnapshotFactory = () => new TestSnapshot(settings, [reg1]) };
        var cache = CreateCache(settings);

        var orchestrator = CreateOrchestrator(registry, cache);
        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input };

        // First run populates the cache
        var initialEvents = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            initialEvents.Add(evt);
        }

        prov1.Invocations.Count.ShouldBe(1);

        // Second run should hit cache
        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        // Provider is NOT called again
        prov1.Invocations.Count.ShouldBe(1);

        events.OfType<ProviderDelta>().ShouldBeEmpty();
        var completed = events.OfType<ProviderCompleted>().Single();
        completed.FromCache.ShouldBeTrue();
        completed.Truncated.ShouldBeFalse();
        completed.Usage.ShouldBeNull();
        completed.Elapsed.ShouldBe(TimeSpan.Zero);
        completed.Text.ShouldBe("translated text");
    }

    [Fact]
    public async Task TranslateAsync_BypassCache_SkipsReading_CallsProvider_AndUpdatesCache()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1", model: "model-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var registry = new TestRegistry { SnapshotFactory = () => new TestSnapshot(settings, [reg1]) };
        var cache = CreateCache(settings);

        var orchestrator = CreateOrchestrator(registry, cache);
        var initialReq = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input };

        // Initial run caches "translated text"
        await foreach (var _ in orchestrator.TranslateAsync(initialReq, TestContext.Current.CancellationToken)) { }
        prov1.Invocations.Count.ShouldBe(1);

        // Set provider to return fresh translation
        prov1.StreamHandler = (req, ct) => FreshStream();
        static async IAsyncEnumerable<TranslationChunk> FreshStream()
        {
            await Task.Yield();
            yield return new TranslationChunk("Fresh Translation", new TokenUsage(5, 5), TranslationFinishReason.Completed);
        }

        var bypassReq = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input, BypassCache = true };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(bypassReq, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        prov1.Invocations.Count.ShouldBe(2);
        var completed = events.OfType<ProviderCompleted>().Single();
        completed.FromCache.ShouldBeFalse();
        completed.Text.ShouldBe("Fresh Translation");

        // Third run with cache enabled should hit cache with the newly updated value
        var thirdEvents = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(initialReq, TestContext.Current.CancellationToken))
        {
            thirdEvents.Add(evt);
        }

        prov1.Invocations.Count.ShouldBe(2); // no third invocation
        thirdEvents.OfType<ProviderCompleted>().Single().Text.ShouldBe("Fresh Translation");
    }

    [Fact]
    public async Task TranslateAsync_CacheReadFailure_FallsBackToProvider_AndCacheWriteFailure_DoesNotFailTranslation()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = new ExplodingCache { ThrowOnGet = true, ThrowOnSet = true };

        var orchestrator = CreateOrchestrator(registry, cache);
        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        prov1.Invocations.Count.ShouldBe(1);
        var completed = events.OfType<ProviderCompleted>().Single();
        completed.Text.ShouldBe("translated text");

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(1);
        finished.FailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_FinalChunkDelta_IsAppendedAndEmitted()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => StreamWithFinalChunk();
        static async IAsyncEnumerable<TranslationChunk> StreamWithFinalChunk()
        {
            await Task.Yield();
            yield return new TranslationChunk("First ");
            yield return new TranslationChunk("Second", new TokenUsage(10, 10), TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var deltas = events.OfType<ProviderDelta>().Select(d => d.Delta).ToList();
        deltas.ShouldBe(["First ", "Second"]);

        var completed = events.OfType<ProviderCompleted>().Single();
        completed.Text.ShouldBe("First Second");
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_DispersedUsage_IsCombined()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => StreamWithDispersedUsage();
        static async IAsyncEnumerable<TranslationChunk> StreamWithDispersedUsage()
        {
            await Task.Yield();
            yield return new TranslationChunk("Part 1", new TokenUsage(15, null));
            yield return new TranslationChunk("Part 2", new TokenUsage(null, 40), TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var completed = events.OfType<ProviderCompleted>().Single();
        completed.Usage.ShouldNotBeNull();
        completed.Usage.InputTokens.ShouldBe(15);
        completed.Usage.OutputTokens.ShouldBe(40);
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_TruncatedFinishReason_MarksTruncated_DoesNotWriteCache_KeepsInHistory()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1", model: "model-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = CreateCache(settings);
        var history = new FakeHistoryRepository();

        prov1.StreamHandler = (req, ct) => StreamTruncated();
        static async IAsyncEnumerable<TranslationChunk> StreamTruncated()
        {
            await Task.Yield();
            yield return new TranslationChunk("Partial result", new TokenUsage(10, 50), TranslationFinishReason.Truncated);
        }

        var orchestrator = CreateOrchestrator(registry, cache, history);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input, RecordHistory = true };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var completed = events.OfType<ProviderCompleted>().Single();
        completed.Truncated.ShouldBeTrue();

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(1);

        // Cache must NOT store truncated output
        var provReq = new ProviderTranslateRequest("Hello", "en", "zh-Hans")
        {
            PreparedPrompt = new RenderedPrompt(BuiltInPrompts.Default.System, "Hello")
        };
        var key = TranslationCacheKeyFactory.Create(reg1, provReq);
        cache.TryGet(key, out _).ShouldBeFalse();

        // History must record truncated output with Truncated = true
        history.SavedRecords.Count.ShouldBe(1);
        history.SavedRecords[0].Results.Single().Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_EmptyStream_FailsWithInvalidResponse()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => AsyncEnumerable.Empty<TranslationChunk>();

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        failed.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_MissingFinishReason_FailsWithInvalidResponse()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => StreamNoFinish();
        static async IAsyncEnumerable<TranslationChunk> StreamNoFinish()
        {
            await Task.Yield();
            yield return new TranslationChunk("Some text without finish reason");
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        failed.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_DuplicateFinishReason_FailsWithInvalidResponse()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => StreamDuplicateFinish();
        static async IAsyncEnumerable<TranslationChunk> StreamDuplicateFinish()
        {
            await Task.Yield();
            yield return new TranslationChunk("Text 1", FinishReason: TranslationFinishReason.Completed);
            yield return new TranslationChunk("Text 2", FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        failed.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_ChunkAfterFinishReason_FailsWithInvalidResponse()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => StreamTrailingChunk();
        static async IAsyncEnumerable<TranslationChunk> StreamTrailingChunk()
        {
            await Task.Yield();
            yield return new TranslationChunk("Text 1", FinishReason: TranslationFinishReason.Completed);
            yield return new TranslationChunk("Trailing chunk");
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        failed.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task TranslateAsync_StreamValidation_NullDelta_FailsWithInvalidResponse()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => StreamNullDelta();
        static async IAsyncEnumerable<TranslationChunk> StreamNullDelta()
        {
            await Task.Yield();
            yield return new TranslationChunk(null!, FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        failed.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t ")]
    public async Task TranslateAsync_StreamValidation_BlankAccumulatedText_FailsWithInvalidResponse(string blankText)
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => StreamBlank(blankText);
        static async IAsyncEnumerable<TranslationChunk> StreamBlank(string blank)
        {
            await Task.Yield();
            yield return new TranslationChunk(blank, FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        failed.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task TranslateAsync_ExceptionSanitization_HidesFakeSecrets_AndNormalizesProviderId()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-target");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) =>
        {
            // Throw IrisException that mistakenly reports a different providerId and inner sensitive info
            var inner = new InvalidOperationException("LEAK_SECRET_KEY_987654321");
            throw new IrisException(ErrorCode.RateLimited, "Rate limit reached", inner, providerId: "wrong-provider-id", detail: "safe-detail");
        };

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        // ProviderId must be normalized to prov-target!
        failed.ProviderId.ShouldBe("prov-target");
        failed.Error.Code.ShouldBe(ErrorCode.RateLimited);
        failed.Error.Detail.ShouldBe("safe-detail");
        failed.Error.Detail!.ShouldNotContain("LEAK_SECRET_KEY_987654321");
        failed.Error.Detail!.ShouldNotContain("wrong-provider-id");
    }

    [Fact]
    public async Task TranslateAsync_UnknownExceptionSanitization_HidesFakeSecrets_AndNormalizesProviderId()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-target");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) =>
        {
            throw new InvalidOperationException("API_KEY=sk-fake-secret-9999");
        };

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = events.OfType<ProviderFailed>().Single();
        failed.ProviderId.ShouldBe("prov-target");
        failed.Error.Code.ShouldBe(ErrorCode.Unknown);
        failed.Error.Detail.ShouldBe("InvalidOperationException");
        failed.Error.Detail!.ShouldNotContain("sk-fake-secret-9999");
    }

    [Fact]
    public async Task TranslateAsync_ConcurrentRequestsOnSameOrchestrator_AreCompletelyIsolated()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        prov1.StreamHandler = (req, ct) => DelayStream(req.Text, ct);
        static async IAsyncEnumerable<TranslationChunk> DelayStream(string text, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(10, ct);
            yield return new TranslationChunk($"Response to: {text}", FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var req1 = new TranslationRequest { RequestId = id1, Text = "Request One", Origin = TextOrigin.Input };
        var req2 = new TranslationRequest { RequestId = id2, Text = "Request Two", Origin = TextOrigin.Input };

        var task1 = Task.Run(async () =>
        {
            var list = new List<TranslationEvent>();
            await foreach (var evt in orchestrator.TranslateAsync(req1, TestContext.Current.CancellationToken))
            {
                list.Add(evt);
            }
            return list;
        }, TestContext.Current.CancellationToken);

        var task2 = Task.Run(async () =>
        {
            var list = new List<TranslationEvent>();
            await foreach (var evt in orchestrator.TranslateAsync(req2, TestContext.Current.CancellationToken))
            {
                list.Add(evt);
            }
            return list;
        }, TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(task1, task2);
        var events1 = results[0];
        var events2 = results[1];

        events1.All(e => e.RequestId == id1).ShouldBeTrue();
        events2.All(e => e.RequestId == id2).ShouldBeTrue();

        events1.OfType<ProviderCompleted>().Single().Text.ShouldBe("Response to: Request One");
        events2.OfType<ProviderCompleted>().Single().Text.ShouldBe("Response to: Request Two");
    }

    #endregion

    #region Group C: Cancellation, Resources & Bounded Output

    [Fact]
    public async Task TranslateAsync_EnumeratorCancellation_CancelsAllSubtasks()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        var streamStartedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamCancelledGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        prov1.StreamHandler = (req, ct) => NeverEndingStream(streamStartedGate, streamCancelledGate, ct);
        static async IAsyncEnumerable<TranslationChunk> NeverEndingStream(
            TaskCompletionSource streamStartedGate,
            TaskCompletionSource streamCancelledGate,
            [EnumeratorCancellation] CancellationToken ct)
        {
            streamStartedGate.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                streamCancelledGate.SetResult();
                throw;
            }
            yield return new TranslationChunk("Never reached");
        }

        var orchestrator = CreateOrchestrator(registry);
        using var cts = new CancellationTokenSource();
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.WithCancellation(cts.Token).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue(); // TranslationStarted
        (await enumerator.MoveNextAsync()).ShouldBeTrue(); // ProviderStarted

        await streamStartedGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Cancel enumerator token
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await streamCancelledGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TranslateAsync_EarlyConsumerBreak_CancelsWorkersAndReleasesLease_DoesNotWriteHistoryOrCache()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1", model: "model-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = new TrackingCache(CreateCache(settings));
        var history = new FakeHistoryRepository();

        var workerCleanedUpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseDisposedAfterWorkerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        prov1.StreamHandler = (req, ct) => StreamWithCleanup(workerCleanedUpGate, ct);
        static async IAsyncEnumerable<TranslationChunk> StreamWithCleanup(
            TaskCompletionSource workerCleanedUpGate,
            [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                yield return new TranslationChunk("Delta 1 ");
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                workerCleanedUpGate.SetResult();
            }
        }

        snapshot.OnDispose = () =>
        {
            // Verify that the worker has already finished/cleaned up when the lease is disposed!
            leaseDisposedAfterWorkerGate.TrySetResult(workerCleanedUpGate.Task.IsCompleted);
        };

        var orchestrator = CreateOrchestrator(registry, cache, history);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input, RecordHistory = true };

        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            if (evt is ProviderDelta)
            {
                // Break early after receiving first Delta!
                break;
            }
        }

        var leaseReleasedAfterWorker = await leaseDisposedAfterWorkerGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        leaseReleasedAfterWorker.ShouldBeTrue("Lease must not be disposed prematurely before workers exit.");

        // Neither cache nor history should have been written
        cache.TrySetCount.ShouldBe(0);
        history.SavedRecords.Count.ShouldBe(0);
    }

    [Fact]
    public async Task TranslateAsync_SlowConsumer_EnforcesBackpressureOnChannel()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        var generatedCount = 0;
        var reachedWaitThreshold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        prov1.StreamHandler = (req, ct) => BurstStream();
        async IAsyncEnumerable<TranslationChunk> BurstStream()
        {
            for (int i = 0; i < 200; i++)
            {
                Interlocked.Increment(ref generatedCount);
                if (generatedCount > 135)
                {
                    reachedWaitThreshold.TrySetResult();
                }
                yield return new TranslationChunk($"chunk-{i} ");
            }
            yield return new TranslationChunk("final", FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var enumerable = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken);
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Read TranslationStarted and ProviderStarted
        (await enumerator.MoveNextAsync()).ShouldBeTrue(); // TranslationStarted
        (await enumerator.MoveNextAsync()).ShouldBeTrue(); // ProviderStarted

        // Wait a bit: provider will produce until channel capacity (128) is full and block on WriteAsync
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Due to channel capacity 128, the producer will have blocked and not immediately finished all 200
        var countAtPause = Volatile.Read(ref generatedCount);
        countAtPause.ShouldBeLessThan(200);

        // Now consume everything
        var totalEvents = 2; // already read 2
        TranslationEvent? lastEvent = null;
        while (await enumerator.MoveNextAsync())
        {
            totalEvents++;
            lastEvent = enumerator.Current;
        }

        totalEvents.ShouldBeGreaterThan(200);
        lastEvent.ShouldNotBeNull();
        lastEvent.ShouldBeOfType<TranslationFinished>();
    }

    [Fact]
    public async Task TranslateAsync_DeltaSplitting_SplitsChunksExceeding4096Units_PreservingSurrogates()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        // Construct 5000 chars with surrogate pairs near 4096 boundary
        // 4095 ASCII chars + 1 emoji (2 UTF-16 code units: \uD83D\uDE00) + 903 ASCII chars = 5000 code units
        var sb = new StringBuilder();
        sb.Append('A', 4095);
        sb.Append("\uD83D\uDE00"); // High surrogate at index 4095!
        sb.Append('B', 903);
        var bigString = sb.ToString();
        bigString.Length.ShouldBe(5000);

        prov1.StreamHandler = (req, ct) => BigStringStream(bigString);
        static async IAsyncEnumerable<TranslationChunk> BigStringStream(string str)
        {
            await Task.Yield();
            yield return new TranslationChunk(str, FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var deltas = events.OfType<ProviderDelta>().ToList();
        // Since high surrogate was at 4095, the first chunk should be split at 4095 (not cutting surrogate pair)
        deltas.Count.ShouldBe(2);
        deltas[0].Delta.Length.ShouldBe(4095);
        deltas[0].Delta.ShouldBe(new string('A', 4095));

        deltas[1].Delta.Length.ShouldBe(905);
        deltas[1].Delta.StartsWith("\uD83D\uDE00", StringComparison.Ordinal).ShouldBeTrue();

        var completed = events.OfType<ProviderCompleted>().Single();
        completed.Text.ShouldBe(bigString);
    }

    [Fact]
    public async Task TranslateAsync_ProviderTextBudget_ExceedingLimit_FailsProvider_OtherProvidersContinue()
    {
        var (regA, provA) = CreateRegisteredProvider("prov-a");
        var (regB, provB) = CreateRegisteredProvider("prov-b");
        var settings = CreateDefaultSettings(regA.Profile, regB.Profile);
        var snapshot = new TestSnapshot(settings, [regA, regB]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        // Provider A yields more than 1,048,576 UTF-16 code units
        provA.StreamHandler = (req, ct) => HugeStream();
        static async IAsyncEnumerable<TranslationChunk> HugeStream()
        {
            await Task.Yield();
            var chunk4000 = new string('A', 4000);
            for (int i = 0; i < 265; i++) // 265 * 4000 = 1,060,000 > 1,048,576
            {
                yield return new TranslationChunk(chunk4000);
            }
            yield return new TranslationChunk("end", FinishReason: TranslationFinishReason.Completed);
        }

        provB.StreamHandler = (req, ct) => NormalStream();
        static async IAsyncEnumerable<TranslationChunk> NormalStream()
        {
            await Task.Yield();
            yield return new TranslationChunk("Normal result B", FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest
        {
            Text = "Hello",
            Origin = TextOrigin.Input,
            ProviderIds = ["prov-a", "prov-b"],
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failedA = events.OfType<ProviderFailed>().Single(f => f.ProviderId == "prov-a");
        failedA.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
        failedA.Error.Message.ShouldBe("翻译结果超过本地长度限制");

        var completedB = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-b");
        completedB.Text.ShouldBe("Normal result B");

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(1);
        finished.FailedCount.ShouldBe(1);
    }

    [Fact]
    public async Task TranslateAsync_LateCancellationAfterCompletionPhase_StillYieldsTranslationFinished()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var history = new FakeHistoryRepository();

        using var cts = new CancellationTokenSource();

        history.SaveHandler = async (record, ct) =>
        {
            // Now that completion phase has started, cancel the user's cancellation token!
            cts.Cancel();
            await Task.Yield();
            return 1L;
        };

        var orchestrator = CreateOrchestrator(registry, history: history);
        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input, RecordHistory = true };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, cts.Token))
        {
            events.Add(evt);
        }

        events.Last().ShouldBeOfType<TranslationFinished>();
        history.SavedRecords.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TranslateAsync_HistoryTimeout_CancelsHistoryTokenAfter5Seconds_AndLogsWarning_StillFinishes()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var history = new FakeHistoryRepository();
        var fakeTime = new FakeTimeProvider();

        var historyCanceledGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        history.SaveHandler = async (record, ct) =>
        {
            // Advance time by 5 seconds to trigger the 5s history timeout
            fakeTime.Advance(TimeSpan.FromSeconds(5));
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                historyCanceledGate.SetResult();
                throw;
            }
            return 1L;
        };

        var orchestrator = CreateOrchestrator(registry, history: history, timeProvider: fakeTime);
        var request = new TranslationRequest { Text = "Hello world", Origin = TextOrigin.Input, RecordHistory = true };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        await historyCanceledGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Translation finished should still be yielded normally despite history timeout
        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(1);
        finished.FailedCount.ShouldBe(0);
    }

    #endregion

    #region T-013-R1 Regression Tests

    private sealed class CustomDisposeException : Exception
    {
        public CustomDisposeException(string message) : base(message) { }
    }

    private sealed class DirectProvider : ITranslationProvider
    {
        private readonly Func<ProviderTranslateRequest, CancellationToken, IAsyncEnumerable<TranslationChunk>> _streamFactory;
        public ProviderDescriptor Descriptor { get; }
        public List<ProviderTranslateRequest> Invocations { get; } = new();

        public DirectProvider(string id, Func<ProviderTranslateRequest, CancellationToken, IAsyncEnumerable<TranslationChunk>> streamFactory)
        {
            Descriptor = new ProviderDescriptor(id, id, "OpenAICompatible", "fake-model", true);
            _streamFactory = streamFactory;
        }

        public IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(ProviderTranslateRequest request, CancellationToken cancellationToken)
        {
            lock (Invocations)
            {
                Invocations.Add(request);
            }
            return _streamFactory(request, cancellationToken);
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.Zero, "ok"));
    }

    private static (TranslationProviderRegistration Registration, DirectProvider Provider) CreateDirectProvider(
        string id,
        Func<ProviderTranslateRequest, CancellationToken, IAsyncEnumerable<TranslationChunk>> streamFactory)
    {
        var profile = new TranslationProviderProfile
        {
            Id = id,
            DisplayName = id,
            Kind = TranslationProviderKind.OpenAICompatible,
            Model = "fake-model",
            Enabled = true,
            Order = 1,
        };
        var provider = new DirectProvider(id, streamFactory);
        var reg = new TranslationProviderRegistration(profile, provider, Guid.NewGuid());
        return (reg, provider);
    }

    private sealed class DisposableFailingEnumerable : IAsyncEnumerable<TranslationChunk>, IAsyncEnumerator<TranslationChunk>
    {
        private int _index;
        private readonly TranslationChunk[] _chunks =
        [
            new TranslationChunk("Result text ", new TokenUsage(5, 10), null),
            new TranslationChunk("final", null, TranslationFinishReason.Completed)
        ];

        public IAsyncEnumerator<TranslationChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_index < _chunks.Length)
            {
                Current = _chunks[_index++];
                return ValueTask.FromResult(true);
            }
            return ValueTask.FromResult(false);
        }

        public TranslationChunk Current { get; private set; } = default!;

        public ValueTask DisposeAsync()
        {
            throw new CustomDisposeException("Enumerator dispose failed");
        }
    }

    private sealed class FailingStreamWithDisposeError : IAsyncEnumerable<TranslationChunk>, IAsyncEnumerator<TranslationChunk>
    {
        public IAsyncEnumerator<TranslationChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            throw new IrisException(ErrorCode.Timeout, "Stream read timed out.");
        }

        public TranslationChunk Current => default!;

        public ValueTask DisposeAsync()
        {
            throw new CustomDisposeException("Enumerator dispose failed");
        }
    }

    [Fact]
    public async Task TranslateAsync_EarlyConsumerBreak_WithQueuedProviderAndCleanupLatch_PreservesSemaphoreAndLeaseUntilLatchReleased()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1", model: "model-1");
        var (reg2, prov2) = CreateRegisteredProvider("prov-2", model: "model-2");
        var settings = CreateDefaultSettings(reg1.Profile, reg2.Profile) with
        {
            Translation = new TranslationSettings
            {
                Providers = [reg1.Profile, reg2.Profile],
                MaxConcurrentProviders = 1
            }
        };

        var snapshot = new TestSnapshot(settings, [reg1, reg2]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = new TrackingCache(CreateCache(settings));
        var history = new FakeHistoryRepository();

        var cleanupEnteredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupReleaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        prov1.StreamHandler = (req, ct) => StreamWithCleanupLatch(cleanupEnteredGate, cleanupReleaseGate, ct);
        static async IAsyncEnumerable<TranslationChunk> StreamWithCleanupLatch(
            TaskCompletionSource enteredGate,
            TaskCompletionSource releaseGate,
            [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                yield return new TranslationChunk("Delta 1 ");
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                enteredGate.SetResult();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await releaseGate.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        var orchestrator = CreateOrchestrator(registry, cache, history);
        var request = new TranslationRequest
        {
            Text = "Hello world",
            Origin = TextOrigin.Input,
            ProviderIds = ["prov-1", "prov-2"],
            RecordHistory = true
        };

        var enumerator = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            enumerator.Current.ShouldBeOfType<TranslationStarted>();

            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            enumerator.Current.ShouldBeOfType<ProviderStarted>();

            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            enumerator.Current.ShouldBeOfType<ProviderDelta>();

            var disposeTask = enumerator.DisposeAsync().AsTask();

            await cleanupEnteredGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // While cleanup latch is pending:
            disposeTask.IsCompleted.ShouldBeFalse("DisposeAsync must still wait for worker cleanup.");
            snapshot.IsDisposed.ShouldBeFalse("Lease must not be disposed while worker cleanup is pending.");
            prov2.Invocations.Count.ShouldBe(0, "Queued provider must not be invoked.");

            // Release cleanup latch
            cleanupReleaseGate.SetResult();

            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            snapshot.IsDisposed.ShouldBeTrue("Lease must be disposed after all workers exit.");
            prov2.Invocations.Count.ShouldBe(0, "Queued provider was never invoked.");
            cache.TrySetCount.ShouldBe(0, "Cache must not be written on early break.");
            history.SavedRecords.Count.ShouldBe(0, "History must not be written on early break.");
        }
        finally
        {
            cleanupReleaseGate.TrySetResult();
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TranslateAsync_CancellationWhileQueued_WorkerExitsCleanlyWithoutInvokingQueuedProvider()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1", model: "model-1");
        var (reg2, prov2) = CreateRegisteredProvider("prov-2", model: "model-2");
        var settings = CreateDefaultSettings(reg1.Profile, reg2.Profile) with
        {
            Translation = new TranslationSettings
            {
                Providers = [reg1.Profile, reg2.Profile],
                MaxConcurrentProviders = 1
            }
        };

        var snapshot = new TestSnapshot(settings, [reg1, reg2]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = new TrackingCache(CreateCache(settings));
        var history = new FakeHistoryRepository();

        var prov1StartedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prov1ReleaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        prov1.StreamHandler = (req, ct) => StreamWithGate(prov1StartedGate, prov1ReleaseGate, ct);
        static async IAsyncEnumerable<TranslationChunk> StreamWithGate(
            TaskCompletionSource startedGate,
            TaskCompletionSource releaseGate,
            [EnumeratorCancellation] CancellationToken ct)
        {
            startedGate.SetResult();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await releaseGate.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) { }

            yield return new TranslationChunk("Delta 1", FinishReason: TranslationFinishReason.Completed);
        }

        using var userCts = new CancellationTokenSource();
        var orchestrator = CreateOrchestrator(registry, cache, history);
        var request = new TranslationRequest
        {
            Text = "Hello",
            Origin = TextOrigin.Input,
            ProviderIds = ["prov-1", "prov-2"],
            RecordHistory = true
        };

        var events = new List<TranslationEvent>();
        var enumerator = orchestrator.TranslateAsync(request, userCts.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            events.Add(enumerator.Current); // TranslationStarted

            // MoveNextAsync to start workers and enter prov1 stream
            var moveNextTask = enumerator.MoveNextAsync().AsTask();

            await prov1StartedGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // User cancels while prov2 is queued waiting for concurrency semaphore
            userCts.Cancel();

            prov1ReleaseGate.TrySetResult();

            await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                if (await moveNextTask)
                {
                    events.Add(enumerator.Current);
                }
                while (await enumerator.MoveNextAsync())
                {
                    events.Add(enumerator.Current);
                }
            });

            snapshot.IsDisposed.ShouldBeTrue("Lease must be disposed upon cancellation.");
            prov2.Invocations.Count.ShouldBe(0, "Queued provider 2 must never have been invoked.");
            cache.TrySetCount.ShouldBe(0, "Cache must not be written on cancellation.");
            history.SavedRecords.Count.ShouldBe(0, "History must not be written on cancellation.");
        }
        finally
        {
            prov1ReleaseGate.TrySetResult();
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TranslateAsync_EarlyConsumerBreak_WhileBackpressureBlocked_CancelsAndCleansUp()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };

        var chunksProduced = 0;
        var producerBlockedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        prov1.StreamHandler = (req, ct) => BackpressureStream(ct);
        async IAsyncEnumerable<TranslationChunk> BackpressureStream([EnumeratorCancellation] CancellationToken ct)
        {
            for (int i = 0; i < 200; i++)
            {
                ct.ThrowIfCancellationRequested();
                chunksProduced++;
                if (chunksProduced >= 120)
                {
                    producerBlockedGate.TrySetResult();
                }
                yield return new TranslationChunk($"chunk-{i} ");
            }
            yield return new TranslationChunk("end", FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry);
        var request = new TranslationRequest { Text = "Hello", Origin = TextOrigin.Input };

        var enumerator = orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue(); // TranslationStarted
            (await enumerator.MoveNextAsync()).ShouldBeTrue(); // ProviderStarted
            (await enumerator.MoveNextAsync()).ShouldBeTrue(); // First ProviderDelta

            await producerBlockedGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var disposeTask = enumerator.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            snapshot.IsDisposed.ShouldBeTrue("Lease must be disposed after backpressure cancellation.");
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TranslateAsync_SnapshotCacheDisabled_DoesNotCallTryGetOrTrySet_ProviderSucceeds()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var settings = CreateDefaultSettings(reg1.Profile) with
        {
            Translation = new TranslationSettings
            {
                Providers = [reg1.Profile],
                CacheEnabled = false
            }
        };

        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = new TrackingCache(CreateCache(settings));

        var orchestrator = CreateOrchestrator(registry, cache);
        var request = new TranslationRequest
        {
            Text = "Hello world",
            Origin = TextOrigin.Input
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        cache.TryGetCount.ShouldBe(0, "TryGet must not be called when CacheEnabled is false in snapshot.");
        cache.TrySetCount.ShouldBe(0, "TrySet must not be called when CacheEnabled is false in snapshot.");

        var completed = events.OfType<ProviderCompleted>().Single();
        completed.FromCache.ShouldBeFalse();
        completed.Text.ShouldBe("translated text");

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(1);
        finished.FailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task TranslateAsync_DisposeAsyncThrows_WithoutPrimaryFailure_FailsProviderWithUnknownError_NoCompletedOrCacheOrHistory()
    {
        var (reg1, prov1) = CreateDirectProvider("prov-1", (req, ct) => new DisposableFailingEnumerable());

        var (reg2, prov2) = CreateRegisteredProvider("prov-2");
        var settings = CreateDefaultSettings(reg1.Profile, reg2.Profile);
        var snapshot = new TestSnapshot(settings, [reg1, reg2]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = new TrackingCache(CreateCache(settings));
        var history = new FakeHistoryRepository();

        prov2.StreamHandler = (req, ct) => NormalStream();
        static async IAsyncEnumerable<TranslationChunk> NormalStream()
        {
            await Task.Yield();
            yield return new TranslationChunk("Provider 2 text", FinishReason: TranslationFinishReason.Completed);
        }

        var orchestrator = CreateOrchestrator(registry, cache, history);
        var request = new TranslationRequest
        {
            Text = "Hello",
            Origin = TextOrigin.Input,
            ProviderIds = ["prov-1", "prov-2"],
            RecordHistory = true
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var prov1Failed = events.OfType<ProviderFailed>().Single(f => f.ProviderId == "prov-1");
        prov1Failed.Error.Code.ShouldBe(ErrorCode.Unknown);
        prov1Failed.Error.Detail.ShouldBe(nameof(CustomDisposeException));
        events.OfType<ProviderCompleted>().Any(c => c.ProviderId == "prov-1").ShouldBeFalse("Failed provider must not yield ProviderCompleted.");

        var prov2Completed = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-2");
        prov2Completed.Text.ShouldBe("Provider 2 text");

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(1);
        finished.FailedCount.ShouldBe(1);

        cache.TrySetCount.ShouldBe(1);

        history.SavedRecords.Count.ShouldBe(1);
        var historyEntry = history.SavedRecords.Single();
        historyEntry.Results.Count.ShouldBe(1);
        historyEntry.Results[0].ProviderId.ShouldBe("prov-2");
    }

    [Fact]
    public async Task TranslateAsync_DisposeAsyncThrows_WithPrimaryTimeout_RetainsPrimaryTimeoutError_NoDoubleFinalEvents()
    {
        var (reg1, prov1) = CreateDirectProvider("prov-1", (req, ct) => new FailingStreamWithDisposeError());

        var settings = CreateDefaultSettings(reg1.Profile);
        var snapshot = new TestSnapshot(settings, [reg1]);
        var registry = new TestRegistry { CurrentSnapshot = snapshot };
        var cache = new TrackingCache(CreateCache(settings));
        var history = new FakeHistoryRepository();

        var orchestrator = CreateOrchestrator(registry, cache, history);
        var request = new TranslationRequest
        {
            Text = "Hello",
            Origin = TextOrigin.Input,
            RecordHistory = true
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failedEvents = events.OfType<ProviderFailed>().Where(f => f.ProviderId == "prov-1").ToList();
        failedEvents.Count.ShouldBe(1, "Exactly one terminal event must be emitted.");
        failedEvents[0].Error.Code.ShouldBe(ErrorCode.Timeout);

        events.OfType<ProviderCompleted>().Any(c => c.ProviderId == "prov-1").ShouldBeFalse();

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(0);
        finished.FailedCount.ShouldBe(1);

        cache.TrySetCount.ShouldBe(0);
        history.SavedRecords.Count.ShouldBe(0);
    }

    [Fact]
    public async Task TranslateAsync_TotalRequestBudget_AcrossMultipleProvidersAndCacheHit_FailsExceededProviderAndDoesNotOverflow()
    {
        var (reg1, prov1) = CreateRegisteredProvider("prov-1");
        var (reg2, prov2) = CreateRegisteredProvider("prov-2");
        var (reg3, prov3) = CreateRegisteredProvider("prov-3");
        var (reg4, prov4) = CreateRegisteredProvider("prov-4");
        var (reg5, prov5) = CreateRegisteredProvider("prov-5");

        var settings = CreateDefaultSettings(reg1.Profile, reg2.Profile, reg3.Profile, reg4.Profile, reg5.Profile) with
        {
            Translation = new TranslationSettings
            {
                Providers = [reg1.Profile, reg2.Profile, reg3.Profile, reg4.Profile, reg5.Profile],
                MaxConcurrentProviders = 1
            }
        };
        var registry = new TestRegistry { SnapshotFactory = () => new TestSnapshot(settings, [reg1, reg2, reg3, reg4, reg5]) };
        var cache = CreateCache(settings);

        static async IAsyncEnumerable<TranslationChunk> GenerateStream(int totalChars)
        {
            await Task.Yield();
            var chunk4000 = new string('X', 4000);
            int count = totalChars / 4000;
            for (int i = 0; i < count; i++)
            {
                yield return new TranslationChunk(chunk4000);
            }
            int remainder = totalChars % 4000;
            if (remainder > 0)
            {
                yield return new TranslationChunk(new string('X', remainder));
            }
            yield return new TranslationChunk("end", FinishReason: TranslationFinishReason.Completed);
        }

        // Prime cache for prov-1 with 500,000 chars (within MaxEntryBytes)
        prov1.StreamHandler = (req, ct) => GenerateStream(500_000);
        prov2.StreamHandler = (req, ct) => GenerateStream(1_000_000);
        prov3.StreamHandler = (req, ct) => GenerateStream(1_000_000);
        prov4.StreamHandler = (req, ct) => GenerateStream(1_000_000);
        prov5.StreamHandler = (req, ct) => GenerateStream(1_000_000);

        var orchestrator = CreateOrchestrator(registry, cache);

        // Run priming request for prov-1
        var primeRequest = new TranslationRequest
        {
            Text = "Hello",
            Origin = TextOrigin.Input,
            ProviderIds = ["prov-1"],
            RecordHistory = false
        };
        await foreach (var evt in orchestrator.TranslateAsync(primeRequest, TestContext.Current.CancellationToken))
        {
        }
        var request = new TranslationRequest
        {
            Text = "Hello",
            Origin = TextOrigin.Input,
            ProviderIds = ["prov-1", "prov-2", "prov-3", "prov-4", "prov-5"]
        };

        var events = new List<TranslationEvent>();
        await foreach (var evt in orchestrator.TranslateAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var comp1 = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-1");
        comp1.FromCache.ShouldBeTrue();

        var comp2 = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-2");
        comp2.FromCache.ShouldBeFalse();
        var comp3 = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-3");
        comp3.FromCache.ShouldBeFalse();
        var comp4 = events.OfType<ProviderCompleted>().Single(c => c.ProviderId == "prov-4");
        comp4.FromCache.ShouldBeFalse();

        var fail5 = events.OfType<ProviderFailed>().Single(f => f.ProviderId == "prov-5");
        fail5.Error.Code.ShouldBe(ErrorCode.InvalidResponse);
        fail5.Error.Message.ShouldBe("翻译结果超过本地长度限制");

        var finished = events.OfType<TranslationFinished>().Single();
        finished.SucceededCount.ShouldBe(4);
        finished.FailedCount.ShouldBe(1);
    }

    #endregion
}
