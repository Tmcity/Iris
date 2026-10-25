using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.App.Updates;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Maintenance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Updates;

[Collection("AppShutdownerSharedState")]
public sealed class UpdateHandoffLifecycleTests
{
    [Fact]
    public async Task HandoffPrepareFailure_SetsStatusToFailed_AndPreservesRetryabilityWithoutModal()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ReadyAsync();
            fixture.Environment.PrepareFailure = new IrisException(ErrorCode.Timeout, "synthetic preparation timeout");
            await fixture.InstallAsync();

            fixture.Coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Failed);
            fixture.Coordinator.Snapshot.Error.ShouldBe(ErrorCode.Timeout);
            fixture.Coordinator.HasCandidate.ShouldBeTrue();
            fixture.ViewModel.UpdateStatus.ShouldBe(UiText.UpdateState(UpdatePhase.Failed));
            fixture.ViewModel.UpdateDetail.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Timeout));
            fixture.ViewModel.InstallUpdateCommand.CanExecute(null).ShouldBeTrue();
            fixture.ViewModel.CheckUpdateCommand.CanExecute(null).ShouldBeTrue();
            fixture.Lifetime.HasConfirmedUpdate.ShouldBeFalse();
            fixture.Lifetime.ShutdownWork.ShouldBeNull();
            fixture.Environment.Workers.Count.ShouldBe(0);
            fixture.Record("prepare-failure", new { ActualCommandExecuted = true, Phase = fixture.Coordinator.Snapshot.Phase.ToString(), HasCandidate = fixture.Coordinator.HasCandidate });
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShutdownFailureAfterHandoffPrepared_RollsBackCoordinator_AndResetsTransferredFlag(bool windowRetained)
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ReadyAsync();
            var originalSession = fixture.Presenter.ActiveSession!;
            var originalVm = fixture.ViewModel;
            var witness = fixture.Witnesses.Single();
            if (windowRetained) fixture.BlockWindowCloseAttempts = 2;
            else fixture.FailHostStops = 2;

            await fixture.InstallAsync();
            _ = fixture.Lifetime.ShutdownWork.ShouldNotBeNull();
            var firstFailure = await Should.ThrowAsync<Exception>(fixture.Lifetime.ShutdownWork!);
            fixture.AssertFailure(firstFailure, windowRetained);
            fixture.Environment.AssertRevoked(1);

            fixture.Coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Failed);
            fixture.Coordinator.Snapshot.Error.ShouldBe(ErrorCode.Unknown);
            fixture.Coordinator.HasCandidate.ShouldBeTrue();
            fixture.Lifetime.HasConfirmedUpdate.ShouldBeTrue();
            fixture.Lifetime.RecoveryWindow.ShouldNotBeNull();
            fixture.Lifetime.RecoveryWindow!.IsVisible.ShouldBeTrue();
            fixture.Lifetime.RecoveryWindow.FailureText.ShouldContain(UiText.UpdateRecoveryRequired);
            fixture.Lifetime.RecoveryWindow.RetryCommand.CanExecute(null).ShouldBeTrue();
            if (windowRetained)
            {
                fixture.Presenter.ActiveSession.ShouldBeSameAs(originalSession);
                witness.Disposed.ShouldBeFalse();
                originalVm.UpdateHandoffTransferred.ShouldBeFalse();
                originalVm.UpdateStatus.ShouldBe(UiText.UpdateState(UpdatePhase.Failed));
                originalVm.UpdateDetail.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
            }
            else
            {
                fixture.Presenter.ActiveSession.ShouldBeNull();
                witness.Disposed.ShouldBeTrue();
                fixture.NotificationsAfterScopeDisposed.ShouldBe(0);
            }
            await fixture.AssertOrdinaryWorkClosedAsync(originalVm);
            fixture.Witnesses.Count.ShouldBe(1);
            await fixture.CaptureRecoveryAsync(windowRetained ? "retained" : "disposed");
            fixture.Record(windowRetained ? "retained-failure" : "disposed-failure", fixture.Snapshot());

            // Retry uses the dedicated shutdown-owned command and a fresh actual
            // helper handshake. A second failure revokes/joins that worker too.
            var retryCommand = fixture.Lifetime.RecoveryWindow!.RetryCommand;
            retryCommand.Execute(null);
            await retryCommand.ExecutionTask!;
            _ = fixture.Lifetime.ShutdownWork.ShouldNotBeNull();
            var secondFailure = await Should.ThrowAsync<Exception>(fixture.Lifetime.ShutdownWork!);
            fixture.AssertFailure(secondFailure, windowRetained);
            fixture.Environment.AssertRevoked(2);
            fixture.Lifetime.HasConfirmedUpdate.ShouldBeTrue();
            fixture.Coordinator.HasCandidate.ShouldBeTrue();

            // A final stop succeeds. Concurrent retry requests share this attempt,
            // and only Main's final boundary may send the one commit.
            fixture.HoldSuccessfulHostStop = true;
            retryCommand.Execute(null);
            await fixture.HostSuccessEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            var finalStop = fixture.Lifetime.ShutdownWork!;
            fixture.Lifetime.RetryUpdateAsync().ShouldBeSameAs(finalStop);
            fixture.Lifetime.RequestShutdown();
            fixture.Environment.Workers.Count.ShouldBe(3);
            fixture.Handoff.IsPrepared.ShouldBeTrue();
            MaintenanceGate.IsBlocked(fixture.Environment.StateRootForTests).ShouldBeTrue();
            fixture.HostSuccessRelease.TrySetResult();
            await retryCommand.ExecutionTask!;
            await finalStop;
            fixture.Bootstrapper.CleanupCompleted.ShouldBeTrue();
            fixture.ShutdownCalls.ShouldBe(1);
            fixture.Presenter.ActiveSession.ShouldBeNull();
            fixture.Witnesses.Count.ShouldBe(1);
            witness.Disposed.ShouldBeTrue();
            fixture.Lifetime.RecoveryWindow.ShouldBeNull();
            fixture.Lifetime.HasConfirmedUpdate.ShouldBeFalse();

            await fixture.Handoff.CompleteMainAsync(true);
            await fixture.Handoff.CompleteMainAsync(true);
            var worker = fixture.Environment.Workers.Last();
            worker.Process.HasExited.ShouldBeFalse("the actual original test process is still alive");
            fixture.Record(windowRetained ? "retained-success-before-parent-exit" : "disposed-success-before-parent-exit", new
            {
                fixture.ShutdownCalls, PreparedWorkers = fixture.Environment.Workers.Count,
                CommitReceiptCompleted = true, RepeatedFinalBoundaryCompleted = true, ParentStillAlive = true,
                fixture.Source.Requests, fixture.Source.Downloads,
            });
            // CompleteMain relinquished its process handle; this owned cancel
            // event joins the controlled worker without terminating the test host.
            worker.Cancel.Set();
            await worker.Process.WaitForExitAsync(CancellationToken.None);
            worker.Process.ExitCode.ShouldBe(0);
            fixture.Environment.AssertAllSafe();
        });
    }

    [Fact]
    public async Task ShutdownFailureAndRevokeCleanupFailure_PreserveBothErrors_AndRetryCleanupBeforePreparingAgain()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ReadyAsync();
            fixture.FailHostStops = 1;
            fixture.Environment.FailDirectoryCleanup = true;
            await fixture.InstallAsync();
            var error = await Should.ThrowAsync<AggregateException>(fixture.Lifetime.ShutdownWork!);
            error.Flatten().InnerExceptions.ShouldContain(item => ReferenceEquals(item, fixture.HostFailure));
            error.Flatten().InnerExceptions.ShouldContain(item => ReferenceEquals(item, fixture.Environment.CleanupFailure));
            fixture.Lifetime.RecoveryFailure.ShouldBeSameAs(error);
            fixture.Environment.Workers.Count.ShouldBe(1);
            fixture.Environment.Workers[0].Process.HasExited.ShouldBeTrue();
            fixture.Handoff.IsPrepared.ShouldBeFalse();
            MaintenanceGate.IsBlocked(fixture.Environment.StateRootForTests).ShouldBeFalse();

            // An additional cleanup failure cannot start another helper.
            await Should.ThrowAsync<AggregateException>(fixture.Lifetime.RetryUpdateAsync());
            fixture.Lifetime.RecoveryFailures.ShouldContain(error);
            fixture.Environment.Workers.Count.ShouldBe(1);
            fixture.Environment.FailDirectoryCleanup = false;
            await fixture.Lifetime.RetryUpdateAsync();
            fixture.Environment.Workers.Count.ShouldBe(2);
            fixture.Bootstrapper.CleanupCompleted.ShouldBeTrue();
            await fixture.Handoff.CompleteMainAsync(false);
            fixture.Environment.AssertAllSafe();
            fixture.Record("cleanup-failure-then-success", new { PrimaryPreserved = true, CleanupPreserved = true, Workers = fixture.Environment.Workers.Count });
        });
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("confirmation-canceled")]
    [InlineData("accepted-then-canceled")]
    [InlineData("new-offer")]
    public async Task OrdinaryShutdownFailureWithoutTransferredIntent_NeverRecoversAnUnconfirmedOrStaleCandidate(string scenario)
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ReadyAsync();
            if (scenario == "confirmation-canceled")
            {
                fixture.ViewModel.ConfirmUpdateInstall = () => false;
                await fixture.InstallAsync();
            }
            else if (scenario == "accepted-then-canceled")
            {
                fixture.Runtime.HoldValidation = true;
                var command = (AsyncRelayCommand)fixture.ViewModel.InstallUpdateCommand;
                command.Execute(null);
                await fixture.Runtime.ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                var cancel = fixture.Coordinator.CancelAsync();
                fixture.Runtime.ValidationToken.IsCancellationRequested.ShouldBeTrue();
                fixture.Runtime.ValidationRelease.TrySetResult();
                await command.ExecutionTask!;
                await cancel;
            }
            else if (scenario == "new-offer")
            {
                fixture.Environment.PrepareFailure = new IrisException(ErrorCode.Timeout, "synthetic preparation timeout");
                await fixture.InstallAsync();
                var oldOffer = fixture.Coordinator.Snapshot.OfferId;
                await fixture.Coordinator.CheckAsync(fixture.ViewModel);
                fixture.Coordinator.Snapshot.OfferId.ShouldNotBe(oldOffer);
            }

            fixture.FailHostStops = 1;
            fixture.Lifetime.RequestShutdown();
            var failure = await Should.ThrowAsync<IOException>(fixture.Lifetime.ShutdownWork!);
            failure.ShouldBeSameAs(fixture.HostFailure);
            fixture.Lifetime.HasConfirmedUpdate.ShouldBeFalse();
            fixture.Lifetime.RecoveryWindow.ShouldBeNull();
            fixture.Coordinator.HasCandidate.ShouldBeFalse();
            fixture.Environment.Workers.Count.ShouldBe(0);
            fixture.Notifications.Received().Notify(NotificationLevel.Error, UiText.AppName, UiText.ShutdownIncomplete);
            fixture.Record("ordinary-" + scenario, new { HasConfirmedUpdate = false, RecoveryWindowCreated = false, Helpers = fixture.Environment.Workers.Count });
            fixture.Lifetime.RequestShutdown();
            await fixture.Lifetime.ShutdownWork!;
        });
    }

    [Fact]
    public async Task CancelTransferredUpdateAfterFailure_DropsOnlyItsIntent_AndNeverPreparesOrCommitsItAgain()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ReadyAsync();
            fixture.FailHostStops = 2;
            await fixture.InstallAsync();
            await Should.ThrowAsync<IOException>(fixture.Lifetime.ShutdownWork!);
            fixture.Environment.AssertRevoked(1);

            var cancelCommand = fixture.Lifetime.RecoveryWindow!.CancelUpdateCommand;
            cancelCommand.Execute(null);
            await cancelCommand.ExecutionTask!;
            await Should.ThrowAsync<IOException>(fixture.Lifetime.ShutdownWork!);
            fixture.Lifetime.HasConfirmedUpdate.ShouldBeFalse();
            fixture.Lifetime.RecoveryWindow.ShouldBeNull();
            fixture.Coordinator.HasCandidate.ShouldBeFalse();
            fixture.Environment.Workers.Count.ShouldBe(1);
            fixture.Lifetime.RequestShutdown();
            await fixture.Lifetime.ShutdownWork!;
            fixture.Environment.Workers.Count.ShouldBe(1);
            fixture.Environment.AssertAllSafe();
            fixture.Record("cancel-transferred", new { Helpers = fixture.Environment.Workers.Count, fixture.ShutdownCalls, HasConfirmedUpdate = false });
        });
    }

    private sealed class ScopeWitness : IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions RecordJson = new() { WriteIndented = true };
        private readonly TempDirectory _temporary = new();
        private readonly ServiceProvider _provider;
        private readonly Action<int> _previousShutdowner;
        private readonly TestSettingsEditCoordinator _editing;
        private readonly IAutoStartService _autoStart = Substitute.For<IAutoStartService>();
        private int _commits, _tests, _models;
        private string? _evidenceLabel;
        internal readonly ISettingsService Settings = Substitute.For<ISettingsService>();
        internal readonly INotificationService Notifications = Substitute.For<INotificationService>();
        internal readonly UpdateCoordinatorTests.Source Source = new() { Package = UpdateCoordinatorTests.PackageBytes() };
        internal readonly UpdateCoordinatorTests.Runtime Runtime;
        internal readonly AppUpdateCoordinator Coordinator;
        internal readonly ControlledEnvironment Environment;
        internal readonly UpdateHandoff Handoff;
        internal readonly SettingsWindowPresenter Presenter;
        internal readonly AppLifetime Lifetime;
        internal readonly AppBootstrapper Bootstrapper;
        internal readonly List<ScopeWitness> Witnesses = [];
        internal readonly IOException HostFailure = new("synthetic Host.StopAsync failure");
        internal readonly TaskCompletionSource HostSuccessEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            HostSuccessRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SettingsViewModel ViewModel = null!;
        internal int FailHostStops, BlockWindowCloseAttempts, HostStops, ShutdownCalls, NotificationsAfterScopeDisposed;
        internal bool HoldSuccessfulHostStop;

        internal Fixture()
        {
            string packages = Path.Combine(_temporary.Path, "install", "packages");
            Directory.CreateDirectory(packages);
            Settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
            Runtime = new(packages, Source);
            Coordinator = new(Runtime, Settings, new FakeTimeProvider());
            Environment = new ControlledEnvironment(_temporary.Path);
            Handoff = new(Environment);
            var ui = new WpfUiDispatcher();
            var host = Substitute.For<IHost>();
            host.StopAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                HostStops++;
                if (HostStops <= FailHostStops) throw HostFailure;
                HostSuccessEntered.TrySetResult();
                if (HoldSuccessfulHostStop) await HostSuccessRelease.Task;
            });
            var network = Substitute.For<INetworkTransportLifetime>();
            var theme = new ThemeService(Settings, visualThemeApplier: _ => { });
            _editing = new TestSettingsEditCoordinator(Settings)
            {
                OnCommitAsync = (_, _) => { _commits++; return Task.FromResult(new SettingsEditResult(SettingsEditStatus.Applied)); },
                OnTestDraftAsync = (_, _) => { _tests++; throw new InvalidOperationException("Stopped settings test was admitted."); },
                OnListDraftModelsAsync = (_, _) => { _models++; throw new InvalidOperationException("Stopped model query was admitted."); },
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Settings);
            services.AddSingleton(theme);
            services.AddSingleton(Coordinator);
            services.AddSingleton(Handoff);
            services.AddSingleton<IUiDispatcher>(ui);
            services.AddSingleton(Notifications);
            services.AddSingleton<IAppLifetime, AppLifetime>();
            services.AddSingleton<SettingsWindowPresenter>();
            services.AddSingleton<ISettingsShutdownCoordinator>(sp => sp.GetRequiredService<SettingsWindowPresenter>());
            services.AddScoped<ScopeWitness>(_ => { var witness = new ScopeWitness(); Witnesses.Add(witness); return witness; });
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
            {
                _ = sp.GetRequiredService<ScopeWitness>();
                return new(Settings, _editing, _autoStart, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });
            services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
                Settings, theme, sp.GetRequiredService<GeneralSettingsSaveCoordinator>(), _editing, null, ui, null,
                updates: Coordinator, updateHandoff: Handoff, updateLifetime: sp.GetRequiredService<IAppLifetime>()));
            services.AddTransient<SettingsWindow>(_ => new SettingsWindow
            {
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
            });
            services.AddSingleton<AppBootstrapper>(sp => new AppBootstrapper(Settings, Substitute.For<ITranslationProviderRegistry>(),
                Substitute.For<IDatabaseInitializer>(), new LogLevelController(Settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance),
                theme, new TrayIconController(sp.GetRequiredService<IAppLifetime>(), Settings, Substitute.For<IActionDispatcher>(), NullLogger<TrayIconController>.Instance)
                { SuppressIconCreationForTesting = true }, new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), host,
                NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(System.Environment.TickCount64),
                settingsShutdownCoordinator: sp.GetRequiredService<ISettingsShutdownCoordinator>(), networkLifetime: network, updates: Coordinator));
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            Presenter = _provider.GetRequiredService<SettingsWindowPresenter>();
            Lifetime = (AppLifetime)_provider.GetRequiredService<IAppLifetime>();
            Bootstrapper = _provider.GetRequiredService<AppBootstrapper>();
            _previousShutdowner = AppBootstrapper.AppShutdowner;
            AppBootstrapper.AppShutdowner = _ => ShutdownCalls++;
        }

        internal async Task ReadyAsync()
        {
            Coordinator.Start();
            await Presenter.ShowAsync("general");
            ViewModel = Presenter.ActiveSession!.ViewModel;
            ViewModel.AddProfileFromPreset(SettingsViewModel.AvailablePresets[0]);
            ViewModel.SelectedProfile!.Model = "synthetic-model";
            ViewModel.ConfirmUpdateInstall = () => true;
            var witness = Witnesses.Single();
            ViewModel.PropertyChanged += (_, _) => { if (witness.Disposed) NotificationsAfterScopeDisposed++; };
            Presenter.ActiveSession.Window.Closing += (_, args) =>
            {
                if (BlockWindowCloseAttempts > 0) { BlockWindowCloseAttempts--; args.Cancel = true; }
            };
            await Coordinator.CheckAsync(ViewModel);
            await Coordinator.DownloadAsync(ViewModel, Coordinator.Snapshot.OfferId!.Value);
            await DrainAsync();
            Coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Ready);
        }

        internal async Task InstallAsync()
        {
            var command = (AsyncRelayCommand)ViewModel.InstallUpdateCommand;
            command.CanExecute(null).ShouldBeTrue();
            command.Execute(null);
            _ = command.ExecutionTask.ShouldNotBeNull();
            await command.ExecutionTask!;
            await DrainAsync();
        }

        internal void AssertFailure(Exception failure, bool retained)
        {
            if (retained)
            {
                failure.ShouldBeOfType<IrisException>().Detail!.ShouldContain("SETTINGS_OWNER");
                HostStops.ShouldBe(0);
            }
            else failure.ShouldBeSameAs(HostFailure);
        }

        internal async Task AssertOrdinaryWorkClosedAsync(SettingsViewModel original)
        {
            original.CanEdit.ShouldBeFalse();
            original.SaveCommand.CanExecute(null).ShouldBeFalse();
            original.TestDraftCommand.CanExecute(null).ShouldBeFalse();
            original.ListModelsCommand.CanExecute(null).ShouldBeFalse();
            original.CheckUpdateCommand.CanExecute(null).ShouldBeFalse();
            original.DownloadUpdateCommand.CanExecute(null).ShouldBeFalse();
            int autoCalls = _autoStart.ReceivedCalls().Count();
            await original.ExecuteSaveAsync();
            await original.ExecuteTestDraftAsync();
            await original.ExecuteListModelsAsync();
            await original.ExecuteRetryAutoStartAsync();
            await original.ExecuteRetryPendingApplyAsync();
            await Presenter.ShowAsync("translation");
            await Coordinator.CheckAsync(this);
            _commits.ShouldBe(0); _tests.ShouldBe(0); _models.ShouldBe(0);
            _autoStart.ReceivedCalls().Count().ShouldBe(autoCalls);
            _ = Settings.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
            Source.Requests.ShouldBe(1); Source.Downloads.ShouldBe(1);
            Witnesses.Count.ShouldBe(1);
        }

        internal object Snapshot() => new
        {
            Phase = Coordinator.Snapshot.Phase.ToString(), Coordinator.HasCandidate, Lifetime.HasConfirmedUpdate,
            SettingsSessionAlive = Presenter.ActiveSession != null, ScopedVmCount = Witnesses.Count,
            DisposedVmCount = Witnesses.Count(witness => witness.Disposed),
            HelpersStarted = Environment.Workers.Count, HelpersExited = Environment.Workers.Count(worker => worker.Process.HasExited),
            Source.Requests, Source.Downloads, Commits = _commits, DraftTests = _tests, ModelQueries = _models, HostStops,
        };

        internal void Record(string name, object data)
        {
            _evidenceLabel ??= name;
            string? output = System.Environment.GetEnvironmentVariable("IRIS_R4_EVIDENCE");
            if (output == null) return;
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(new { Data = data, Current = Snapshot() }, RecordJson));
        }

        internal async Task CaptureRecoveryAsync(string name)
        {
            string? output = System.Environment.GetEnvironmentVariable("IRIS_R4_EVIDENCE");
            if (output == null) return;
            Directory.CreateDirectory(output);
            var window = Lifetime.RecoveryWindow!;
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            using var stream = File.Create(Path.Combine(output, name + "-recovery.png"));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        }

        private static async Task DrainAsync()
            => await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        public async ValueTask DisposeAsync()
        {
            BlockWindowCloseAttempts = 0; FailHostStops = 0;
            HostSuccessRelease.TrySetResult();
            Runtime.ValidationRelease.TrySetResult();
            Environment.FailDirectoryCleanup = false;
            try
            {
                if (Lifetime.ShutdownWork != null) try { await Lifetime.ShutdownWork; } catch (Exception) { }
                if (Lifetime.HasConfirmedUpdate) await Lifetime.CancelUpdateAndExitAsync();
                else
                {
                    Lifetime.RequestShutdown();
                    if (Lifetime.ShutdownWork != null) await Lifetime.ShutdownWork;
                }
                await Handoff.RevokeAsync();
                Lifetime.RecoveryWindow?.Close();
                await Presenter.ShutdownAsync();
                await Coordinator.StopAsync();
                await Environment.JoinAsync();
                string? output = System.Environment.GetEnvironmentVariable("IRIS_R4_EVIDENCE");
                if (output != null && _evidenceLabel != null)
                {
                    Directory.CreateDirectory(output);
                    for (int index = 0; index < Environment.Workers.Count; index++)
                        File.Copy(Environment.Workers[index].Result, Path.Combine(output, _evidenceLabel + "-worker-" + index + ".json"), overwrite: false);
                }
            }
            finally
            {
                AppBootstrapper.AppShutdowner = _previousShutdowner;
                await Handoff.DisposeAsync();
                await _provider.DisposeAsync();
                Environment.Dispose();
                _temporary.Dispose();
            }
        }
    }

    private sealed class ControlledEnvironment(string root) : UpdateHandoffEnvironment, IDisposable
    {
        internal sealed record Worker(Process Process, EventWaitHandle Cancel, string Result);
        internal readonly List<Worker> Workers = [];
        internal Exception? PrepareFailure;
        internal bool FailDirectoryCleanup;
        internal readonly IOException CleanupFailure = new("synthetic owned handoff-directory cleanup failure");
        internal string StateRootForTests => StateRoot;
        internal override string StateRoot => Path.Combine(root, "state");
        internal override string HelperPath => Path.Combine(AppContext.BaseDirectory, "update-worker", "Iris.Maintenance.Tests.exe");
        internal override void ValidateInstallation() { }
        internal override async Task ValidateCandidateAsync(UpdateCandidate candidate, CancellationToken token)
        {
            await base.ValidateCandidateAsync(candidate, token);
            if (PrepareFailure != null) throw PrepareFailure;
        }
        internal override Process StartHelper(ProcessStartInfo start)
        {
            string name = "Iris.R4.WorkerCancel." + Guid.NewGuid().ToString("N");
            var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            string result = "worker-" + Workers.Count + ".json";
            start.FileName = HelperPath;
            start.ArgumentList.Add(root);
            start.ArgumentList.Add(result);
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(System.Environment.ProcessPath!);
            var process = base.StartHelper(start);
            var monitor = Process.GetProcessById(process.Id);
            _ = monitor.Handle;
            Workers.Add(new(monitor, cancel, Path.Combine(root, result)));
            return process;
        }
        internal override void DeleteWorkDirectory(string directory)
        {
            if (FailDirectoryCleanup) throw CleanupFailure;
            base.DeleteWorkDirectory(directory);
        }
        internal void AssertRevoked(int expected)
        {
            Workers.Count.ShouldBe(expected);
            foreach (var worker in Workers)
            {
                worker.Process.HasExited.ShouldBeTrue("failure feedback must wait for the owned helper");
                worker.Process.ExitCode.ShouldBe(0);
            }
            AssertAllSafe();
        }
        internal void AssertAllSafe()
        {
            foreach (var worker in Workers)
            {
                using var result = JsonDocument.Parse(File.ReadAllText(worker.Result));
                result.RootElement.GetProperty("Failure").ValueKind.ShouldBe(JsonValueKind.Null);
                result.RootElement.GetProperty("NativeCalls").GetInt32().ShouldBe(0);
                result.RootElement.GetProperty("Restarts").GetInt32().ShouldBe(0);
                result.RootElement.GetProperty("GateBlocked").GetBoolean().ShouldBeFalse();
            }
            MaintenanceGate.IsBlocked(StateRoot).ShouldBeFalse();
        }
        internal async Task JoinAsync()
        {
            foreach (var worker in Workers) worker.Cancel.Set();
            foreach (var worker in Workers) await worker.Process.WaitForExitAsync(CancellationToken.None);
        }
        public void Dispose()
        {
            foreach (var worker in Workers) { worker.Process.Dispose(); worker.Cancel.Dispose(); }
        }
    }
}
