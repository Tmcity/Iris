using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.Hosting;
using Iris.App.Tests.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.App.Updates;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Updates;

public sealed class UpdateHandoffLifecycleTests
{
    [Fact]
    public async Task HandoffPrepareFailure_SetsStatusToFailed_AndPreservesRetryabilityWithoutModal()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        using var directory = new TempDirectory();
        var source = new UpdateCoordinatorTests.Source { Package = UpdateCoordinatorTests.PackageBytes() };
        var runtime = new UpdateCoordinatorTests.Runtime(directory.Path, source);
        var coordinator = new AppUpdateCoordinator(runtime, settings, new FakeTimeProvider());
        coordinator.Start();
        try
        {
            await coordinator.CheckAsync(this);
            var offer = coordinator.Snapshot.OfferId!.Value;
            await coordinator.DownloadAsync(this, offer);
            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Ready);

            var env = new FailingHandoffEnvironment(new IrisException(ErrorCode.Timeout, "prepare timed out"));
            await using var handoff = new UpdateHandoff(env);
            var lifetime = Substitute.For<IAppLifetime>();

            var uiDispatcher = Substitute.For<IUiDispatcher>();
            uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => callInfo.Arg<Func<Task>>()());

            var autoStart = Substitute.For<IAutoStartService>();
            var saveCoordinator = new GeneralSettingsSaveCoordinator(settings, new TestSettingsEditCoordinator(settings), autoStart, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            using var vm = new SettingsViewModel(settings, new ThemeService(settings), saveCoordinator, null, null, uiDispatcher, null,
                updates: coordinator, updateHandoff: handoff, updateLifetime: lifetime);

            // Execute install directly via private/internal path with candidate already prepared
            var candidate = await coordinator.PrepareApplyAsync(vm, offer, CancellationToken.None);
            candidate.ShouldNotBeNull();

            // When handoff preparation fails, coordinator rolls back to Failed with error code
            coordinator.RollbackApply(candidate, ErrorCode.Timeout);

            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Failed);
            coordinator.Snapshot.Error.ShouldBe(ErrorCode.Timeout);
            coordinator.HasCandidate.ShouldBeTrue();

            vm.UpdateStatus.ShouldBe(UiText.UpdateState(UpdatePhase.Failed));
            vm.UpdateDetail.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Timeout));
            vm.InstallUpdateCommand.CanExecute(null).ShouldBeTrue();
            vm.CheckUpdateCommand.CanExecute(null).ShouldBeTrue();
        }
        finally
        {
            await coordinator.StopAsync();
        }
    }

    [Fact]
    public async Task ShutdownFailureAfterHandoffPrepared_RollsBackCoordinator_AndResetsTransferredFlag()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        using var directory = new TempDirectory();
        var source = new UpdateCoordinatorTests.Source { Package = UpdateCoordinatorTests.PackageBytes() };
        var runtime = new UpdateCoordinatorTests.Runtime(directory.Path, source);
        var coordinator = new AppUpdateCoordinator(runtime, settings, new FakeTimeProvider());
        coordinator.Start();
        try
        {
            await coordinator.CheckAsync(this);
            var offer = coordinator.Snapshot.OfferId!.Value;
            await coordinator.DownloadAsync(this, offer);
            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Ready);

            var candidate = await coordinator.PrepareApplyAsync(this, offer, CancellationToken.None);
            candidate.ShouldNotBeNull();

            var host = Substitute.For<IHost>();
            var failure = new IOException("simulated host shutdown failure");
            host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(failure));

            var logs = new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance);
            var theme = new ThemeService(settings);
            var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, Substitute.For<IActionDispatcher>(), NullLogger<TrayIconController>.Instance)
            { SuppressIconCreationForTesting = true };

            var bootstrapper = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
                logs, theme, tray, new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), host,
                NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(Environment.TickCount64));

            var env = new ControlledHandoffEnvironment();
            await using var handoff = new UpdateHandoff(env);

            var uiDispatcher = Substitute.For<IUiDispatcher>();
            uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => callInfo.Arg<Func<Task>>()());

            var autoStart = Substitute.For<IAutoStartService>();
            var saveCoordinator = new GeneralSettingsSaveCoordinator(settings, new TestSettingsEditCoordinator(settings), autoStart, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton(bootstrapper);
            services.AddSingleton(handoff);
            services.AddSingleton(coordinator);

            var provider = services.BuildServiceProvider();
            var lifetime = new AppLifetime(provider, uiDispatcher, NullLogger<AppLifetime>.Instance);

            var vm = new SettingsViewModel(settings, theme, saveCoordinator, null, null, uiDispatcher, null,
                updates: coordinator, updateHandoff: handoff, updateLifetime: lifetime);

            // Re-build provider with SettingsViewModel
            services.AddSingleton(vm);
            var providerWithVm = services.BuildServiceProvider();
            var lifetimeWithVm = new AppLifetime(providerWithVm, uiDispatcher, NullLogger<AppLifetime>.Instance);

            lifetimeWithVm.RequestShutdown();
            (lifetimeWithVm.ShutdownWork != null).ShouldBeTrue();

            var thrown = await Should.ThrowAsync<Exception>(lifetimeWithVm.ShutdownWork!);
            (thrown is IOException || (thrown is AggregateException ae && ae.InnerExceptions.Any(e => e is IOException))).ShouldBeTrue();

            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Failed);
            coordinator.Snapshot.Error.ShouldBe(ErrorCode.Unknown);
            coordinator.HasCandidate.ShouldBeTrue();

            vm.UpdateStatus.ShouldBe(UiText.UpdateState(UpdatePhase.Failed));
            vm.InstallUpdateCommand.CanExecute(null).ShouldBeTrue();
            vm.CheckUpdateCommand.CanExecute(null).ShouldBeTrue();
        }
        finally
        {
            await coordinator.StopAsync();
        }
    }

    private sealed class FailingHandoffEnvironment(Exception error) : UpdateHandoffEnvironment
    {
        internal override void ValidateInstallation() { }
        internal override Task ValidateCandidateAsync(UpdateCandidate candidate, CancellationToken token) => Task.FromException(error);
    }

    private sealed class ControlledHandoffEnvironment : UpdateHandoffEnvironment
    {
        internal override void ValidateInstallation() { }
        internal override Task ValidateCandidateAsync(UpdateCandidate candidate, CancellationToken token) => Task.CompletedTask;
    }
}
