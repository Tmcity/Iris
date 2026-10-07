using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

public sealed class SettingsViewModelTests
{
    private static (SettingsViewModel ViewModel, ISettingsService SettingsService, ThemeService ThemeService, GeneralSettingsSaveCoordinator Coordinator, IAutoStartService AutoStart) CreateSut(AppSettings? initialSettings = null)
    {
        var settings = initialSettings ?? new AppSettings
        {
            General = new GeneralSettings
            {
                Theme = AppTheme.Light,
                LaunchAtStartup = false,
                ShowStartupNotification = true,
                LogLevel = "Information"
            }
        };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var themeService = new ThemeService(settingsService);
        var autoStartService = Substitute.For<IAutoStartService>();
        var coordinator = new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var uiDispatcher = Substitute.For<IUiDispatcher>();
        uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var fn = callInfo.Arg<Func<Task>>();
                return fn();
            });

        var vm = new SettingsViewModel(settingsService, themeService, coordinator, uiDispatcher);
        return (vm, settingsService, themeService, coordinator, autoStartService);
    }

    [Fact]
    public void Initialization_LoadsValuesFromCurrentSettings()
    {
        var (vm, _, _, _, _) = CreateSut();
        using (vm)
        {
            vm.Theme.ShouldBe(AppTheme.Light);
            vm.LaunchAtStartup.ShouldBeFalse();
            vm.ShowStartupNotification.ShouldBeTrue();
            vm.LogLevel.ShouldBe("Information");
            vm.IsDirty.ShouldBeFalse();
            vm.IsSaving.ShouldBeFalse();
            vm.IsConflict.ShouldBeFalse();
            vm.CanRetryAutoStart.ShouldBeFalse();
            vm.StatusMessage.ShouldBeNull();

            SettingsViewModel.AvailableThemes.ShouldContain(AppTheme.System);
            SettingsViewModel.AvailableThemes.ShouldContain(AppTheme.Light);
            SettingsViewModel.AvailableThemes.ShouldContain(AppTheme.Dark);

            SettingsViewModel.AvailableLogLevels.ShouldContain("Information");
        }
    }

    [Fact]
    public void ModifyingProperty_MarksIsDirty_AndThemePreviewTriggers()
    {
        var (vm, _, themeService, _, _) = CreateSut();
        using (vm)
        {
            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();
            themeService.HasActivePreview.ShouldBeTrue();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

            // 还原回原始值，IsDirty 变回 false
            vm.Theme = AppTheme.Light;
            vm.IsDirty.ShouldBeFalse();
        }
    }

    [Fact]
    public void ExecuteCancel_ClearsPreview_AndRaisesRequestClose()
    {
        var (vm, _, themeService, _, _) = CreateSut();
        using (vm)
        {
            var closeRequested = false;
            vm.RequestClose += () => closeRequested = true;

            vm.Theme = AppTheme.Dark;
            themeService.HasActivePreview.ShouldBeTrue();

            vm.CancelCommand.Execute(null);

            closeRequested.ShouldBeTrue();
            themeService.HasActivePreview.ShouldBeFalse();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Light);
        }
    }

    [Fact]
    public void ExecuteReload_ResetsDraftToBaseSnapshot_AndClearsDirtyAndStatus()
    {
        var (vm, _, themeService, _, _) = CreateSut();
        using (vm)
        {
            vm.Theme = AppTheme.Dark;
            vm.LaunchAtStartup = true;
            vm.ShowStartupNotification = false;
            vm.LogLevel = "Debug";
            vm.IsDirty.ShouldBeTrue();

            vm.ReloadCommand.Execute(null);

            vm.Theme.ShouldBe(AppTheme.Light);
            vm.LaunchAtStartup.ShouldBeFalse();
            vm.ShowStartupNotification.ShouldBeTrue();
            vm.LogLevel.ShouldBe("Information");
            vm.IsDirty.ShouldBeFalse();
            themeService.HasActivePreview.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ExecuteSaveAsync_WhenSuccess_ClearsPreview_UpdatesBaseSnapshot_AndSetsStatus()
    {
        var (vm, settingsService, themeService, _, _) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    return Task.CompletedTask;
                });

            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();

            await vm.ExecuteSaveAsync();

            vm.IsDirty.ShouldBeFalse();
            vm.StatusMessage.ShouldBe("设置已保存");
            vm.IsConflict.ShouldBeFalse();
            themeService.HasActivePreview.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ExecuteSaveAsync_WhenConflict_PreservesDraft_SetsConflictMessage_AndIsConflictTrue()
    {
        var (vm, settingsService, _, _, _) = CreateSut();
        using (vm)
        {
            // 外部更新导致保存时冲突检测触发
            var externalSettings = new AppSettings
            {
                General = new GeneralSettings { Theme = AppTheme.System }
            };
            settingsService.Current.Returns(externalSettings);

            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    mutate(externalSettings); // 抛出冲突
                    return Task.CompletedTask;
                });

            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();

            await vm.ExecuteSaveAsync();

            vm.IsDirty.ShouldBeTrue();
            vm.IsConflict.ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("冲突");
        }
    }

    [Fact]
    public async Task ExecuteSaveAsync_WhenAutoStartPending_UpdatesBase_SetsSavedApplicationPending_AndEnablesRetry()
    {
        var (vm, settingsService, _, _, autoStart) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    return Task.CompletedTask;
                });

            autoStart.When(x => x.SetEnabled(true)).Do(_ => throw new InvalidOperationException("Registry permission denied"));

            vm.LaunchAtStartup = true;
            vm.IsDirty.ShouldBeTrue();

            await vm.ExecuteSaveAsync();

            vm.IsDirty.ShouldBeFalse();
            vm.CanRetryAutoStart.ShouldBeTrue();
            vm.RetryAutoStartCommand.CanExecute(null).ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("开机启动设置未生效");
        }
    }

    [Fact]
    public void ExternalSettingsChanged_WhenFieldNotDirty_FollowsLatestSettings()
    {
        var (vm, settingsService, _, _, _) = CreateSut();
        using (vm)
        {
            // 用户只编辑了 Theme
            vm.Theme = AppTheme.Dark;

            // 外部修改了 LogLevel
            var oldSettings = settingsService.Current;
            var updatedSettings = oldSettings with
            {
                General = oldSettings.General with { LogLevel = "Warning" }
            };
            settingsService.Current.Returns(updatedSettings);

            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService,
                new SettingsChangedEventArgs(oldSettings, updatedSettings));

            // 未编辑的 LogLevel 自动跟随最新值
            vm.LogLevel.ShouldBe("Warning");
            // 已编辑的 Theme 保持用户的目标 Dark
            vm.Theme.ShouldBe(AppTheme.Dark);
            vm.IsDirty.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task EditDuringSave_IsDisabled_AndLateEditsAreIgnored()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (vm, settingsService, _, _, _) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    saveStarted.TrySetResult();
                    await saveRelease.Task;
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                });

            vm.LogLevel = "Debug";
            vm.IsDirty.ShouldBeTrue();

            var saveTask = vm.ExecuteSaveAsync();
            await saveStarted.Task;

            // 保存中状态
            vm.IsSaving.ShouldBeTrue();
            vm.CanEdit.ShouldBeFalse();

            // 尝试在保存中修改各字段
            vm.LogLevel = "Error";
            vm.Theme = AppTheme.Dark;
            vm.LaunchAtStartup = true;
            vm.ShowStartupNotification = false;

            // 验证未被更改
            vm.LogLevel.ShouldBe("Debug");
            vm.Theme.ShouldBe(AppTheme.Light);
            vm.LaunchAtStartup.ShouldBeFalse();
            vm.ShowStartupNotification.ShouldBeTrue();

            saveRelease.TrySetResult();
            await saveTask;

            vm.IsSaving.ShouldBeFalse();
            vm.CanEdit.ShouldBeTrue();
            vm.LogLevel.ShouldBe("Debug");
            vm.IsDirty.ShouldBeFalse();
            settingsService.Current.General.LogLevel.ShouldBe("Debug");
        }
    }

    [Fact]
    public async Task ExternalSettingsChanged_DuringSave_UpdatesBaselineFromCurrent_WhenSaveFinishes()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var saveRelease = new ManualResetEventSlim();

        var (vm, settingsService, _, _, autoStart) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    await Task.CompletedTask;
                });

            autoStart.When(x => x.SetEnabled(true)).Do(_ =>
            {
                saveStarted.TrySetResult();
                if (!saveRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Save release wait timed out");
                }
            });

            vm.LogLevel = "Debug";
            vm.LaunchAtStartup = true;

            var saveTask = vm.ExecuteSaveAsync();
            await saveStarted.Task;

            // 外部更新将已持久化的 LogLevel 改为 Error
            var externalSettings = settingsService.Current with
            {
                General = settingsService.Current.General with { LogLevel = "Error" }
            };
            settingsService.Current.Returns(externalSettings);

            saveRelease.Set();
            await saveTask;

            // 保存完成后从 Current 建立基线，UI 同步反映真实状态
            vm.LogLevel.ShouldBe("Error");
            vm.IsDirty.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task SaveAsync_WhenAutoStartApplyFails_PreservesRetryEligibility_AcrossUnrelatedSavesAndReload()
    {
        var (vm, settingsService, _, _, autoStart) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    return Task.CompletedTask;
                });

            // 模拟自启注册失败
            var failAutoStart = true;
            autoStart.When(x => x.SetEnabled(true)).Do(_ =>
            {
                if (failAutoStart)
                {
                    throw new InvalidOperationException("Registry error");
                }
                autoStart.IsEnabled().Returns(true);
            });
            autoStart.IsEnabled().Returns(_ => !failAutoStart);

            vm.LaunchAtStartup = true;
            await vm.ExecuteSaveAsync();

            vm.CanRetryAutoStart.ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("开机启动设置未生效");

            // 保存无关字段（如 LogLevel）
            vm.LogLevel = "Debug";
            await vm.ExecuteSaveAsync();

            // 自启仍待应用，重试资格独立保留
            vm.CanRetryAutoStart.ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("开机启动设置仍未生效");

            // 执行 Reload，重试资格依然保留
            vm.ExecuteReload();
            vm.CanRetryAutoStart.ShouldBeTrue();

            // 重试成功
            failAutoStart = false;
            await vm.ExecuteRetryAutoStartAsync();

            vm.CanRetryAutoStart.ShouldBeFalse();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("已生效");
        }
    }

    [Fact]
    public void Theme_WhenChangedAndChangedBackToBase_ClearsPreview_AndFollowsExternalUpdates()
    {
        var (vm, settingsService, themeService, _, _) = CreateSut();
        using (vm)
        {
            // 初始持久主题为 Light
            themeService.EffectiveTheme.ShouldBe(AppTheme.Light);
            themeService.HasActivePreview.ShouldBeFalse();

            // 改为 Dark：触发预览
            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();
            themeService.HasActivePreview.ShouldBeTrue();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

            // 改回 Light：结束预览，回到基线
            vm.Theme = AppTheme.Light;
            vm.IsDirty.ShouldBeFalse();
            themeService.HasActivePreview.ShouldBeFalse();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Light);

            // 外部更新持久主题为 Dark
            var oldSettings = settingsService.Current;
            var newSettings = oldSettings with
            {
                General = oldSettings.General with { Theme = AppTheme.Dark }
            };
            settingsService.Current.Returns(newSettings);

            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService,
                new SettingsChangedEventArgs(oldSettings, newSettings));

            // UI 与 EffectiveTheme 一致更新为 Dark，且无悬挂预览
            vm.Theme.ShouldBe(AppTheme.Dark);
            themeService.HasActivePreview.ShouldBeFalse();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);
        }
    }

    private sealed class HoldFirstDispatch : IUiDispatcher
    {
        private readonly WpfUiDispatcher _real = new();
        private readonly object _gate = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true, _drained;
        private Func<Task>? _callback;

        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released => _released.Task.IsCompleted;

        public bool CheckAccess() => _real.CheckAccess();

        public Task InvokeAsync(Func<Task> callback, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_first && !_drained)
                {
                    _first = false;
                    _callback = callback;
                    Queued.TrySetResult();
                    return _released.Task;
                }
            }
            return _real.InvokeAsync(callback, ct);
        }

        public async Task DrainAsync()
        {
            Func<Task>? work;
            lock (_gate)
            {
                _drained = true; // A callback arriving after cleanup starts must not become held work.
                work = _callback;
                _callback = null;
            }
            try
            {
                if (work != null) await _real.InvokeAsync(work, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _released.TrySetResult(); }
        }
    }

    private sealed class CaptureVmLogger : Microsoft.Extensions.Logging.ILogger<SettingsViewModel>
    {
        public int Calls { get; private set; }
        public bool RawException { get; private set; }
        public string? LastFormatted { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel level,
            Microsoft.Extensions.Logging.EventId id,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls++;
            RawException |= exception != null;
            LastFormatted = formatter(state, exception);
        }
    }

    [Fact]
    public async Task QueryAutoStartStatus_LateInitialQueryCallback_DoesNotOverrideSaveFailure()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { LaunchAtStartup = false } });
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    settingsService.Current.Returns(mutate(settingsService.Current));
                    return Task.CompletedTask;
                });

            using var releaseFreshQuery = new ManualResetEventSlim();
            var freshQueryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var freshUiState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int queries = 0;
            var autoStartService = Substitute.For<IAutoStartService>();
            autoStartService.IsEnabled().Returns(_ =>
            {
                if (Interlocked.Increment(ref queries) > 1)
                {
                    freshQueryEntered.TrySetResult();
                    releaseFreshQuery.Wait(); // Fake external synchronous query; finally always releases it.
                }
                return false;
            });
            autoStartService.When(x => x.SetEnabled(true)).Do(_ => throw new System.IO.IOException("Injected fake auto start error"));

            var dispatcher = new HoldFirstDispatch();
            using var themeService = new ThemeService(settingsService);
            using var coordinator = new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            SettingsViewModel? vm = null;
            Task? save = null;
            System.ComponentModel.PropertyChangedEventHandler? changed = null;
            Exception? primary = null;
            var cleanupErrors = new System.Collections.Generic.List<Exception>();
            try
            {
                vm = new SettingsViewModel(settingsService, themeService, coordinator, dispatcher);
                changed = (_, args) =>
                {
                    if (args.PropertyName == nameof(vm.CanRetryAutoStart) && vm.CanRetryAutoStart)
                    {
                        dispatcher.CheckAccess().ShouldBeTrue();
                        freshUiState.TrySetResult();
                    }
                };
                vm.PropertyChanged += changed; // Observe before saving can schedule the fresh query.
                await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                dispatcher.Released.ShouldBeFalse();

                vm.LaunchAtStartup = true;
                save = vm.ExecuteSaveAsync();
                await freshQueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                await save.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                save.IsCompletedSuccessfully.ShouldBeTrue();
                dispatcher.Released.ShouldBeFalse("the old query callback must remain queued after save");
                freshUiState.Task.IsCompleted.ShouldBeFalse("the fresh external query is still blocked");
                settingsService.Current.General.LaunchAtStartup.ShouldBeTrue();
                autoStartService.Received(1).SetEnabled(true);

                releaseFreshQuery.Set();
                await freshUiState.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                vm.CanRetryAutoStart.ShouldBeTrue("保存失败且最新查询UI状态完成后应允许重试自启");
                dispatcher.Released.ShouldBeFalse("observe the latest UI state before releasing the old callback");

                await dispatcher.DrainAsync();
                await coordinator.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                await vm.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                vm.CanRetryAutoStart.ShouldBeTrue("迟到的旧查询回调不得覆盖已失败的自启重试资格");
                settingsService.Current.General.LaunchAtStartup.ShouldBeTrue();
                autoStartService.Received(1).SetEnabled(true);
                Volatile.Read(ref queries).ShouldBe(2);
            }
            catch (Exception ex) { primary = ex; }
            finally
            {
                releaseFreshQuery.Set();
                try { await dispatcher.DrainAsync(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                if (save != null) try { await save; } catch (Exception ex) { cleanupErrors.Add(ex); }
                try { await coordinator.WaitForActiveWorkAsync(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                if (vm != null)
                {
                    try { await vm.WaitForActiveWorkAsync(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                    if (changed != null) vm.PropertyChanged -= changed;
                    try { vm.Dispose(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                }
            }
            if (primary != null && cleanupErrors.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary != null) cleanupErrors.Insert(0, primary);
            if (cleanupErrors.Count != 0) throw new AggregateException(cleanupErrors);
        });
    }

    [Fact]
    public async Task SaveAsync_WithFatalLogLevel_RejectedBeforeUpdateAsync()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { LogLevel = "Information" } });

        var autoStartService = Substitute.For<IAutoStartService>();
        using var coordinator = new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var draft = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.System,
            TargetTheme: AppTheme.System,
            ThemeDirty: false,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: false,
            LaunchAtStartupDirty: false,
            BaseShowStartupNotification: false,
            TargetShowStartupNotification: false,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "Fatal",
            LogLevelDirty: true);

        var result = await coordinator.SaveAsync(draft, CancellationToken.None);

        result.Status.ShouldBe(GeneralSaveStatus.Uncommitted);
        result.FailureReason.ShouldBe(UiText.SettingsStatusInvalidLogLevel);
        _ = settingsService.DidNotReceiveWithAnyArgs().UpdateAsync(default!, Arg.Any<CancellationToken>());
        settingsService.Current.General.LogLevel.ShouldBe("Information");
    }

    [Fact]
    public async Task Commands_OnUnexpectedException_LogDiagnostics_AndResetIsSavingState()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var injectFailure = false;
        settingsService.Current.Returns(_ =>
        {
            if (injectFailure)
            {
                throw new InvalidOperationException("Unexpected storage crash");
            }
            return new AppSettings();
        });

        var autoStartService = Substitute.For<IAutoStartService>();
        var logger = new CaptureVmLogger();
        using var themeService = new ThemeService(settingsService);
        using var coordinator = new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var uiDispatcher = Substitute.For<IUiDispatcher>();
        uiDispatcher.CheckAccess().Returns(true);
        uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task>>()());

        using var vm = new SettingsViewModel(settingsService, themeService, coordinator, uiDispatcher, logger);

        injectFailure = true;
        vm.LogLevel = "Debug";
        await vm.ExecuteSaveAsync();

        vm.IsSaving.ShouldBeFalse("意外异常后必须复位 IsSaving");
        vm.CanEdit.ShouldBeTrue("意外异常后必须恢复可编辑状态");
        vm.StatusMessage.ShouldBe(UiText.SettingsStatusSaveFailed);

        logger.Calls.ShouldBe(1);
        logger.RawException.ShouldBeFalse("意外异常诊断不得传递原始 Exception 对象");
        logger.LastFormatted.ShouldNotBeNull();
        logger.LastFormatted!.ShouldContain("Save");
        logger.LastFormatted!.ShouldContain("InvalidOperationException");
    }

    private sealed class MutableSettingsService(AppSettings initialSettings) : ISettingsService
    {
        public bool IsReadOnly => false;
        public AppSettings Current { get; private set; } = initialSettings;
        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = Current;
            Current = mutate(Current);
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingAutoStartService : IAutoStartService
    {
        public bool Enabled { get; set; }
        public Action? OnQuery { get; set; }
        public Action? BeforeSet { get; set; }

        public bool IsEnabled()
        {
            OnQuery?.Invoke();
            return Enabled;
        }

        public void SetEnabled(bool value)
        {
            BeforeSet?.Invoke();
            Enabled = value;
        }
    }

    [Fact]
    public async Task Save_ExternalIntentDuringApply_RetainsRetryEligibilityAndPendingStatus()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var initialSettings = new AppSettings
            {
                General = new GeneralSettings { LaunchAtStartup = false }
            };
            var state = new MutableSettingsService(initialSettings);
            var auto = new BlockingAutoStartService();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();

            auto.BeforeSet = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("BeforeSet wait timed out");
                }
            };

            using var theme = new ThemeService(state);
            using var coordinator = new GeneralSettingsSaveCoordinator(state, new TestSettingsEditCoordinator(state), auto, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            using var vm = new SettingsViewModel(state, theme, coordinator, new WpfUiDispatcher());

            vm.LaunchAtStartup = true;
            var saveTask = vm.ExecuteSaveAsync();

            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 在 SetEnabled 挂起期间，外部将已保存意图改为 false
                await state.UpdateAsync(s => s with { General = s.General with { LaunchAtStartup = false } });

                release.Set();
                await saveTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 验证应用完成后协调结果
                state.Current.General.LaunchAtStartup.ShouldBeFalse("已保存意图不得被陈旧目标覆盖");
                auto.Enabled.ShouldBeTrue("SetEnabled(true) 确实执行完成");
                vm.LaunchAtStartup.ShouldBeFalse("VM 选择应与最新已保存意图协调");
                vm.CanRetryAutoStart.ShouldBeTrue("实际注册与最新意图不一致时必须保留重试资格");
                vm.StatusMessage.ShouldBe(UiText.SettingsStatusSavedAutoStartStillPending);

                // 排干所有工作后再次确认
                await coordinator.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);
                await vm.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);

                vm.CanRetryAutoStart.ShouldBeTrue("排干后续后台查询后重试资格依然保留");
            }
            finally
            {
                release.Set();
                await saveTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await coordinator.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);
                await vm.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public async Task Retry_ExternalIntentDuringApply_RetainsRetryEligibilityAndPendingStatus()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var initialSettings = new AppSettings
            {
                General = new GeneralSettings { LaunchAtStartup = false }
            };
            var state = new MutableSettingsService(initialSettings);
            var auto = new BlockingAutoStartService { Enabled = false };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();

            var isInitialSave = true;
            auto.BeforeSet = () =>
            {
                if (isInitialSave)
                {
                    isInitialSave = false;
                    throw new System.IO.IOException("Simulated save auto start failure");
                }
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("BeforeSet wait timed out");
                }
            };

            using var theme = new ThemeService(state);
            using var coordinator = new GeneralSettingsSaveCoordinator(state, new TestSettingsEditCoordinator(state), auto, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            using var vm = new SettingsViewModel(state, theme, coordinator, new WpfUiDispatcher());

            // 首次保存：由于自启失败获得重试资格
            vm.LaunchAtStartup = true;
            await vm.ExecuteSaveAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            vm.CanRetryAutoStart.ShouldBeTrue("首次保存自启失败后应具有重试资格");

            // 发起重试
            var retryTask = vm.ExecuteRetryAutoStartAsync();

            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 在重试应用的 SetEnabled 挂起期间，外部把已保存意图改为 false
                await state.UpdateAsync(s => s with { General = s.General with { LaunchAtStartup = false } });

                release.Set();
                await retryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 验证重试完成后协调结果
                state.Current.General.LaunchAtStartup.ShouldBeFalse("已保存意图不得被覆盖");
                auto.Enabled.ShouldBeTrue("重试时的 SetEnabled(true) 确实执行完成");
                vm.LaunchAtStartup.ShouldBeFalse("VM 选择应与最新已保存意图协调");
                vm.CanRetryAutoStart.ShouldBeTrue("实际注册(true)与最新意图(false)不一致时必须保留重试资格");
                vm.StatusMessage.ShouldBe(UiText.SettingsStatusSavedAutoStartStillPending);

                // 排干所有工作后再次确认
                await coordinator.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);
                await vm.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);

                vm.CanRetryAutoStart.ShouldBeTrue("排干后续后台查询后重试资格依然保留");
            }
            finally
            {
                release.Set();
                await retryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await coordinator.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);
                await vm.WaitForActiveWorkAsync().WaitAsync(TestContext.Current.CancellationToken);
            }
        });
    }
}
