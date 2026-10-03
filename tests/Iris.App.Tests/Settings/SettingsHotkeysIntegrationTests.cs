using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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

    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Func<Task>> _queue = new();
        public int QueueCount
        {
            get
            {
                lock (_queue) return _queue.Count;
            }
        }

        public bool CheckAccess() => true;

        public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken = default)
        {
            lock (_queue)
            {
                _queue.Enqueue(callback);
            }
            return Task.CompletedTask;
        }

        public void Drain()
        {
            while (true)
            {
                Func<Task>? item = null;
                lock (_queue)
                {
                    if (_queue.Count > 0)
                    {
                        item = _queue.Dequeue();
                    }
                }
                if (item == null) break;
                item().GetAwaiter().GetResult();
            }
        }
    }

    private sealed class FakeCaptureService : IHotkeyCaptureService
    {
        public int BeginCallCount { get; set; }
        public int StopCallCount { get; set; }
        public bool FailBegin { get; set; }
        public bool FailStop { get; set; }
        public bool FailUnhookOnDispose { get; set; }
        public TaskCompletionSource? CleanupGate { get; set; }
        public TaskCompletionSource? CleanupEntered { get; set; }
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
            if (FailStop || FailUnhookOnDispose)
            {
                throw new IrisException(ErrorCode.Unknown, "StopAsync 失败");
            }
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

        public async ValueTask DisposeAsync()
        {
            Cancel();
            _service.CleanupEntered?.TrySetResult();
            if (_service.CleanupGate != null)
            {
                await _service.CleanupGate.Task;
            }
            if (_service.FailUnhookOnDispose)
            {
                throw new IrisException(ErrorCode.Unknown, "卸载失败");
            }
        }
    }

    private sealed class TestNativeHookAdapter : INativeKeyboardHookAdapter
    {
        private readonly BlockingCollection<NativeMsg> _queue = [];
        public nint ValidHwnd { get; set; } = 0x1234;
        public nint ForegroundHwnd { get; set; } = 0x1234;
        public uint Pid { get; set; } = 4000;
        public bool SimulateGetMessageError { get; set; }

        public nint SetWindowsHook(NativeKeyboardHookProc proc) => 0x9999;
        public bool UnhookWindowsHook(nint hookHandle) => true;
        public nint CallNextHook(nint hookHandle, int nCode, nuint wParam, nint lParam) => 0;
        public nint GetForegroundWindow() => ForegroundHwnd;
        public uint GetWindowProcessId(nint windowHandle) => windowHandle == ValidHwnd ? Pid : 9999;
        public uint GetCurrentProcessId() => Pid;
        public bool IsWindow(nint windowHandle) => windowHandle == ValidHwnd;
        public bool IsKeyDown(int virtualKey) => false;
        public uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;
        public bool PostThreadQuitMessage(uint threadId)
        {
            try { _queue.TryAdd(new NativeMsg(0x0012, 0, 0)); return true; } catch { return false; }
        }
        public void EnsureThreadMessageQueueCreated() { while (_queue.TryTake(out _)) { } }
        public void ReleaseHookResources(nint hookHandle) { }
        public int GetMessage(out NativeMsg msg)
        {
            if (SimulateGetMessageError)
            {
                msg = default;
                return -1;
            }
            try
            {
                msg = _queue.Take();
                return msg.Message == 0x0012 ? 0 : 1;
            }
            catch
            {
                msg = new NativeMsg(0x0012, 0, 0);
                return 0;
            }
        }
        public int GetLastError() => 1400;
    }

    private sealed class IntegrationEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider _bootstrapSp;
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

        public IntegrationEnvironment(AppSettings? initialSettings = null, IUiDispatcher? uiDispatcher = null, IHotkeyCaptureService? captureService = null)
        {
            var effectiveUiDispatcher = uiDispatcher ?? new ImmediateUiDispatcher();
            var paths = new TestAppPaths(TempDir.Path);
            SecretStore = new MemorySecretStore();
            AutoStart = new FakeAutoStartService();
            ReceiptManager = new SettingsApplyReceiptManager();
            FakeNative = new FakeNativeHotkeyAdapter();
            CaptureService = (captureService as FakeCaptureService) ?? new FakeCaptureService();
            var effectiveCapture = captureService ?? CaptureService;

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

            _bootstrapSp = services.BuildServiceProvider();
            var rawSettingsService = _bootstrapSp.GetRequiredService<ISettingsService>();
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

            RecordingCoordinator = new HotkeyRecordingCoordinator(Coordinator, effectiveCapture, NullLogger<HotkeyRecordingCoordinator>.Instance);

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
            await _bootstrapSp.DisposeAsync();
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
        var originalClip = clipItem.BaseBinding;

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

        // 验证：已编辑行保留用户草稿目标与原基线，交给 Core 做乐观冲突校验，绝不静默覆盖基线
        clipItem.TargetBinding.ShouldBe(userClipBinding);
        clipItem.BaseBinding.ShouldBe(originalClip);
        clipItem.IsDirty.ShouldBeTrue();

        // 保存时 Core 检测到乐观并发冲突并拒绝保存
        await env.ViewModel.ExecuteSaveAsync();
        env.ViewModel.IsConflict.ShouldBeTrue("检测到乐观并发冲突");
        env.ViewModel.StatusMessage.ShouldNotBeNull();
        env.ViewModel.StatusMessage!.ShouldContain("保存冲突");

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

        // 设置清理闸门以验证真实清理阻塞时不能提前写入设置
        var cleanupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.CaptureService.CleanupGate = cleanupGate;
        env.CaptureService.CleanupEntered = cleanupEntered;

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

        // 等待清理进入阻塞
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // 清理阻塞期间：尚未写入 JSON/Settings，Save 按钮禁用
        env.SettingsService.UpdateCount.ShouldBe(0, "清理未完成时绝不能提前写入持久化设置");
        env.ViewModel.CanSave.ShouldBeFalse("清理未完成时禁止再次保存");

        // 放行清理闸门
        cleanupGate.TrySetResult();

        await saveTask;
        await recordingTask;

        env.SettingsService.UpdateCount.ShouldBe(1, "清理完成后正常提交持久化设置");
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
                window.UpdateLayout();

                var cancelBtn = FindVisualChild<Wpf.Ui.Controls.Button>(recorderBox, b => (b.Content as string) == UiText.SettingsHotkeyCancel);
                cancelBtn.ShouldNotBeNull("录入时录入按钮必须由 Style Trigger 切换为取消按钮");
                cancelBtn.Command.ShouldBe(inputItem.CancelRecordCommand, "取消按钮的 Command 必须为 CancelRecordCommand");

                // 3. 窗口按 Esc 取消录入且窗口存活
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                await env.ViewModel.WaitForActiveRecordingAsync().WaitAsync(TimeSpan.FromSeconds(3));
                env.ViewModel.IsAnyRecording.ShouldBeFalse("按 Esc 必须取消录入");
                window.IsLoaded.ShouldBeTrue("按 Esc 取消录入不得关闭设置窗口");
                window.UpdateLayout();

                var recNormalBtn = FindVisualChild<Wpf.Ui.Controls.Button>(recorderBox, b => (b.Content as string) == UiText.SettingsHotkeyRecord);
                recNormalBtn.ShouldNotBeNull("录入取消后按钮必须切回“录入”");

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

            bool isScopeDisposed = false;
            var scope = Substitute.For<IServiceScope>();
            scope.When(s => s.Dispose()).Do(_ => isScopeDisposed = true);

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

            var themeService = env.ServiceProvider.GetRequiredService<ThemeService>();
            var session = new SettingsWindowSession(scope, window, env.ViewModel, themeService, env.SaveCoordinator);

            try
            {
                window.Show();
                env.ViewModel.CurrentPageKey = "hotkeys";
                window.UpdateLayout();

                var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
                var recTask = env.ViewModel.StartRecordingAsync(inputItem);

                // 确认已真实进入录入并调用 BeginAsync
                env.CaptureService.BeginCallCount.ShouldBeGreaterThan(0, "录入必须真实调用了 BeginAsync");

                // 模拟卸载与停止失败
                env.CaptureService.FailUnhookOnDispose = true;
                env.CaptureService.FailStop = true;

                // 触发普通 window.Close()，关闭协议拦截关闭 (e.Cancel = true)，启动异步清理
                window.Close();

                // 观察清理失败异常：录入控制器清理失败，保留资源与作用域
                (session.CurrentCleanupTask != null).ShouldBeTrue("window.Close 必须已启动异步清理任务");
                await Should.ThrowAsync<InvalidOperationException>(() => session.CurrentCleanupTask!);

                window.IsLoaded.ShouldBeTrue("清理失败时窗口 HWND 保持存活，未被销毁");
                isScopeDisposed.ShouldBeFalse("清理失败时必须保留 DI 作用域与会话");
                session.IsOpen.ShouldBeTrue("清理失败时会话保持开启");
                env.ViewModel.HasFailedRecordingCleanup.ShouldBeTrue("清理失败必须在 ViewModel 中可见");
                inputItem.DraftError.ShouldBe(UiText.SettingsHotkeyCleanupFailed);
                env.Coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeTrue("清理未完成前热键必须保持暂停");

                // 验证重试按钮在视觉树中可见且可用
                window.UpdateLayout();
                var retryBtn = FindVisualChild<Button>(window, b => (b.Content as string) == UiText.SettingsRetryHotkeys);
                retryBtn.ShouldNotBeNull("重试按钮必须在视觉树中存在");
                retryBtn.Visibility.ShouldBe(Visibility.Visible, "清理失败时重试快捷键按钮必须对用户可见");
                retryBtn.IsEnabled.ShouldBeTrue("重试按钮必须处于可用状态");

                // 恢复卸载并点击重试按钮执行重试清理
                env.CaptureService.FailUnhookOnDispose = false;
                env.CaptureService.FailStop = false;
                retryBtn.Command.Execute(null);
                await env.ViewModel.WaitForActiveWorkAsync();

                env.ViewModel.HasFailedRecordingCleanup.ShouldBeFalse("重试成功后清理失败标志清除");
                env.Coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeFalse("清理成功后热键成功恢复");

                // 再次调用普通 window.Close()，此时清理成功，窗口与作用域安全关闭
                window.Close();
                (session.CurrentCleanupTask != null).ShouldBeTrue();
                await session.CurrentCleanupTask!;

                isScopeDisposed.ShouldBeTrue("清理成功后会话关闭并释放 DI 作用域");
                session.IsOpen.ShouldBeFalse("清理成功后会话关闭");
            }
            finally
            {
                window.ForceClose = true;
                window.Close();
            }
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

            var fakeCapture = new FakeCaptureService();
            var recordingCoordinator = new HotkeyRecordingCoordinator(
                hotkeyCoordinator: null,
                captureService: fakeCapture,
                logger: NullLogger<HotkeyRecordingCoordinator>.Instance);

            // 启动真实录入会话
            var recTask = recordingCoordinator.RecordAsync("test-bootstrapper", 0x1234, null, CancellationToken.None);
            _ = recTask;
            fakeCapture.CurrentSession.ShouldNotBeNull();

            // 模拟捕获服务清理失败
            fakeCapture.FailUnhookOnDispose = true;
            fakeCapture.FailStop = true;

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
                captureService: null,
                recordingCoordinator: recordingCoordinator);

            // 第一次 StopAsync：装配真实录入协调器，捕获服务清理失败，错误必须可见传播，根容器不得提前 Dispose，不产生死等
            await Should.ThrowAsync<IrisException>(() => bootstrapper.StopAsync());
            await host.DidNotReceive().StopAsync(Arg.Any<CancellationToken>());
            exitCalls.ShouldBe(0);

            // 修复捕获服务并重试
            fakeCapture.FailUnhookOnDispose = false;
            fakeCapture.FailStop = false;
            await recordingCoordinator.RetryCleanupAsync();

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
            var hashes = new Dictionary<(double Width, double Height, double Scale, AppTheme Theme), string>();

            int renderCount = 0;

            foreach (var theme in themes)
            {
                var wpfTheme = theme == AppTheme.Light ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark;
                var themeDict = System.Windows.Application.Current.Resources.MergedDictionaries.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault();
                if (themeDict != null)
                {
                    themeDict.Theme = wpfTheme;
                }
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(wpfTheme);

                foreach (var (width, height) in sizes)
                {
                    foreach (var scale in scales)
                    {
                        await using var env = new IntegrationEnvironment(new AppSettings
                        {
                            General = new GeneralSettings { Theme = theme }
                        });
                        await env.Coordinator.StartAsync();

                        // 1. 第一行：设置静态最长组合键 Ctrl+Alt+Shift+Win+F10
                        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
                        var longBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Win, "F10", true);
                        inputItem.ApplyCapturedBinding(longBinding);
                        inputItem.KeyComboText.ShouldBe("Ctrl+Alt+Shift+Win+F10");

                        // 2. 第二行：通过实际录入接线触发正在录入态，并推送最长候选组合 Ctrl+Alt+Shift+Win+F10
                        var clipItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.ClipboardTranslate);
                        var recTask = env.ViewModel.StartRecordingAsync(clipItem);
                        var session = env.CaptureService.CurrentSession;
                        session.ShouldNotBeNull();
                        session.RaiseProgress(new HotkeyCaptureProgress(
                            Revision: 2,
                            IsReady: true,
                            Modifiers: HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Win,
                            Key: "F10"));

                        clipItem.IsRecording.ShouldBeTrue("第二行必须处于真实接线的正在录入态");
                        clipItem.KeyComboText.ShouldBe("Ctrl+Alt+Shift+Win+F10");
                        clipItem.RecordingPrompt.ShouldBe(UiText.SettingsHotkeyRecordingPrompt);

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
                            wpfTheme = theme == AppTheme.Dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(wpfTheme);
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(window);
                            env.ViewModel.CurrentPageKey = "hotkeys";
                            window.UpdateLayout();

                            // 验证视觉树主要控件尺寸与边界
                            var recorderBoxes = FindVisualChildren<HotkeyRecorderBox>(window);
                            recorderBoxes.Count.ShouldBeGreaterThanOrEqualTo(2);

                            // 第一行（静态展示态）断言
                            var recorderBox1 = recorderBoxes[0];
                            var border1 = FindVisualChild<Border>(recorderBox1);
                            border1.ShouldNotBeNull();
                            var comboTextBlock1 = FindVisualChild<TextBlock>(recorderBox1, tb => tb.Text == "Ctrl+Alt+Shift+Win+F10" && tb.Visibility == Visibility.Visible);
                            comboTextBlock1.ShouldNotBeNull();
                            comboTextBlock1.ActualWidth.ShouldBeLessThanOrEqualTo(border1.ActualWidth - 12.0, "静态最长热键组合必须完全在 Border 内部显示，绝无裁切");

                            // 第二行（正在录入态）断言：垂直分行后，提示与组合互不挤压，完整组合与主键 F10 绝无裁切
                            var recorderBox2 = recorderBoxes[1];
                            var border2 = FindVisualChild<Border>(recorderBox2);
                            border2.ShouldNotBeNull();
                            double innerBorderWidth2 = border2.ActualWidth - 12.0;

                            var promptTb = FindVisualChild<TextBlock>(recorderBox2, tb => tb.Text == UiText.SettingsHotkeyRecordingPrompt && tb.Visibility == Visibility.Visible);
                            promptTb.ShouldNotBeNull();
                            promptTb.ActualWidth.ShouldBeLessThanOrEqualTo(innerBorderWidth2, "录入提示文本必须完全在 Border 内部显示，绝无裁切");

                            var comboTextBlock2 = FindVisualChild<TextBlock>(recorderBox2, tb => tb.Text == "Ctrl+Alt+Shift+Win+F10" && tb.Visibility == Visibility.Visible);
                            comboTextBlock2.ShouldNotBeNull();
                            comboTextBlock2.ActualWidth.ShouldBeGreaterThan(50);
                            comboTextBlock2.ActualWidth.ShouldBeLessThanOrEqualTo(innerBorderWidth2, "录入态最长热键组合必须完全在 Border 内部显示，主键 F10 绝无裁切");

                            var cancelBtn = FindVisualChild<Wpf.Ui.Controls.Button>(recorderBox2, b => (b.Content as string) == UiText.SettingsHotkeyCancel);
                            cancelBtn.ShouldNotBeNull();
                            cancelBtn.Visibility.ShouldBe(Visibility.Visible, "录入态取消按钮必须可见");
                            cancelBtn.IsEnabled.ShouldBeTrue("录入态取消按钮必须保持可操作");
                            var cancelRight = cancelBtn.TransformToAncestor(window).Transform(new Point(cancelBtn.ActualWidth, 0)).X;
                            cancelRight.ShouldBeLessThanOrEqualTo(width + 2.0, "取消按钮未被窗口右边缘挤出或裁切");

                            var saveBtn = FindVisualChild<Wpf.Ui.Controls.Button>(window, b => (b.Content as string) == UiText.SettingsSave);
                            saveBtn.ShouldNotBeNull();
                            var saveRight = saveBtn.TransformToAncestor(window).Transform(new Point(saveBtn.ActualWidth, 0)).X;
                            saveRight.ShouldBeLessThanOrEqualTo(width + 2.0, "保存按钮未被窗口右边缘挤出或裁切");

                            // 离屏渲染为 PNG：模拟真实操作系统桌面窗口背景，避免 Mica 半透明 Alpha 导致像素黑透
                            int pixelWidth = (int)(width * scale);
                            int pixelHeight = (int)(height * scale);
                            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
                            var bgVisual = new DrawingVisual();
                            using (var dc = bgVisual.RenderOpen())
                            {
                                var windowBg = theme == AppTheme.Light
                                    ? new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA))
                                    : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                                dc.DrawRectangle(windowBg, null, new Rect(0, 0, width, height));
                            }
                            rtb.Render(bgVisual);
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

                            using (var sha = SHA256.Create())
                            using (var stream = File.OpenRead(filePath))
                            {
                                var hash = Convert.ToHexString(sha.ComputeHash(stream));
                                hashes[(width, height, scale, theme)] = hash;
                            }

                            // 像素对比度检查：验证录入框背景在 Light 下为浅色、Dark 下为深色，绝无近白字叠近白底
                            int stride = pixelWidth * 4;
                            byte[] pixels = new byte[stride * pixelHeight];
                            rtb.CopyPixels(pixels, stride, 0);

                            var borderPoint = border2.TransformToAncestor(window).Transform(new Point(border2.ActualWidth - 6, border2.ActualHeight / 2));
                            int sampleX = Math.Clamp((int)(borderPoint.X * scale), 0, pixelWidth - 1);
                            int sampleY = Math.Clamp((int)(borderPoint.Y * scale), 0, pixelHeight - 1);
                            int bgIndex = sampleY * stride + sampleX * 4;
                            byte bgB = pixels[bgIndex];
                            byte bgG = pixels[bgIndex + 1];
                            byte bgR = pixels[bgIndex + 2];
                            double bgLuminance = 0.299 * bgR + 0.587 * bgG + 0.114 * bgB;

                            if (theme == AppTheme.Light)
                            {
                                bgLuminance.ShouldBeGreaterThan(130, "Light 主题下录入区域背景应为浅色");
                            }
                            else
                            {
                                bgLuminance.ShouldBeLessThan(120, "Dark 主题下录入区域背景应为深色");
                            }

                            renderCount++;
                        }
                        finally
                        {
                            await env.ViewModel.CancelRecordingAsync();
                            try
                            {
                                await recTask;
                            }
                            catch
                            {
                            }
                            window.ForceClose = true;
                            window.Close();
                        }
                    }
                }
            }

            renderCount.ShouldBe(8, "必须成功生成并验证全部 8 组离屏渲染图片");

            foreach (var (width, height) in sizes)
            {
                foreach (var scale in scales)
                {
                    var lightHash = hashes[(width, height, scale, AppTheme.Light)];
                    var darkHash = hashes[(width, height, scale, AppTheme.Dark)];
                    lightHash.ShouldNotBe(darkHash, $"在尺寸 {width}x{height} 缩放 {scale} 下，Light 与 Dark 主题的渲染图片 SHA256 哈希必须不同");
                }
            }
        });
    }

    [Fact]
    public async Task W04B_OffScreenRendering_FailedCleanupState_ShowsRetryButtonAndDraftError()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var env = new IntegrationEnvironment();
            await env.Coordinator.StartAsync();

            var openItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.OpenSettings);
            env.CaptureService.FailUnhookOnDispose = true;
            env.CaptureService.FailStop = true;
            var openRec = env.ViewModel.StartRecordingAsync(openItem);
            try
            {
                await env.ViewModel.CancelRecordingAsync();
            }
            catch (IrisException)
            {
            }
            try
            {
                await openRec;
            }
            catch
            {
            }

            env.ViewModel.HasFailedRecordingCleanup.ShouldBeTrue("录入清理失败必须在 ViewModel 中标记");
            openItem.DraftError.ShouldBe(UiText.SettingsHotkeyCleanupFailed);

            var window = new SettingsWindow
            {
                DataContext = env.ViewModel,
                Width = 600,
                Height = 440,
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

                var retryBtn = FindVisualChild<Button>(window, b => (b.Content as string) == UiText.SettingsRetryHotkeys);
                retryBtn.ShouldNotBeNull();
                retryBtn.Visibility.ShouldBe(Visibility.Visible, "清理失败态下重试按钮必须可见");

                var errorTb = FindVisualChild<TextBlock>(window, tb => tb.Text == UiText.SettingsHotkeyCleanupFailed);
                errorTb.ShouldNotBeNull();
                errorTb.Visibility.ShouldBe(Visibility.Visible, "清理失败错误提示必须可见");

                // 恢复服务并执行重试
                env.CaptureService.FailUnhookOnDispose = false;
                env.CaptureService.FailStop = false;
                await env.ViewModel.ExecuteRetryHotkeysAsync();
                window.UpdateLayout();

                env.ViewModel.HasFailedRecordingCleanup.ShouldBeFalse();
                retryBtn.Visibility.ShouldBe(Visibility.Collapsed, "重试成功后重试按钮必须隐藏");
            }
            finally
            {
                window.ForceClose = true;
                window.Close();
            }
        });
    }

    #endregion

    #region R2-03: 保存恢复 UI 线程、录入身份与配置消费

    [Fact]
    public async Task R203_ExecuteSaveAsync_UnderRealDispatcher_RunsBeforeSaveOnUiThread_FlushesUnfocusedInput_AndBlocksOnFailedCleanup()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var wpfDispatcher = new WpfUiDispatcher();
            await using var env = new IntegrationEnvironment(uiDispatcher: wpfDispatcher);
            await env.Coordinator.StartAsync();

            bool beforeSaveRanOnUiThread = false;
            env.ViewModel.BeforeSave = () =>
            {
                beforeSaveRanOnUiThread = Application.Current.Dispatcher.CheckAccess();
            };

            var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
            var newBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K", true);
            inputItem.ApplyCapturedBinding(newBinding);

            // 1. 正常保存：BeforeSave 在 UI 线程执行，UpdateCount 为 1
            await env.ViewModel.ExecuteSaveAsync();
            beforeSaveRanOnUiThread.ShouldBeTrue("BeforeSave 必须在 UI 线程执行");
            env.SettingsService.UpdateCount.ShouldBe(1, "正常保存必须发生一次 UpdateAsync");
            env.SettingsService.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(newBinding);
            env.ViewModel.StatusMessage.ShouldBe(UiText.SettingsStatusSaved);

            // 2. 录入清理失败时：保存必须阻塞并禁止提交
            var pendingBinding = new HotkeyBinding(HotkeyModifiers.Control, "P", true);
            inputItem.ApplyCapturedBinding(pendingBinding);

            env.CaptureService.FailUnhookOnDispose = true;
            env.CaptureService.FailStop = true;
            var recTask = env.ViewModel.StartRecordingAsync(inputItem);
            _ = recTask;

            // 执行保存：应等待录入清理，清理失败后终止提交，不调用 Core UpdateAsync
            await env.ViewModel.ExecuteSaveAsync();

            env.ViewModel.HasFailedRecordingCleanup.ShouldBeTrue("录入清理失败必须在 ViewModel 中标记");
            env.SettingsService.UpdateCount.ShouldBe(1, "录入清理失败时必须禁止向 Core 提交，UpdateCount 不得增加");
            env.SettingsService.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(newBinding, "未成功提交的草稿不得写入已持久化配置");
            env.ViewModel.CanSave.ShouldBeFalse("清理失败时 CanSave 必须为 false");

            // 恢复并重试清理，以便顺利 Dispose
            env.CaptureService.FailUnhookOnDispose = false;
            env.CaptureService.FailStop = false;
            await env.ViewModel.ExecuteRetryHotkeysAsync();
        });
    }

    [Fact]
    public async Task R203_SettingsCallback_Queued_ThenPaused_ThenResumed_OldCallbackArrives_FutureChangedStillProcessed()
    {
        var queuedDispatcher = new QueuedUiDispatcher();
        await using var env = new IntegrationEnvironment(uiDispatcher: queuedDispatcher);
        await env.Coordinator.StartAsync();
        queuedDispatcher.Drain();

        var initialBinding = env.SettingsService.Current.Hotkeys[IrisAction.InputTranslate];

        // 1. 设置变更触发 Changed，调度在 QueuedUiDispatcher 中排队，尚未执行
        var intermediateBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "X", true);
        await env.SettingsService.UpdateAsync(s =>
        {
            var dict = new Dictionary<IrisAction, HotkeyBinding>(s.Hotkeys)
            {
                [IrisAction.InputTranslate] = intermediateBinding
            };
            return s with { Hotkeys = dict };
        }, TestContext.Current.CancellationToken);
        queuedDispatcher.QueueCount.ShouldBeGreaterThan(0, "设置变更回调已排队");

        // 2. 发生暂停 (例如开始录入快捷键)
        var pauseLease = await env.Coordinator.PauseAsync("Recording", TestContext.Current.CancellationToken);
        env.Coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeTrue();

        // 3. 恢复快捷键
        await pauseLease.DisposeAsync();
        env.Coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeFalse();

        // 4. 旧代次的回调到达并执行，旧代次退出必须安全重置 _isProcessingSettings
        queuedDispatcher.Drain();

        // 5. 再次发生设置变更（例如用户保存目标 K）
        var targetBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K", true);
        await env.SettingsService.UpdateAsync(s =>
        {
            var dict = new Dictionary<IrisAction, HotkeyBinding>(s.Hotkeys)
            {
                [IrisAction.InputTranslate] = targetBinding
            };
            return s with { Hotkeys = dict };
        }, TestContext.Current.CancellationToken);
        queuedDispatcher.Drain();

        // 6. 验证新设置被成功调度并应用，未因旧回调迟到而卡住
        var snapshot = env.Coordinator.GetRuntimeSnapshot();
        snapshot.Items[IrisAction.InputTranslate].TargetBinding.ShouldBe(targetBinding, "旧回调延迟退出后，后续设置变更仍必须能够被正确调度与处理");
    }

    [Fact]
    public async Task R203_HotkeyRecording_OldProgressQueued_FirstEnds_SameRowReRecorded_OldProgressRejected()
    {
        var queuedDispatcher = new QueuedUiDispatcher();
        await using var env = new IntegrationEnvironment(uiDispatcher: queuedDispatcher);

        var item = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);

        // 1. 同一行第一次开始录入
        var recTask1 = env.ViewModel.StartRecordingAsync(item);
        _ = recTask1;
        queuedDispatcher.Drain();
        item.IsRecording.ShouldBeTrue();
        var session1 = env.CaptureService.CurrentSession;
        session1.ShouldNotBeNull();

        // 2. 产生进度事件并排队在 Dispatcher 中（不 drain）
        session1.RaiseProgress(new HotkeyCaptureProgress(2, false, HotkeyModifiers.Control, "X"));
        queuedDispatcher.QueueCount.ShouldBeGreaterThan(0, "第一轮进度通知已排队");

        // 3. 第一次录入取消并结束
        await env.ViewModel.CancelRecordingAsync();
        await recTask1;
        queuedDispatcher.Drain();
        item.IsRecording.ShouldBeFalse();

        // 4. 同一行第二次开始录入
        var recTask2 = env.ViewModel.StartRecordingAsync(item);
        _ = recTask2;
        queuedDispatcher.Drain();
        item.IsRecording.ShouldBeTrue();
        var session2 = env.CaptureService.CurrentSession;
        session2.ShouldNotBeNull();
        session2.ShouldNotBeSameAs(session1);

        // 5. 模拟来自 session1 的迟到进度排队到达并执行
        session1.RaiseProgress(new HotkeyCaptureProgress(2, false, HotkeyModifiers.Control, "X"));
        queuedDispatcher.Drain();

        // 6. 验证：旧代次进度被拒绝，未将预览改为旧 Ctrl+X
        item.KeyComboText.ShouldNotBe("Ctrl+X", "旧录入会话的进度通知必须被拒绝，绝不能污染新录入会话的预览");

        // 7. 第二轮正常完成
        var newBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Y", true);
        session2.Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Captured, newBinding));
        await recTask2;
        queuedDispatcher.Drain();

        item.IsRecording.ShouldBeFalse();
        item.TargetBinding.ShouldBe(newBinding, "第二轮录入结果应正确应用");
    }

    #endregion

    #region R3: 录入失败恢复、结果归属与终态验证

    [Fact]
    public async Task R301_BeginOrdinaryFailure_RollbackSucceeded_DoesNotLockSettings_AllowsImmediateRecordingInSameWindow()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        var originalBinding = inputItem.TargetBinding;

        // 模拟 Begin 失败但回滚成功 (普通失败)
        env.CaptureService.FailBegin = true;
        env.CaptureService.FailStop = false;

        await env.ViewModel.StartRecordingAsync(inputItem);

        // 断言：清理成功后释放活动状态，不锁死设置窗口
        env.ViewModel.HasFailedRecordingCleanup.ShouldBeFalse("回滚成功时不应标记为清理失败");
        env.ViewModel.IsAnyRecording.ShouldBeFalse("普通失败后活动录入项必须清空");
        env.ViewModel.CanEdit.ShouldBeTrue("普通失败后同一窗口中 CanEdit 必须为 true，用户可立即再次录入");
        env.ViewModel.CanRetryHotkeysOrCleanup.ShouldBeFalse("普通失败非物理清理失败，不应展示重试命令");
        inputItem.IsRecording.ShouldBeFalse();
        inputItem.DraftError.ShouldNotBeNull();
        inputItem.DraftError.ShouldContain("Hook 安装失败");
        inputItem.TargetBinding.ShouldBe(originalBinding, "普通失败时原草稿必须保持不变");

        // 撤除故障，在同一窗口立即录入第二项 ClipboardTranslate
        env.CaptureService.FailBegin = false;
        var clipItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.ClipboardTranslate);
        var recTask = env.ViewModel.StartRecordingAsync(clipItem);
        clipItem.IsRecording.ShouldBeTrue();

        var session = env.CaptureService.CurrentSession;
        session.ShouldNotBeNull();
        var newBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "U", true);
        session.Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Captured, newBinding));
        await recTask;

        clipItem.IsRecording.ShouldBeFalse();
        clipItem.TargetBinding.ShouldBe(newBinding, "同窗口后续录入应成功应用");
        clipItem.DraftError.ShouldBeNull();
        env.ViewModel.CanEdit.ShouldBeTrue();
    }

    [Fact]
    public async Task R301_PlatformLogicFailedOutcome_WithRealCaptureServiceAndFakeNative_ReleasesActiveRecordingState()
    {
        var adapter = new TestNativeHookAdapter();
        var realService = new HotkeyCaptureService(adapter, null, NullLogger<HotkeyCaptureService>.Instance);
        await using var env = new IntegrationEnvironment(captureService: realService);
        await env.Coordinator.StartAsync();

        var item = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        var originalBinding = item.TargetBinding;

        // 1. 模拟前台窗口校验失败 (Begin 阶段)
        adapter.ForegroundHwnd = 0x8888; // 目标为 0x1234
        await env.ViewModel.StartRecordingAsync(item);

        env.ViewModel.HasFailedRecordingCleanup.ShouldBeFalse();
        env.ViewModel.IsAnyRecording.ShouldBeFalse();
        env.ViewModel.CanEdit.ShouldBeTrue("前台校验失败释放后 CanEdit 必须恢复");
        item.DraftError.ShouldNotBeNull();
        item.TargetBinding.ShouldBe(originalBinding);

        // 2. 模拟录入中逻辑失败 (GetMessage 异常终止 -> Outcome=Failed)
        adapter.ForegroundHwnd = 0x1234;
        adapter.SimulateGetMessageError = true;

        var recTask = env.ViewModel.StartRecordingAsync(item);
        adapter.PostThreadQuitMessage(1); // 唤醒 GetMessage 使其返回 -1
        await recTask;

        env.ViewModel.HasFailedRecordingCleanup.ShouldBeFalse();
        env.ViewModel.IsAnyRecording.ShouldBeFalse();
        env.ViewModel.CanEdit.ShouldBeTrue("逻辑 Failed 结果后活动项释放，CanEdit 恢复");
        item.DraftError.ShouldNotBeNull();
        item.TargetBinding.ShouldBe(originalBinding);
    }

    [Fact]
    public async Task R302_TwoActionsTwoContexts_FirstSuccess_SecondBeginAndRollbackFailed_RetryDoesNotReplayFirstResult()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);
        var clipItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.ClipboardTranslate);
        var originalClipBinding = clipItem.TargetBinding;

        // 1. InputTranslate 成功录入 Ctrl+Shift+X
        var rec1 = env.ViewModel.StartRecordingAsync(inputItem);
        var session1 = env.CaptureService.CurrentSession;
        session1.ShouldNotBeNull();
        var inputBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "X", true);
        session1.Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Captured, inputBinding));
        await rec1;

        inputItem.KeyComboText.ShouldBe("Ctrl+Shift+X");
        inputItem.TargetBinding.ShouldBe(inputBinding);

        // 2. ClipboardTranslate 发生 Begin 失败且回滚失败
        env.CaptureService.FailBegin = true;
        env.CaptureService.FailStop = true;

        await env.ViewModel.StartRecordingAsync(clipItem);

        env.ViewModel.HasFailedRecordingCleanup.ShouldBeTrue("Begin+Stop 均失败应标记为清理失败");
        env.ViewModel.CanRetryHotkeysOrCleanup.ShouldBeTrue();
        clipItem.DraftError.ShouldBe(UiText.SettingsHotkeyCleanupFailed);
        clipItem.TargetBinding.ShouldBe(originalClipBinding, "失败期间 ClipboardTranslate 原草稿不得被修改");

        // 3. 撤除故障并执行重试命令
        env.CaptureService.FailBegin = false;
        env.CaptureService.FailStop = false;

        await env.ViewModel.ExecuteRetryHotkeysAsync();

        // 4. 核心断言：重试成功后，Action 2 绝不能被写为 Action 1 的 Ctrl+Shift+X！
        env.ViewModel.HasFailedRecordingCleanup.ShouldBeFalse("重试成功后清理失败标记必须清除");
        clipItem.DraftError.ShouldBeNull();
        clipItem.TargetBinding.ShouldBe(originalClipBinding, "重试成功后，无本次 captured 结果的 Action 2 原草稿必须保持不变，绝不回放 Action 1 的旧值");
        clipItem.KeyComboText.ShouldNotBe("Ctrl+Shift+X");
        env.ViewModel.CanEdit.ShouldBeTrue();
    }

    [Fact]
    public async Task R302_SingleContext_CapturedResult_AppliedOnceOnRetryCleanup()
    {
        await using var env = new IntegrationEnvironment();
        await env.Coordinator.StartAsync();

        var inputItem = env.ViewModel.Hotkeys.First(h => h.Action == IrisAction.InputTranslate);

        // 录入时捕获有效组合 Ctrl+Alt+Shift+Y，但清理时发生 unhook 失败
        env.CaptureService.FailUnhookOnDispose = true;

        var recTask = env.ViewModel.StartRecordingAsync(inputItem);
        var session = env.CaptureService.CurrentSession;
        session.ShouldNotBeNull();
        var yBinding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "Y", true);
        session.Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Captured, yBinding));

        try
        {
            await recTask;
        }
        catch
        {
        }

        env.ViewModel.HasFailedRecordingCleanup.ShouldBeTrue();
        inputItem.DraftError.ShouldBe(UiText.SettingsHotkeyCleanupFailed);

        // 撤除故障并重试
        env.CaptureService.FailUnhookOnDispose = false;
        await env.ViewModel.ExecuteRetryHotkeysAsync();

        // 核心断言：同一 context 的有效 Captured 结果在重试清理成功后被正确应用一次
        env.ViewModel.HasFailedRecordingCleanup.ShouldBeFalse();
        inputItem.DraftError.ShouldBeNull();
        inputItem.TargetBinding.ShouldBe(yBinding);
        inputItem.KeyComboText.ShouldBe("Ctrl+Alt+Shift+Y");
    }

    #endregion

    #region Helper Methods

    private static List<T> FindVisualChildren<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        var list = new List<T>();
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild && (predicate == null || predicate(typedChild)))
            {
                list.Add(typedChild);
            }
            list.AddRange(FindVisualChildren(child, predicate));
        }
        return list;
    }

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
