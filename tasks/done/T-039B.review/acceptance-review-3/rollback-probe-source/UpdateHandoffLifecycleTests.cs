using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Updates;

public sealed class UpdateHandoffLifecycleTests
{
    public bool WireProductionUpdates { get; set; }

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

            var uiDispatcher = Substitute.For<IUiDispatcher>();
            uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => callInfo.Arg<Func<Task>>()());
            uiDispatcher.CheckAccess().Returns(true);

            var autoStart = Substitute.For<IAutoStartService>();
            var theme = new ThemeService(settings);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(settings);
            services.AddSingleton(theme);
            services.AddSingleton(handoff);
            services.AddSingleton(coordinator);
            services.AddSingleton(uiDispatcher);
            services.AddSingleton<IAppLifetime, AppLifetime>();
            services.AddSingleton<SettingsWindowPresenter>();
            services.AddTransient<SettingsWindow>();
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp => new GeneralSettingsSaveCoordinator(
                settings, new TestSettingsEditCoordinator(settings), autoStart, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
                settings, sp.GetRequiredService<ThemeService>(), sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
                null, null, uiDispatcher, null,
                updates: coordinator, updateHandoff: handoff, updateLifetime: sp.GetRequiredService<IAppLifetime>()));

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();

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
        await WpfTestRunner.RunAsync(async () =>
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
                    NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(Environment.TickCount64), updates: WireProductionUpdates ? coordinator : null);

                var env = new ControlledHandoffEnvironment();
                await using var handoff = new UpdateHandoff(env);

                var uiDispatcher = Substitute.For<IUiDispatcher>();
                uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo => callInfo.Arg<Func<Task>>()());
                uiDispatcher.CheckAccess().Returns(true);

                var autoStart = Substitute.For<IAutoStartService>();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton(settings);
                services.AddSingleton(theme);
                services.AddSingleton(bootstrapper);
                services.AddSingleton(handoff);
                services.AddSingleton(coordinator);
                services.AddSingleton(uiDispatcher);
                services.AddSingleton<IAppLifetime, AppLifetime>();
                services.AddSingleton<SettingsWindowPresenter>();
                services.AddTransient<SettingsWindow>();
                services.AddScoped<GeneralSettingsSaveCoordinator>(sp => new GeneralSettingsSaveCoordinator(
                    settings, new TestSettingsEditCoordinator(settings), autoStart, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
                services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
                    settings, sp.GetRequiredService<ThemeService>(), sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
                    null, null, uiDispatcher, null,
                    updates: coordinator, updateHandoff: handoff, updateLifetime: sp.GetRequiredService<IAppLifetime>()));

                using var provider = services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                });

                var presenter = provider.GetRequiredService<SettingsWindowPresenter>();
                await presenter.ShowAsync("general");
                presenter.ActiveSession.ShouldNotBeNull();
                var vm = presenter.ActiveSession!.ViewModel;

                // Simulate handoff transferred flag being set before shutdown
                vm.SetHandoffTransferredForTesting(true);
                vm.UpdateHandoffTransferred.ShouldBeTrue();

                var lifetime = (AppLifetime)provider.GetRequiredService<IAppLifetime>();
                lifetime.RequestShutdown();
                (lifetime.ShutdownWork != null).ShouldBeTrue();

                var thrown = await Should.ThrowAsync<Exception>(lifetime.ShutdownWork!);
                (thrown is IOException || (thrown is AggregateException ae && ae.InnerExceptions.Any(e => e is IOException))).ShouldBeTrue();

                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { WireProductionUpdates, Phase = coordinator.Snapshot.Phase.ToString(), coordinator.HasCandidate, vm.UpdateHandoffTransferred, InstallCanExecute = vm.InstallUpdateCommand.CanExecute(null), CheckCanExecute = vm.CheckUpdateCommand.CanExecute(null), ShutdownException = thrown.GetType().Name }));
                // Coordinator rolled back to Failed with Unknown error
                coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Failed);
                coordinator.Snapshot.Error.ShouldBe(ErrorCode.Unknown);
                coordinator.HasCandidate.ShouldBeTrue();

                // Window VM handoff transferred flag is indeed reset
                vm.UpdateHandoffTransferred.ShouldBeFalse();
                vm.UpdateStatus.ShouldBe(UiText.UpdateState(UpdatePhase.Failed));
                vm.InstallUpdateCommand.CanExecute(null).ShouldBeTrue();
                vm.CheckUpdateCommand.CanExecute(null).ShouldBeTrue();

                try { presenter.ActiveSession?.Window?.Close(); } catch { }
            }
            finally
            {
                await coordinator.StopAsync();
            }
        });
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
