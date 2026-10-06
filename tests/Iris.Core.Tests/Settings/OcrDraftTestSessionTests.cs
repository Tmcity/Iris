using System.Runtime.CompilerServices;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Shouldly;
using static Iris.Core.Tests.Settings.OcrEditingTestScope;

namespace Iris.Core.Tests.Settings;

public sealed class OcrDraftTestSessionTests
{
    [Fact]
    public Task ConstructionAndUnusedDispose_DoNoExternalWork() => Run(async f =>
    {
        var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
        f.Settings.Reads.ShouldBe(0); f.SecretStore.Reads.ShouldBeEmpty(); f.Local.Created.ShouldBeEmpty();
        await session.DisposeAsync(); await session.DisposeAsync();
        f.Settings.Reads.ShouldBe(0); f.Local.Created.ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => session.RunAsync(TestContext.Current.CancellationToken));
    });

    [Theory][InlineData(CredentialEditKind.Keep)][InlineData(CredentialEditKind.Clear)][InlineData(CredentialEditKind.Replace)]
    public Task DisabledDraft_TestRunsOnceWithExplicitCredentialsAndNoRecognitionOrWrites(CredentialEditKind intent) => Run(async f =>
    {
        var profile = Profile("draft", OcrProviderKind.BaiduCloud) with { Enabled = false };
        var edit = new ProviderCredentialEdit(intent, intent == CredentialEditKind.Replace ? new("draft-key", "draft-secret") : null);
        f.Cloud.Configure = p => p.Test = _ => Task.FromResult(new ProviderTestResult(false, TimeSpan.FromMilliseconds(12), "private sample",
            new(ErrorCode.AuthenticationFailed, "private message", "private detail", 401, true, "private id")));
        var session = f.Draft(new(profile, null, edit));
        var result = await session.RunAsync(TestContext.Current.CancellationToken);
        var provider = f.Cloud.Created.Single(); provider.Profile.Enabled.ShouldBeFalse(); provider.TestCalls.ShouldBe(1); provider.RecognitionCalls.ShouldBe(0); provider.Disposed.ShouldBeFalse();
        provider.Credentials.ShouldBe(intent == CredentialEditKind.Replace ? edit.Replacement : ProviderCredentials.None);
        result.Sample.ShouldBeNull(); result.Error.ShouldBe(new ErrorInfo(ErrorCode.AuthenticationFailed, ErrorMessages.GetDefault(ErrorCode.AuthenticationFailed), HttpStatus: 401, Retryable: true));
        f.Settings.Updates.ShouldBe(0); f.SecretStore.Reads.ShouldBeEmpty(); f.SecretStore.Writes.ShouldBeEmpty(); f.OtherObserver.Calls.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => session.RunAsync(TestContext.Current.CancellationToken));
        await session.DisposeAsync(); provider.Disposed.ShouldBeTrue(); provider.DisposeCalls.ShouldBe(1);
    });

    [Fact]
    public Task ConcurrentRun_IsRejectedAndRealRegistryStopWaitsForSession() => Run(async f =>
    {
        var gate = f.NewGate(); f.Local.Configure = p => p.Test = async _ => { await gate.PauseAsync(); return new(true, TimeSpan.Zero); };
        var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
        var work = f.Track(session.RunAsync(TestContext.Current.CancellationToken));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Should.Throw<InvalidOperationException>(() => session.RunAsync(TestContext.Current.CancellationToken));
        gate.Open(); (await work).Success.ShouldBeTrue();
        var stopping = f.Track(f.Registry.StopAsync(TestContext.Current.CancellationToken));
        stopping.IsCompleted.ShouldBeFalse(); f.Local.Created.Single().Disposed.ShouldBeFalse();
        await session.DisposeAsync(); await stopping; f.Local.Created.Single().Disposed.ShouldBeTrue();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task Keep_UsesCurrentBaselineAndRejectsChangedOrExplicitBadReference(bool badReference) => Run(async f =>
    {
        var profile = Profile() with { CredentialSetId = badReference ? "broken-reference" : null };
        f.Settings.Value = new() { Ocr = new() { Providers = [profile], ActiveProviderId = profile.Id } };
        f.SecretStore.Values[SecretKeys.ApiKey(profile.Id)] = "legacy-value";
        var session = f.Draft(new(profile, profile, new(CredentialEditKind.Keep)));
        if (!badReference) f.Settings.Value = f.Settings.Value with { Ocr = f.Settings.Value.Ocr with { Providers = [profile with { WindowsOptions = new() { LanguageTag = "ja" } }] } };
        var result = await session.RunAsync(TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse(); result.Error!.Code.ShouldBe(badReference ? ErrorCode.NotConfigured : ErrorCode.Unknown);
        f.Local.Created.ShouldBeEmpty(); f.SecretStore.Reads.ShouldBeEmpty();
    });

    [Theory][InlineData(CredentialEditKind.Keep)][InlineData(CredentialEditKind.Clear)][InlineData(CredentialEditKind.Replace)]
    public Task ExistingDraft_KeepReadsLegacy_OtherIntentsDoNotReadOldCredentials(CredentialEditKind intent) => Run(async f =>
    {
        var profile = Profile("legacy", OcrProviderKind.BaiduCloud);
        f.Settings.Value = new() { Ocr = new() { Providers = [profile], ActiveProviderId = profile.Id } };
        f.SecretStore.Values[SecretKeys.ApiKey(profile.Id)] = "old-key"; f.SecretStore.Values[SecretKeys.SecretKey(profile.Id)] = "old-secret";
        if (intent != CredentialEditKind.Keep) f.SecretStore.BeforeGet = _ => throw new InvalidOperationException("Must not read old credentials.");
        var edit = new ProviderCredentialEdit(intent, intent == CredentialEditKind.Replace ? new("new-key", "new-secret") : null);
        var session = f.Draft(new(profile, profile, edit)); (await session.RunAsync(TestContext.Current.CancellationToken)).Success.ShouldBeTrue();
        f.Cloud.Created.Single().Credentials.ShouldBe(intent switch { CredentialEditKind.Keep => new("old-key", "old-secret"), CredentialEditKind.Replace => edit.Replacement, _ => ProviderCredentials.None });
        if (intent != CredentialEditKind.Keep) f.SecretStore.Reads.ShouldBeEmpty();
        f.SecretStore.Writes.ShouldBeEmpty(); f.Settings.Updates.ShouldBe(0);
    });

    [Fact]
    public Task AlreadyCanceledRun_DoesNotReadOrCreateAnything() => Run(async f =>
    {
        var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
        await Should.ThrowAsync<OperationCanceledException>(() => session.RunAsync(new CancellationToken(true)));
        f.Settings.Reads.ShouldBe(0); f.Local.Created.ShouldBeEmpty(); f.SecretStore.Reads.ShouldBeEmpty();
        await session.DisposeAsync();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task CancellationOrDispose_WaitsForNonCooperativeProvider(bool close) => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var gate = f.NewGate();
        f.Local.Configure = p => p.Test = async _ => { await gate.PauseAsync(); return new(true, TimeSpan.Zero); };
        var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
        var work = f.Track(session.RunAsync(caller.Token), typeof(OperationCanceledException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task? disposing = null;
        if (close) disposing = f.Track(session.DisposeAsync().AsTask()); else caller.Cancel();
        work.IsCompleted.ShouldBeFalse(); f.Local.Created.Single().Disposed.ShouldBeFalse();
        if (disposing != null) { disposing.IsCompleted.ShouldBeFalse(); session.DisposeAsync().AsTask().ShouldBeSameAs(disposing); }
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => work);
        if (disposing != null) await disposing; else await session.DisposeAsync();
        f.Local.Created.Single().DisposeCalls.ShouldBe(1);
    });

    [Fact]
    public Task RunAndDisposeFailures_AreBothObservable_RetryKeepsOriginalSnapshot() => Run(async f =>
    {
        f.Local.Configure = p => { p.FailDispose = true; p.Test = _ => throw new IrisException(ErrorCode.NetworkUnavailable, "private run", detail: "private detail"); };
        var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
        var result = await session.RunAsync(TestContext.Current.CancellationToken);
        result.Error!.Code.ShouldBe(ErrorCode.NetworkUnavailable); result.Error.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.NetworkUnavailable));
        var error = await Should.ThrowAsync<IrisException>(() => session.DisposeAsync().AsTask());
        error.InnerException.ShouldBeNull(); error.Detail.ShouldBe("DISPOSE");
        var provider = f.Local.Created.Single(); provider.Disposed.ShouldBeFalse(); provider.TestCalls.ShouldBe(1);
        provider.FailDispose = false; var gate = f.NewGate(); provider.BeforeDispose = gate.PauseAsync;
        var retry = f.Track(session.DisposeAsync().AsTask()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.DisposeAsync().AsTask().ShouldBeSameAs(retry);
        var registryRetry = f.Track(f.Registry.RetryCleanupAsync(TestContext.Current.CancellationToken));
        gate.Open(); await retry; await registryRetry;
        provider.DisposeCalls.ShouldBe(2); provider.TestCalls.ShouldBe(1); f.Local.Created.Count.ShouldBe(1);
    });

    [Fact]
    public Task ThrowingCancellationCallback_IsObservedAndCleanupStillRuns() => Run(async f =>
    {
        var workGate = f.NewGate(); var callbackGate = f.NewGate(); CancellationTokenRegistration callback = default;
        f.Local.Configure = p => p.Test = async token =>
        {
            callback = token.Register(() => { callbackGate.Pause(); throw new InvalidOperationException("private callback"); });
            await workGate.PauseAsync(); return new(true, TimeSpan.Zero);
        };
        var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
        var work = f.Track(session.RunAsync(TestContext.Current.CancellationToken), typeof(OperationCanceledException));
        await workGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var disposing = f.Track(session.DisposeAsync().AsTask(), typeof(IrisException));
        await callbackGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        workGate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => work); disposing.IsCompleted.ShouldBeFalse();
        callbackGate.Open(); var failure = await Should.ThrowAsync<IrisException>(() => disposing); failure.Detail.ShouldBe("CANCEL");
        f.Local.Created.Single().Disposed.ShouldBeTrue();
        await session.DisposeAsync(); f.Local.Created.Single().DisposeCalls.ShouldBe(1); await callback.DisposeAsync();
    });

    [Fact]
    public Task KeepCapture_ReleasesEditLockBeforeLongProviderTest() => Run(async f =>
    {
        var profile = Profile(); f.Settings.Value = new() { Ocr = new() { Providers = [profile], ActiveProviderId = profile.Id } };
        var gate = f.NewGate(); f.Local.Configure = p => p.Test = async _ => { await gate.PauseAsync(); return new(true, TimeSpan.Zero); };
        var session = f.Draft(new(profile, profile, new(CredentialEditKind.Keep)));
        var work = f.Track(session.RunAsync(TestContext.Current.CancellationToken));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var commit = f.Track(f.Coordinator.CommitAsync(Plan() with { General = new(LogLevel: new("Information", "Warning")) }, TestContext.Current.CancellationToken));
        (await commit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        work.IsCompleted.ShouldBeFalse(); gate.Open(); await work;
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task ClosedSession_ReleasesProviderAndCredentials_ActiveControlRetainsThem(bool dispose) => Run(async f =>
    {
        var (session, provider, credential) = await CreateReferences(f, dispose);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        provider.IsAlive.ShouldBe(!dispose); credential.IsAlive.ShouldBe(!dispose);
        GC.KeepAlive(session); await session.DisposeAsync();
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IOcrDraftTestSession Session, WeakReference Provider, WeakReference Credential)> CreateReferences(OcrEditingTestScope f, bool dispose)
    {
        var key = new string('K', 4000);
        var session = f.Draft(new(Profile("weak", OcrProviderKind.BaiduCloud), null, new(CredentialEditKind.Replace, new(key, "secret"))));
        await session.RunAsync(TestContext.Current.CancellationToken);
        var provider = new WeakReference(f.Cloud.Created.Single()); var credential = new WeakReference(key);
        if (dispose) await session.DisposeAsync();
        f.Cloud.Created.Clear(); return (session, provider, credential);
    }
}
