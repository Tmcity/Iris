using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

[Collection("AppShutdownerSharedState")]
public sealed class SettingsHotkeysIntegrationTests
{
    #region Test Doubles & Fixtures

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IrisHotkeysTests_" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class TestAppPaths(string dataDirectory) : IAppPaths
    {
        public bool IsPortable => false;
        public string DataDirectory { get; } = dataDirectory;
        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
        public string DatabaseFile => Path.Combine(DataDirectory, "history.db");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);

        public string? Get(string key)
        {
            Storage.TryGetValue(key, out var val);
            return val;
        }

        public void Set(string key, string value)
        {
            Storage[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Remove(string key)
        {
            var removed = Storage.TryRemove(key, out _);
            if (removed)
            {
                Changed?.Invoke(this, key);
            }
            return removed;
        }

        public event EventHandler<string>? Changed;
    }

    private sealed class CountingSettingsService : ISettingsService
    {
        private readonly ISettingsService _inner;
        public int UpdateCount { get; private set; }
        public bool ThrowOnUpdate { get; set; }
        public Action? OnUpdating { get; set; }

        public CountingSettingsService(ISettingsService inner)
        {
            _inner = inner;
        }

        public AppSettings Current => _inner.Current;

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);

        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpdate)
            {
                throw new IrisException(ErrorCode.StorageFailure, "Simulated disk write failure");
            }
            OnUpdating?.Invoke();
            UpdateCount++;
            await _inner.UpdateAsync(mutate, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public bool Enabled { get; set; }
        public bool IsEnabled() => Enabled;
        public void SetEnabled(bool enabled) => Enabled = enabled;
    }

    private sealed class FakeReceipt(Guid id) : ISettingsApplyReceipt
    {
        public Guid CommitId => id;
    }

    private sealed class TestActionHandler(IrisAction action) : IActionHandler
    {
        public IrisAction Action { get; } = action;
        public Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCaptureService : IHotkeyCaptureService
    {
        public int BeginCallCount { get; set; }
        public int StopCallCount { get; set; }
        public bool FailBegin { get; set; }
        public bool FailUnhookOnDispose { get; set; }
        public FakeCaptureSession? CurrentSession { get; private set; }

        public Task<IHotkeyCaptureSession> BeginAsync(nint windowHandle, CancellationToken cancellationToken = default)
        {
            BeginCallCount++;
            if (FailBegin)
            {
                throw new IrisException(ErrorCode.Unknown, "Hook 安装失败");
            }

            var session = new FakeCaptureSession(this, cancellationToken);
            CurrentSession = session;
            return Task.FromResult<IHotkeyCaptureSession>(session);
        }

        public Task StopAsync()
        {
            StopCallCount++;
            CurrentSession?.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeCaptureSession : IHotkeyCaptureSession
    {
        private readonly FakeCaptureService _service;
        private readonly TaskCompletionSource<HotkeyCaptureResult> _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid Id { get; } = Guid.NewGuid();
        public HotkeyCaptureProgress Current { get; set; } = new(1, true, HotkeyModifiers.None);
        public Task<HotkeyCaptureResult> Completion => _completionTcs.Task;
        public event EventHandler? ProgressChanged;

        public FakeCaptureSession(FakeCaptureService service, CancellationToken cancellationToken = default)
        {
            _service = service;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(Cancel);
            }
        }

        public void RaiseProgress(HotkeyCaptureProgress progress)
        {
            Current = progress;
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Complete(HotkeyCaptureResult result) => _completionTcs.TrySetResult(result);
        public void Cancel() => _completionTcs.TrySetResult(new HotkeyCaptureResult(HotkeyCaptureOutcome.Canceled));

        public ValueTask DisposeAsync()
        {
            Cancel();
            if (_service.FailUnhookOnDispose)
            {
                throw new IrisException(ErrorCode.Unknown, "卸载失败");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IntegrationEnvironment : IAsyncDisposable
    {
        public TempDirectory TempDir { get; } = new();
        public ServiceProvider ServiceProvider { get; }
        public CountingSettingsService SettingsService { get; }
        public MemorySecretStore SecretStore { get; }
        public FakeAutoStartService AutoStart { get; }
        public FakeNativeHotkeyAdapter FakeNative { get; }
        public HotkeyService HotkeyService { get; }
        public HotkeyCoordinator Coordinator { get; }
        public FakeCaptureService CaptureService { get; }
        public HotkeyRecordingCoordinator RecordingCoordinator { get; }
        public SettingsApplyReceiptManager ReceiptManager { get; }
        public GeneralSettingsSaveCoordinator SaveCoordinator { get; }
        public SettingsViewModel ViewModel { get; }

        public IntegrationEnvironment(AppSettings? initialSettings = null, IUiDispatcher? uiDispatcher = null)
        {
            var effectiveUiDispatcher = uiDispatcher ?? new ImmediateUiDispatcher();
            var paths = new TestAppPaths(TempDir.Path);
            SecretStore = new MemorySecretStore();
            AutoStart = new FakeAutoStartService();
            ReceiptManager = new SettingsApplyReceiptManager();
            FakeNative = new FakeNativeHotkeyAdapter();
            CaptureService = new FakeCaptureService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAppPaths>(paths);
            services.AddSingleton<ISecretStore>(SecretStore);
            services.AddSingleton<IAutoStartService>(AutoStart);
            services.AddSingleton<ISettingsApplyReceiptManager>(ReceiptManager);
            services.AddSingleton<IUiDispatcher>(effectiveUiDispatcher);
            services.AddSingleton<ThemeService>();

            var registry = Substitute.For<ITranslationProviderRegistry>();
            services.AddSingleton(registry);

            services.AddIrisInfrastructure();
            services.AddIrisCore();

            var bootstrapSp = services.BuildServiceProvider();
            var rawSettingsService = bootstrapSp.GetRequiredService<ISettingsService>();
            rawSettingsService.LoadAsync().GetAwaiter().GetResult();
            if (initialSettings != null)
            {
                rawSettingsService.UpdateAsync(_ => initialSettings).GetAwaiter().GetResult();
            }
            SettingsService = new CountingSettingsService(rawSettingsService);

            services.Remove(services.First(d => d.ServiceType == typeof(ISettingsService)));
            services.AddSingleton<ISettingsService>(SettingsService);

            HotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, FakeNative);
            services.AddSingleton<IHotkeyService>(HotkeyService);

            var handlers = new IActionHandler[]
            {
                new TestActionHandler(IrisAction.InputTranslate),
                new TestActionHandler(IrisAction.ClipboardTranslate),
                new TestActionHandler(IrisAction.OpenSettings)
            };
            var actionDispatcher = new ActionDispatcher(handlers, effectiveUiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            Coordinator = new HotkeyCoordinator(
                SettingsService,
                HotkeyService,
                actionDispatcher,
                handlers,
                effectiveUiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            RecordingCoordinator = new HotkeyRecordingCoordinator(Coordinator, CaptureService, NullLogger<HotkeyRecordingCoordinator>.Instance);

            services.AddSingleton(Coordinator);
            services.AddSingleton(RecordingCoordinator);

            services.AddScoped<GeneralSettingsSaveCoordinator>();
            services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
                SettingsService,
                sp.GetRequiredService<ThemeService>(),
                sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
                sp.GetRequiredService<ISettingsEditCoordinator>(),
                ReceiptManager,
                effectiveUiDispatcher,
                sp.GetRequiredService<IHotkeyBindingPolicy>(),
                Coordinator,
                RecordingCoordinator,
                NullLogger<SettingsViewModel>.Instance));

            ServiceProvider = services.BuildServiceProvider();
            SaveCoordinator = ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();
            ViewModel = ServiceProvider.GetRequiredService<SettingsViewModel>();
            ViewModel.SetWindowHandleProvider(() => 0x1234);
        }

        public async ValueTask DisposeAsync()
        {
            SaveCoordinator.StopAcceptanceAndCancelPreCommit();
            ViewModel.StopAcceptance();
            await RecordingCoordinator.DisposeAsync();
            await SaveCoordinator.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await ViewModel.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Coordinator.StopAcceptanceAndNativeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            HotkeyService.Dispose();
            await ServiceProvider.DisposeAsync();
            TempDir.Dispose();
        }
    }

    #endregion

    #region V01 - V09: 草稿、持久化与运行状态

    [Fact]
    public async Task V01_OnlyHotkeysModified_UnfocusedCandidatePersists_DoesNotTakeNoChangeFastPath()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        var newBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "T", true);

        // 未失焦录入值直接写入草稿
        inputItem.ApplyCapturedBinding(newBinding);

        env.ViewModel.IsDirty.ShouldBeTrue("修改快捷键后 ViewModel 必须进入 IsDirty");
        inputItem.IsDirty.ShouldBeTrue("目标快捷键与基线不同时草稿项必须 IsDirty");

        // 执行保存
        await env.ViewModel.ExecuteSaveAsync();

        // 验证：不走无变更跳过分支，发生且仅发生一次 UpdateAsync
        env.SettingsService.UpdateCount.ShouldBe(1, "只改快捷键时必须向 Core 提交变更，不得走无变更快速返回分支");
        env.ViewModel.StatusMessage.ShouldBe(UiText.SettingsStatusSaved);
        env.ViewModel.IsDirty.ShouldBeFalse();
        inputItem.IsDirty.ShouldBeFalse();
        inputItem.BaseBinding.ShouldBe(newBinding);

        // 验证持久化内容
        env.SettingsService.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(newBinding);
    }

    [Fact]
    public async Task V02_GeneralProviderHotkeySimultaneousEdit_SingleUpdateAsync_TransactionalFailureRollback()
    {
        var initialSettings = new AppSettings
        {
            General = new GeneralSettings { Theme = AppTheme.Light },
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "p1",
                        DisplayName = "Provider 1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://example.com",
                        Model = "gpt-model",
                        Order = 0,
                        OpenAIOptions = new()
                    }
                ]
            }
        };

        await using var env = new IntegrationEnvironment(initialSettings);
        await env.Coordinator.StartAsync();

        // 1. 三页同时修改
        env.ViewModel.Theme = AppTheme.Dark;
        env.ViewModel.Profiles[0].DisplayName = "Renamed Provider 1";
        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        inputItem.ApplyCapturedBinding(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K", true));

        env.ViewModel.IsDirty.ShouldBeTrue();

        // 执行保存
        await env.ViewModel.ExecuteSaveAsync();

        env.SettingsService.UpdateCount.ShouldBe(1, "三页同时修改必须由 GeneralSettingsSaveCoordinator 联合一次提交");
        env.SettingsService.Current.General.Theme.ShouldBe(AppTheme.Dark);
        env.SettingsService.Current.Translation.Providers[0].DisplayName.ShouldBe("Renamed Provider 1");
        env.SettingsService.Current.Hotkeys[IrisAction.InputTranslate].Key.ShouldBe("K");

        // 2. 模拟 UpdateAsync 失败：验证事务回滚不部分保存
        env.ViewModel.Theme = AppTheme.Light;
        env.ViewModel.Profiles[0].DisplayName = "Failed Update";
        inputItem.ApplyCapturedBinding(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "L", true));

        env.SettingsService.ThrowOnUpdate = true;
        await env.ViewModel.ExecuteSaveAsync();

        // 验证：设置文件内容不被部分更新
        env.SettingsService.Current.General.Theme.ShouldBe(AppTheme.Dark);
        env.SettingsService.Current.Translation.Providers[0].DisplayName.ShouldBe("Renamed Provider 1");
        env.SettingsService.Current.Hotkeys[IrisAction.InputTranslate].Key.ShouldBe("K");

        // ViewModel 保持 Dirty 以供重试
        env.ViewModel.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task V03_ExternalSettingsChanged_UneditedRowFollows_EditedRowPreserved_ReportsConflict()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        var clipItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.ClipboardTranslate);

        // 用户编辑了 ClipboardTranslate，保留未编辑的 InputTranslate
        var userClipBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "X", true);
        clipItem.ApplyCapturedBinding(userClipBinding);

        // 外部更新到达：修改 InputTranslate 与 ClipboardTranslate
        var extInput = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Y", true);
        var extClip = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Z", true);

        await env.SettingsService.UpdateAsync(s => s with
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(s.Hotkeys)
            {
                [IrisAction.InputTranslate] = extInput,
                [IrisAction.ClipboardTranslate] = extClip
            }
        }, TestContext.Current.CancellationToken);

        // 验证：未编辑行跟进新基线与目标
        inputItem.BaseBinding.ShouldBe(extInput);
        inputItem.TargetBinding.ShouldBe(extInput);
        inputItem.IsDirty.ShouldBeFalse();

        // 验证：已编辑行保留用户草稿目标，但基线更新
        clipItem.TargetBinding.ShouldBe(userClipBinding);
        clipItem.BaseBinding.ShouldBe(extClip);
        clipItem.IsDirty.ShouldBeTrue();

        // 再次外部更新：外部把 InputTranslate 改为与 clipItem 的草稿重复组合 Control+Shift+X
        await env.SettingsService.UpdateAsync(s => s with
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(s.Hotkeys)
            {
                [IrisAction.InputTranslate] = userClipBinding
            }
        }, TestContext.Current.CancellationToken);

        clipItem.HasDraftError.ShouldBeTrue("当外部更新导致同 Action 或组合冲突时，必须显示冲突错误");
        clipItem.DraftError.ShouldNotBeNull();
        clipItem.DraftError.ShouldContain("冲突");
    }

    [Fact]
    public async Task V04_ExecuteSaveAsync_WhileRecording_CancelsAndAwaitsRecording_FreezesAllDrafts()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        // 构造一个 dirty 修改以确保 ExecuteSaveAsync 进入保存提交并冻结草稿
        env.ViewModel.Theme = AppTheme.Dark;

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        var recordingTask = env.ViewModel.StartRecordingAsync(inputItem);

        env.ViewModel.IsAnyRecording.ShouldBeTrue();
        inputItem.IsRecording.ShouldBeTrue();

        bool wasFrozenDuringSave = false;
        bool attemptedEditDuringFreezeSucceeded = true;
        env.SettingsService.OnUpdating = () =>
        {
            wasFrozenDuringSave = inputItem.IsFrozen;
            var originalEnabled = inputItem.Enabled;
            // 冻结期间尝试修改草稿
            inputItem.Enabled = !originalEnabled;
            attemptedEditDuringFreezeSucceeded = inputItem.Enabled != originalEnabled;
        };

        // 录入中执行 ExecuteSaveAsync：必须先等待录入取消/清理完成再提交
        var saveTask = env.ViewModel.ExecuteSaveAsync();

        await saveTask;
        await recordingTask;

        wasFrozenDuringSave.ShouldBeTrue("保存提交期间快捷键草稿必须被冻结");
        attemptedEditDuringFreezeSucceeded.ShouldBeFalse("保存提交冻结期间禁止编辑草稿");

        env.ViewModel.IsAnyRecording.ShouldBeFalse();
        inputItem.IsRecording.ShouldBeFalse();
        inputItem.IsFrozen.ShouldBeFalse("保存完成后草稿解冻");
    }

    [Fact]
    public async Task V05_OpenSettingsMissingBaseline_DoesNotWriteOnOpen_DisablingPreservesKey_BackspaceClearsToDisabledEmpty()
    {
        // 构造缺失 OpenSettings 的初始配置
        var initialSettings = new AppSettings
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>
            {
                [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "I", true)
            }
        };

        await using var env = new IntegrationEnvironment(initialSettings);
        await env.Coordinator.StartAsync();

        var openSettingsItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.OpenSettings);

        // 缺失/未设置基线为 null
        openSettingsItem.BaseBinding.ShouldBeNull();
        openSettingsItem.TargetBinding.ShouldBeNull();
        openSettingsItem.Enabled.ShouldBeFalse();

        // 仅打开页面绝不写配置
        env.SettingsService.UpdateCount.ShouldBe(0);
        env.ViewModel.IsDirty.ShouldBeFalse();

        // 赋予有效组合后禁用：保留键位
        var binding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "O", true);
        openSettingsItem.ApplyCapturedBinding(binding);
        openSettingsItem.Enabled = false;

        openSettingsItem.TargetBinding.ShouldNotBeNull();
        openSettingsItem.TargetBinding.Key.ShouldBe("O", "普通禁用必须保留上次规范组合");
        openSettingsItem.TargetBinding.Enabled.ShouldBeFalse();

        // 保存生效
        await env.ViewModel.ExecuteSaveAsync();
        env.SettingsService.Current.Hotkeys[IrisAction.OpenSettings].Key.ShouldBe("O");
        env.SettingsService.Current.Hotkeys[IrisAction.OpenSettings].Enabled.ShouldBeFalse();

        // Backspace 清除为禁用空键
        openSettingsItem.Clear();
        openSettingsItem.TargetBinding.ShouldBe(new HotkeyBinding(HotkeyModifiers.None, string.Empty, false));
        openSettingsItem.IsDirty.ShouldBeTrue("从已有键位清空必须触发 IsDirty");

        // 保存并验证跨重载保持禁用空键
        await env.ViewModel.ExecuteSaveAsync();
        env.SettingsService.Current.Hotkeys[IrisAction.OpenSettings].ShouldBe(new HotkeyBinding(HotkeyModifiers.None, string.Empty, false));

        env.ViewModel.ExecuteReload();
        openSettingsItem.TargetBinding.ShouldBe(new HotkeyBinding(HotkeyModifiers.None, string.Empty, false));
    }

    [Fact]
    public async Task V06_DraftConflictsWithReservedNonEditableAction_ReportsReservedConflict_PreservesReservedAction()
    {
        var reservedBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "T", true);
        var initialSettings = new AppSettings
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>
            {
                [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "I", true),
                [IrisAction.TranslateText] = reservedBinding // 未开放动作的预留配置
            }
        };

        await using var env = new IntegrationEnvironment(initialSettings);
        await env.Coordinator.StartAsync();

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);

        // 将草稿设置为与未开放动作预留组合重复
        inputItem.ApplyCapturedBinding(reservedBinding);

        inputItem.HasDraftError.ShouldBeTrue("与未开放动作预留配置冲突必须提示错误");
        inputItem.DraftError.ShouldNotBeNull();
        inputItem.DraftError.ShouldContain("预留");
        inputItem.DraftError.ShouldNotContain("系统已被占用");

        // 校验保存被阻止
        await env.ViewModel.ExecuteSaveAsync();
        env.ViewModel.StatusMessage.ShouldBe("请先修正快捷键冲突或错误");
        env.SettingsService.Current.Hotkeys[IrisAction.TranslateText].ShouldBe(reservedBinding, "未开放动作配置绝不被删改");
    }

    [Fact]
    public async Task V07_JsonSavedSuccessfully_NativeRegistrationFails_DisplaysSavedPartialFailed_RetryHotkeysIndependentFromReceipts()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        // 模拟 Provider 存在未完成回执
        env.ReceiptManager.TryReserveSlot().ShouldBeTrue();
        env.ReceiptManager.ConvertReservationToPending(new FakeReceipt(Guid.NewGuid()), "Provider apply pending");
        env.ViewModel.CanRetryPendingApply.ShouldBeTrue();

        // 注入原生注册失败
        env.FakeNative.FailRegisterHotKey = true;

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        inputItem.ApplyCapturedBinding(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "U", true));

        await env.ViewModel.ExecuteSaveAsync();

        // 验证：JSON 保存成功，但页面提示快捷键未应用，出现快捷键独立重试
        env.SettingsService.UpdateCount.ShouldBe(1, "JSON 配置已正常落盘");
        env.ViewModel.StatusMessage.ShouldBe(UiText.SettingsHotkeyStatusSavedPartialFailed);
        env.ViewModel.CanRetryHotkeys.ShouldBeTrue();
        env.ViewModel.CanRetryPendingApply.ShouldBeTrue();

        // 恢复原生注册能力并执行快捷键独立重试
        env.FakeNative.FailRegisterHotKey = false;
        var initialJsonUpdateCount = env.SettingsService.UpdateCount;

        await env.ViewModel.ExecuteRetryHotkeysAsync();

        // 验证：重试快捷键不写 JSON，且与 Provider 回执重试彼此独立
        env.SettingsService.UpdateCount.ShouldBe(initialJsonUpdateCount, "快捷键重试不写 JSON");
        env.ReceiptManager.HasPendingReceipts.ShouldBeTrue("快捷键重试不触碰或重放 Provider 回执");
        env.ViewModel.CanRetryHotkeys.ShouldBeFalse();
    }

    [Fact]
    public async Task V08_OneActionFailedOthersSucceeded_ExternalTargetChanges_RetryOnlyLatestTarget_NoReRegistrationOfSucceeded()
    {
        await using var env = new IntegrationEnvironment(new AppSettings
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>
            {
                [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "I", true),
                [IrisAction.ClipboardTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "C", true)
            }
        });

        // 允许 Input 注册成功，阻止 Clipboard 注册
        env.FakeNative.FailRegisterHotKey = true;
        await env.Coordinator.StartAsync();

        var snapshot1 = env.Coordinator.GetRuntimeSnapshot();
        snapshot1.HasAnyFailure.ShouldBeTrue();

        // 外部目标变更到达
        await env.SettingsService.UpdateAsync(s => s with
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(s.Hotkeys)
            {
                [IrisAction.ClipboardTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D", true)
            }
        }, TestContext.Current.CancellationToken);

        env.FakeNative.FailRegisterHotKey = false;
        await env.Coordinator.RetryApplyAsync();

        var snapshot2 = env.Coordinator.GetRuntimeSnapshot();
        snapshot2.HasAnyFailure.ShouldBeFalse();
        snapshot2.Items[IrisAction.ClipboardTranslate].Status.ShouldBe(HotkeyRuntimeActionStatus.Registered);
    }

    [Fact]
    public async Task V09_UnregisterFailure_StateNotRegisteredOrDisabled_SavingWhilePausedShowsPending_ReopeningDoesNotConsumeIds()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        // 1. 模拟注销失败：发生注销失败的项不能显示为 Registered 或 Disabled
        try
        {
            env.FakeNative.FailUnregisterHotKey = true;
            await Should.ThrowAsync<Exception>(() => env.Coordinator.PauseAsync("FailOwner", TestContext.Current.CancellationToken));
            var snapshot = env.Coordinator.GetRuntimeSnapshot();
            var inputState = snapshot.Items[IrisAction.InputTranslate];
            inputState.Status.ShouldNotBe(HotkeyRuntimeActionStatus.Registered);
            inputState.Status.ShouldNotBe(HotkeyRuntimeActionStatus.Disabled);
            inputState.Status.ShouldBe(HotkeyRuntimeActionStatus.Failed);
        }
        finally
        {
            env.FakeNative.FailUnregisterHotKey = false;
        }

        // 2. 正常暂停并在暂停期间保存配置
        var pauseLease = await env.Coordinator.PauseAsync("TestOwner", TestContext.Current.CancellationToken);
        pauseLease.ShouldNotBeNull();

        // 暂停期间保存配置
        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        inputItem.ApplyCapturedBinding(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Q", true));
        await env.ViewModel.ExecuteSaveAsync();

        env.ViewModel.StatusMessage.ShouldBe(UiText.SettingsHotkeyStatusSavedPending);

        await pauseLease.DisposeAsync();

        // 3. 仅重新打开/构造设置页不触发额外注册或消耗 ID
        var regCountBefore = env.FakeNative.RegisterHotKeyCallCount;
        var vm2 = new SettingsViewModel(
            env.SettingsService,
            env.ServiceProvider.GetRequiredService<ThemeService>(),
            env.SaveCoordinator,
            env.ServiceProvider.GetRequiredService<ISettingsEditCoordinator>(),
            env.ReceiptManager,
            new ImmediateUiDispatcher(),
            env.ServiceProvider.GetRequiredService<IHotkeyBindingPolicy>(),
            env.Coordinator,
            env.RecordingCoordinator,
            NullLogger<SettingsViewModel>.Instance);

        env.FakeNative.RegisterHotKeyCallCount.ShouldBe(regCountBefore);
    }

    #endregion

    #region W01 - W04: WPF 控件交互与离屏渲染

    [Fact]
    public async Task W01_HotkeyRecorderBox_Interactions_LoadedDoesNotRecord_EnterStarts_EscCancelsWithoutClosing_BackspaceClears_PageSwitchCancels()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var env = new IntegrationEnvironment();
            await env.Coordinator.StartAsync();

            var window = new SettingsWindow
            {
                DataContext = env.ViewModel,
                Width = 760,
                Height = 520,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false
            };

            try
            {
                window.Show();
                env.ViewModel.CurrentPageKey = "hotkeys";
                window.UpdateLayout();

                // 1. 构造/Loaded 不触发录入
                env.ViewModel.IsAnyRecording.ShouldBeFalse();

                var recorderBox = FindVisualChild<HotkeyRecorderBox>(window);
                recorderBox.ShouldNotBeNull();

                var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
                var source = PresentationSource.FromVisual(recorderBox) ?? PresentationSource.FromVisual(window)!;

                // 2. 焦点控件按 Enter 开始录入
                recorderBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                env.ViewModel.IsAnyRecording.ShouldBeTrue("按 Enter 必须开始录入");

                // 3. 窗口按 Esc 取消录入且窗口存活
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                await env.ViewModel.WaitForActiveRecordingAsync().WaitAsync(TimeSpan.FromSeconds(3));
                env.ViewModel.IsAnyRecording.ShouldBeFalse("按 Esc 必须取消录入");
                window.IsLoaded.ShouldBeTrue("按 Esc 取消录入不得关闭设置窗口");

                // 4. Backspace 清空测试
                recorderBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Back)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                inputItem.TargetBinding.ShouldBe(new HotkeyBinding(HotkeyModifiers.None, string.Empty, false));
                inputItem.Enabled.ShouldBeFalse();

                // 5. 切页取消录入
                var recTask = env.ViewModel.StartRecordingAsync(inputItem);
                env.ViewModel.IsAnyRecording.ShouldBeTrue();

                env.ViewModel.CurrentPageKey = "general";
                await recTask.WaitAsync(TimeSpan.FromSeconds(3));
                env.ViewModel.IsAnyRecording.ShouldBeFalse("切换页面必须取消正在进行的录入");
            }
            finally
            {
                window.ForceClose = true;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task W02_ClosingWindow_WhileCleanupPending_OrCleanupFailed_PreservesOwnershipForRetry_OldCallbacksIgnored()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var env = new IntegrationEnvironment();
            await env.Coordinator.StartAsync();

            var window = new SettingsWindow
            {
                DataContext = env.ViewModel,
                Width = 760,
                Height = 520,
                Left = -20000,
                Top = -20000
            };

            var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
            var recTask = env.ViewModel.StartRecordingAsync(inputItem);

            // 模拟卸载失败
            env.CaptureService.FailUnhookOnDispose = true;

            // 尝试关闭窗口：录入控制器清理失败，保留资源
            window.Close();

            // 恢复卸载并重试清理
            env.CaptureService.FailUnhookOnDispose = false;
            await env.ViewModel.CancelRecordingAsync();
            await recTask;

            window.ForceClose = true;
            window.Close();
        });
    }

    [Fact]
    public async Task W03_AppBootstrapper_StopAsync_CaptureCleanupFails_PropagatesError_RootContainerNotDisposed_RetrySucceeds()
    {
        var originalShutdown = AppBootstrapper.AppShutdowner;
        int exitCalls = 0;
        try
        {
            AppBootstrapper.AppShutdowner = _ => Interlocked.Increment(ref exitCalls);

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var translationRegistry = Substitute.For<ITranslationProviderRegistry>();
            var databaseInitializer = Substitute.For<IDatabaseInitializer>();
            var themeService = new ThemeService(settingsService);
            var logLevelSwitch = new Serilog.Core.LoggingLevelSwitch();
            var logLevelController = new LogLevelController(settingsService, logLevelSwitch, NullLogger<LogLevelController>.Instance);
            var appLifetime = Substitute.For<IAppLifetime>();
            var trayController = new TrayIconController(appLifetime, settingsService, Substitute.For<IActionDispatcher>(), NullLogger<TrayIconController>.Instance)
            {
                SuppressIconCreationForTesting = true
            };
            var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);
            var host = Substitute.For<IHost>();

            var fakeCapture = Substitute.For<IHotkeyCaptureService>();
            fakeCapture.StopAsync().Returns(Task.FromException(new IrisException(ErrorCode.Unknown, "Hook cleanup failed")));

            var bootstrapper = new AppBootstrapper(
                settingsService,
                translationRegistry,
                databaseInitializer,
                logLevelController,
                themeService,
                trayController,
                trayNotificationService,
                host,
                NullLogger<AppBootstrapper>.Instance,
                new AppStartupInfo(Environment.TickCount64),
                hotkeyCoordinator: null,
                captureService: fakeCapture);

            // 第一次 StopAsync：捕获服务清理失败，错误必须可见传播，根容器不得提前 Dispose
            await Should.ThrowAsync<IrisException>(() => bootstrapper.StopAsync());
            await host.DidNotReceive().StopAsync(Arg.Any<CancellationToken>());
            exitCalls.ShouldBe(0);

            // 修复捕获服务并重试
            fakeCapture.StopAsync().Returns(Task.CompletedTask);
            await bootstrapper.StopAsync();

            // 验证重试后 Host 正常停止且 AppShutdowner 被调用
            await host.Received(1).StopAsync(Arg.Any<CancellationToken>());
            exitCalls.ShouldBe(1);
        }
        finally
        {
            AppBootstrapper.AppShutdowner = originalShutdown;
        }
    }

    [Fact]
    public async Task W04_OffScreenRendering_EightVariants_LightDark_DefaultMinSize_100And150Scale_NoClipping()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Iris_Hotkey_Screenshots");
            Directory.CreateDirectory(outputDir);

            var themes = new[] { AppTheme.Light, AppTheme.Dark };
            var sizes = new[] { (Width: 760.0, Height: 520.0), (Width: 600.0, Height: 440.0) };
            var scales = new[] { 1.0, 1.5 };

            int renderCount = 0;

            foreach (var theme in themes)
            {
                foreach (var (width, height) in sizes)
                {
                    foreach (var scale in scales)
                    {
                        await using var env = new IntegrationEnvironment(new AppSettings
                        {
                            General = new GeneralSettings { Theme = theme }
                        });
                        await env.Coordinator.StartAsync();

                        // 填充长组合与模拟错误状态以测试极端布局无裁切
                        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
                        inputItem.DraftError = "与系统预留快捷键冲突，请更换其他组合";
                        inputItem.RuntimeState = new HotkeyRuntimeItemState(
                            IrisAction.InputTranslate,
                            new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Win, "F10", true),
                            HotkeyRuntimeActionStatus.Failed,
                            "Ctrl+Shift+Alt+Win+F10",
                            "系统热键占用冲突",
                            1);

                        var window = new SettingsWindow
                        {
                            DataContext = env.ViewModel,
                            Width = width,
                            Height = height,
                            Left = -20000,
                            Top = -20000,
                            ShowActivated = false,
                            ShowInTaskbar = false
                        };

                        try
                        {
                            window.Show();
                            env.ViewModel.CurrentPageKey = "hotkeys";
                            window.UpdateLayout();

                            // 验证视觉树主要控件尺寸与边界
                            var recorderBox = FindVisualChild<HotkeyRecorderBox>(window);
                            recorderBox.ShouldNotBeNull();
                            recorderBox.ActualWidth.ShouldBeGreaterThan(100);
                            recorderBox.ActualHeight.ShouldBeGreaterThan(20);

                            var saveBtn = FindVisualChild<Wpf.Ui.Controls.Button>(window, b => (b.Content as string) == UiText.SettingsSave);
                            saveBtn.ShouldNotBeNull();
                            var saveRight = saveBtn.TransformToAncestor(window).Transform(new Point(saveBtn.ActualWidth, 0)).X;
                            saveRight.ShouldBeLessThanOrEqualTo(width + 2.0, "保存按钮未被窗口右边缘挤出或裁切");

                            // 离屏渲染为 PNG
                            int pixelWidth = (int)(width * scale);
                            int pixelHeight = (int)(height * scale);
                            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
                            rtb.Render(window);

                            var fileName = $"hotkeys_{theme}_{width}x{height}_{(int)(scale * 100)}pct.png";
                            var filePath = Path.Combine(outputDir, fileName);

                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(rtb));
                            using (var fileStream = File.Create(filePath))
                            {
                                encoder.Save(fileStream);
                            }

                            File.Exists(filePath).ShouldBeTrue();
                            new FileInfo(filePath).Length.ShouldBeGreaterThan(0);
                            renderCount++;
                        }
                        finally
                        {
                            window.ForceClose = true;
                            window.Close();
                        }
                    }
                }
            }

            renderCount.ShouldBe(8, "必须成功生成并验证全部 8 组离屏渲染图片");
        });
    }

    #endregion

    #region Helper Methods

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild && (predicate == null || predicate(typedChild)))
            {
                return typedChild;
            }

            var descendant = FindVisualChild<T>(child, predicate);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    #endregion
}
