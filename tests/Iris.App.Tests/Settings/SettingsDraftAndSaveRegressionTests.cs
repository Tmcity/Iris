using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

public sealed class SettingsDraftAndSaveRegressionTests
{
    private static TranslationProviderProfile CreateSampleProfile(string id, int order = 0) => new()
    {
        Id = id,
        Kind = TranslationProviderKind.OpenAICompatible,
        DisplayName = id,
        BaseUrl = "https://fake.invalid/v1",
        Model = "fake-model",
        Order = order,
        OpenAIOptions = new(),
    };

    private sealed class TestAppPaths(string dataDirectory) : IAppPaths
    {
        public bool IsPortable => false;
        public string DataDirectory { get; } = dataDirectory;
        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
        public string DatabaseFile => Path.Combine(DataDirectory, "history.db");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        public readonly MutableSettingsService Settings;
        public readonly TestEditCoordinator Edit = new();
        public readonly TestAutoStartService Auto = new();
        public readonly SettingsApplyReceiptManager Receipts = new();
        public readonly GeneralSettingsSaveCoordinator SaveCoordinator;
        public readonly SettingsViewModel Vm;
        public readonly ThemeService Theme;

        public TestFixture(params TranslationProviderProfile[] profiles)
        {
            var initialSettings = new AppSettings
            {
                Translation = new TranslationSettings
                {
                    Providers = profiles.Length > 0 ? profiles : [CreateSampleProfile("p1")]
                }
            };
            Settings = new MutableSettingsService(initialSettings);
            Theme = new ThemeService(Settings, visualThemeApplier: _ => { });
            SaveCoordinator = new GeneralSettingsSaveCoordinator(
                Settings,
                Edit,
                Auto,
                Receipts,
                NullLogger<GeneralSettingsSaveCoordinator>.Instance);

            var dispatcher = Dispatcher.CurrentDispatcher;
            Vm = new SettingsViewModel(
                Settings,
                Theme,
                SaveCoordinator,
                Edit,
                Receipts,
                new WpfUiDispatcher(() => dispatcher),
                NullLogger<SettingsViewModel>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            SaveCoordinator.StopAcceptanceAndCancelPreCommit();
            Vm.StopAcceptance();
            await SaveCoordinator.WaitForActiveWorkAsync();
            await Vm.WaitForActiveWorkAsync();
            Vm.Dispose();
            SaveCoordinator.Dispose();
            Theme.Dispose();
        }
    }

    private sealed class MutableSettingsService : ISettingsService
    {
        public AppSettings Current { get; private set; }
        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public MutableSettingsService(AppSettings initial)
        {
            Current = initial;
        }

        public void Publish(AppSettings next)
        {
            var old = Current;
            Current = next;
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, next));
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            Publish(mutate(Current));
            return Task.CompletedTask;
        }
    }

    private sealed class TestAutoStartService : IAutoStartService
    {
        private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
        public bool QueriedOnUi { get; private set; }
        public bool Enabled { get; set; }
        public bool ThrowOnSet { get; set; }
        public Func<bool, Task>? OnSetEnabled { get; set; }

        public bool IsEnabled()
        {
            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                QueriedOnUi = true;
            }
            return Enabled;
        }

        public void SetEnabled(bool value)
        {
            if (OnSetEnabled != null)
            {
                OnSetEnabled(value).GetAwaiter().GetResult();
            }
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("Simulated auto start error");
            }
            Enabled = value;
        }
    }

    private sealed class ControlledEditObserver : IProviderConfigurationObserver
    {
        public bool FailRefresh { get; set; }
        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (FailRefresh)
            {
                throw new InvalidOperationException("Simulated observer refresh failure");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CountingSettingsService : ISettingsService
    {
        private readonly ISettingsService _inner;
        public int UpdateCount { get; private set; }

        public CountingSettingsService(ISettingsService inner) => _inner = inner;
        public AppSettings Current => _inner.Current;
        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }
        public Task LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);
        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            await _inner.UpdateAsync(mutate, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);
        public List<string> SetCalls { get; } = [];
        public List<string> RemoveCalls { get; } = [];

        public string? Get(string key)
        {
            Storage.TryGetValue(key, out var val);
            return val;
        }

        public void Set(string key, string value)
        {
            SetCalls.Add(key);
            Storage[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Remove(string key)
        {
            RemoveCalls.Add(key);
            var removed = Storage.TryRemove(key, out _);
            if (removed)
            {
                Changed?.Invoke(this, key);
            }
            return removed;
        }

        public event EventHandler<string>? Changed;
    }

    private sealed class FakeTranslationProvider(ProviderDescriptor descriptor) : ITranslationProvider
    {
        public ProviderDescriptor Descriptor { get; } = descriptor;
        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TranslationChunk("translated text");
        }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(50), "sample"));
    }

    private sealed class FakeTranslationProviderFactory : ITranslationProviderFactory
    {
        public TranslationProviderKind Kind => TranslationProviderKind.OpenAICompatible;
        public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials)
        {
            var descriptor = new ProviderDescriptor(
                profile.Id,
                profile.DisplayName,
                profile.Kind.ToString(),
                profile.Model,
                IsConfigured: !string.IsNullOrWhiteSpace(credentials.ApiKey));
            return new FakeTranslationProvider(descriptor);
        }
    }

    private sealed class TestScopedWitness : IDisposable
    {
        public static readonly List<TestScopedWitness> Created = [];
        public bool IsDisposed { get; private set; }

        public TestScopedWitness()
        {
            lock (Created)
            {
                Created.Add(this);
            }
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class TestReceipt : ISettingsApplyReceipt
    {
        public Guid CommitId { get; } = Guid.NewGuid();
    }

    private sealed class TestEditCoordinator : ISettingsEditCoordinator
    {
        public IOcrDraftTestSession CreateOcrDraftTest(OcrProviderDraft draft) => throw new NotSupportedException();

        public int TestCalls { get; private set; }
        public int ListCalls { get; private set; }
        public SettingsEditPlan? LastPlan { get; private set; }
        public SettingsEditResult CommitResult = new(SettingsEditStatus.Applied);

        public Func<TranslationProviderDraft, CancellationToken, Task<ProviderTestResult>>? OnTestDraft;
        public Func<TranslationProviderDraft, CancellationToken, Task<IReadOnlyList<string>>>? OnListDraftModels;

        public Func<SettingsEditPlan, CancellationToken, Task<SettingsEditResult>>? OnCommitWithToken;
        public Func<SettingsEditPlan, Task<SettingsEditResult>>? OnCommit
        {
            get => null;
            set => OnCommitWithToken = value != null ? (p, _) => value(p) : null;
        }
        public Func<ISettingsApplyReceipt, Task<SettingsApplyResult>>? OnRetryApply;

        public Task<SettingsEditResult> CommitAsync(SettingsEditPlan plan, CancellationToken cancellationToken = default)
        {
            LastPlan = plan;
            return OnCommitWithToken != null ? OnCommitWithToken(plan, cancellationToken) : Task.FromResult(CommitResult);
        }

        public Task<SettingsApplyResult> RetryApplyAsync(ISettingsApplyReceipt receipt, CancellationToken cancellationToken = default)
        {
            return OnRetryApply != null ? OnRetryApply(receipt) : Task.FromResult(new SettingsApplyResult(true));
        }

        public Task<ProviderTestResult> TestDraftAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default)
        {
            TestCalls++;
            return OnTestDraft != null
                ? OnTestDraft(draft, cancellationToken)
                : Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(5), "ok"));
        }

        public Task<IReadOnlyList<string>> ListDraftModelsAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return OnListDraftModels != null
                ? OnListDraftModels(draft, cancellationToken)
                : Task.FromResult<IReadOnlyList<string>>(["model-1", "model-2"]);
        }
    }

    // 1. 测试→保存→再次测试/列模型（涵盖成功、未提交、校验失败；排干后可重用；窗口停止后拒绝；队列重入与集合受控保护）
    [Fact]
    public async Task Scenario1_TestThenSave_ReusesQueueAcrossAllSaveStatuses_AndRejectsAfterShutdown()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // Case 1a: 保存成功后再次测试与获取模型，新 SelectedProfile 属于 Profiles 且未冻结，后续编辑正常受理
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var initialProfile = f.Vm.SelectedProfile!;
                await f.Vm.ExecuteTestDraftAsync();
                f.Vm.SelectedProfile!.DisplayName = "changed-1";
                f.Edit.OnCommit = plan =>
                {
                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            Providers = plan.TranslationChanges.Select(x => x.TargetProfile!).ToArray()
                        }
                    });
                    return Task.FromResult(new SettingsEditResult(SettingsEditStatus.Applied));
                };
                await f.Vm.ExecuteSaveAsync();

                f.Vm.Profiles.Contains(f.Vm.SelectedProfile!).ShouldBeTrue("保存成功后 SelectedProfile 必须属于新 Profiles 集合");
                ReferenceEquals(initialProfile, f.Vm.SelectedProfile).ShouldBeFalse("保存成功后必须重新绑定新草稿实例");
                f.Vm.SelectedProfile.IsFrozen.ShouldBeFalse("保存成功后 SelectedProfile 必须解除冻结");

                f.Vm.SelectedProfile.DisplayName = "next-edit";
                f.Vm.SelectedProfile.DisplayName.ShouldBe("next-edit", "新选中项必须允许后续编辑");

                await f.Vm.ExecuteTestDraftAsync();
                f.Edit.TestCalls.ShouldBe(2, "保存成功后测试队列必须保持开放并可再次发起测试");

                await f.Vm.ExecuteListModelsAsync();
                f.Edit.ListCalls.ShouldBe(1, "保存成功后获取模型列表必须可正常发起");
            }

            // Case 1b: 保存未提交（Uncommitted）后仍可再次测试；保存冻结期间 Clear() 被受控拦截
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var commitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Edit.OnCommit = async _ =>
                {
                    await commitGate.Task;
                    return new SettingsEditResult(SettingsEditStatus.Uncommitted);
                };
                await f.Vm.ExecuteTestDraftAsync();
                f.Vm.SelectedProfile!.DisplayName = "changed-uncommitted";

                var saveTask = f.Vm.ExecuteSaveAsync();
                f.Vm.IsSaving.ShouldBeTrue("执行保存时必须处于保存状态");
                f.Vm.Profiles.Clear(); // 尝试在保存中清空集合
                commitGate.SetResult();
                await saveTask;

                f.Vm.Profiles.Count.ShouldBe(1, "保存期间调用 Clear() 必须被受控拦截以保留原草稿");
                f.Vm.SelectedProfile.DisplayName.ShouldBe("changed-uncommitted");

                await f.Vm.ExecuteTestDraftAsync();
                f.Edit.TestCalls.ShouldBe(2, "保存未提交后测试队列必须保持开放");
            }

            // Case 1c: 校验失败（空名称）后仍可再次测试
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                await f.Vm.ExecuteTestDraftAsync();
                f.Vm.SelectedProfile!.DisplayName = ""; // 触发校验失败
                await f.Vm.ExecuteSaveAsync();
                f.Vm.StatusMessage.ShouldNotBeNull();
                f.Vm.StatusMessage!.ShouldContain("名称");

                f.Vm.SelectedProfile.DisplayName = "restored-name";
                await f.Vm.ExecuteTestDraftAsync();
                f.Edit.TestCalls.ShouldBe(2, "校验失败后测试队列必须保持开放");
            }

            // Case 1d: 队列上限（1 个运行 + 1 个等待项），替换等待项，重入峰值为 1 且 Drain 不提前返回
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var runTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                f.Edit.OnTestDraft = async (d, ct) =>
                {
                    runTcs.TrySetResult();
                    await releaseTcs.Task;
                    return new ProviderTestResult(true, TimeSpan.Zero, "ok");
                };

                var task1 = f.Vm.ExecuteTestDraftAsync();
                await runTcs.Task;

                // 连续排入两次
                var task2 = f.Vm.ExecuteTestDraftAsync();
                var task3 = f.Vm.ExecuteTestDraftAsync();

                releaseTcs.SetResult();
                await Task.WhenAll(task1, task2, task3);
            }

            // 队列内部重入断言
            {
                using var q = new SettingsDraftOperationQueue();
                var outerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var innerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? inner = null;
                int active = 0, peak = 0;

                var outer = q.EnqueueAsync(async ct =>
                {
                    active++;
                    peak = Math.Max(peak, active);
                    inner = q.EnqueueAsync(async token =>
                    {
                        active++;
                        peak = Math.Max(peak, active);
                        await innerGate.Task;
                        active--;
                    }, CancellationToken.None);
                    await outerGate.Task;
                    active--;
                });

                outerGate.SetResult();
                await outer;

                var drainTask = q.DrainAndCancelAllAsync();
                drainTask.IsCompleted.ShouldBeFalse("内层未退出时 Drain 绝不能提前返回");

                innerGate.SetResult();
                await drainTask;
                await inner!;
                peak.ShouldBe(1, "队列同步重入必须保持并发峰值为 1");
            }

            // 队列同步启动与排空屏障断言
            {
                using var q = new SettingsDraftOperationQueue();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? drain = null;
                bool early = false;
                var work = q.EnqueueAsync(async ct =>
                {
                    drain = q.DrainAndCancelAllAsync();
                    early = drain.IsCompleted;
                    await release.Task;
                });
                early.ShouldBeFalse("work 同步执行期间 Drain 绝不能提前返回");
                release.SetResult();
                await work;
                await drain!;
                q.HasActiveWork.ShouldBeFalse("排空完成后 HasActiveWork 必须为 false");
            }

            // 同步完成路径 HasActiveWork 准确性断言
            {
                using var q = new SettingsDraftOperationQueue();
                await q.EnqueueAsync(ct => Task.CompletedTask);
                await q.DrainAndCancelAllAsync();
                q.HasActiveWork.ShouldBeFalse("同步完成且排空后 HasActiveWork 必须为 false");
            }

            // Case 1e: 关停后拒绝新准入
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                f.Vm.StopAcceptance();
                f.Vm.CanEdit.ShouldBeFalse();
                await f.Vm.ExecuteTestDraftAsync();
                f.Edit.TestCalls.ShouldBe(0, "关停后不得发起底层测试请求");
            }
        });
    }

    // 2. 两个真实 Profile 的 PasswordBox 输入 A→B→A；切项、失败保存与重试保留待提交 Key；成功后解除明文；Reload/Cancel 清除明文
    [Fact]
    public async Task Scenario2_PasswordBox_InputAcrossProfileSwitch_PreservesPendingKeys_AndRetainsKeyOnFailedSaveOrRetry()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            await using var f = new TestFixture(CreateSampleProfile("p1"), CreateSampleProfile("p2", 1));
            var window = new SettingsWindow { DataContext = f.Vm };
            try
            {
                var pb = (PasswordBox)window.FindName("ApiKeyPasswordBox");
                var a = f.Vm.Profiles[0];
                var b = f.Vm.Profiles[1];

                // Profile A 输入 Key A
                f.Vm.SelectedProfile = a;
                a.CredentialEditKind = CredentialEditKind.Replace;
                pb.Password = "fake-key-a";

                // 切到 Profile B 输入 Key B
                f.Vm.SelectedProfile = b;
                b.CredentialEditKind = CredentialEditKind.Replace;
                pb.Password = "fake-key-b";

                // 切回 Profile A：A 的 Key A 仍保留在草稿中，PasswordBox 显示草稿中待提交 Key
                f.Vm.SelectedProfile = a;
                a.BuildCredentialEdit().Replacement?.ApiKey.ShouldBe("fake-key-a", "切换回 A 时必须保留 A 尚未提交的 Replacement Key");
                pb.Password.ShouldBe("fake-key-a");

                // 用户修改密码并尝试保存，保存失败（Uncommitted）
                pb.Password = "fake-correction-a";
                f.Edit.CommitResult = new SettingsEditResult(SettingsEditStatus.Uncommitted);
                await f.Vm.ExecuteSaveAsync();

                a.BuildCredentialEdit().Replacement?.ApiKey.ShouldBe("fake-correction-a", "保存失败后必须保留待修正 Key");

                // 模拟在途回执重试，重试不得清除未保存的草稿 Key
                f.Receipts.TryReserveSlot();
                f.Receipts.ConvertReservationToPending(new TestReceipt(), null);
                await f.Vm.ExecuteRetryPendingApplyAsync();

                a.BuildCredentialEdit().Replacement?.ApiKey.ShouldBe("fake-correction-a", "重试应用不得清除草稿待提交 Key");

                // 保存成功（Applied）：凭据转为 Keep，草稿明文清除
                f.Edit.OnCommit = plan =>
                {
                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            Providers = plan.TranslationChanges.Select(x => x.TargetProfile!).ToArray()
                        }
                    });
                    return Task.FromResult(new SettingsEditResult(SettingsEditStatus.Applied));
                };
                await f.Vm.ExecuteSaveAsync();

                f.Vm.StatusMessage.ShouldBe(UiText.SettingsStatusSaved);
                f.Vm.SelectedProfile!.CredentialEditKind.ShouldBe(CredentialEditKind.Keep);
                f.Vm.SelectedProfile.BuildCredentialEdit().Kind.ShouldBe(CredentialEditKind.Keep);
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBeNull("保存成功后必须清空明文 Key 引用");
                pb.Password.ShouldBe(string.Empty, "保存成功后 PasswordBox 必须清空");
                a.ReplacementApiKey.ShouldBeNull("保存成功后旧对象的明文 Key 必须被清空");

                // 再次测试输入明文后 Reload 与 Cancel，应清除明文引用
                f.Vm.SelectedProfile.CredentialEditKind = CredentialEditKind.Replace;
                pb.Password = "transient-key";
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBe("transient-key");

                f.Vm.ExecuteReload();
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBeNull("Reload 必须清除未提交密码引用");

                f.Vm.SelectedProfile.CredentialEditKind = CredentialEditKind.Replace;
                pb.Password = "transient-key-2";
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBe("transient-key-2");

                f.Vm.ExecuteCancel();
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBeNull("Cancel 必须清除未提交密码引用");
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
        });
    }

    // 3. 闸门挂起旧测试，保存已捕获后尝试修改 Profile/Key/集合；实际提交仍为捕获值；等待时停止则零 Commit；通知全在 UI 调度器
    [Fact]
    public async Task Scenario3_Save_CapturesAndFreezesPlan_IgnoresSubsequentModificationsDuringTestDrain()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // Case 3a: 捕获计划冻结，修改名称、凭据、集合在等待期间均被守护
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    f.Edit.OnTestDraft = async (d, ct) =>
                    {
                        entered.TrySetResult();
                        await release.Task;
                        return new ProviderTestResult(true, TimeSpan.Zero, "ok");
                    };

                    var testTask = f.Vm.ExecuteTestDraftAsync();
                    await entered.Task;

                    f.Vm.SelectedProfile!.DisplayName = "captured-display-name";

                    int uiThreadId = Environment.CurrentManagedThreadId;
                    bool offUiThreadNotification = false;
                    f.Vm.PropertyChanged += (s, e) =>
                    {
                        if (Environment.CurrentManagedThreadId != uiThreadId)
                        {
                            offUiThreadNotification = true;
                        }
                    };

                    // 发起保存：在进入 Drain 等待前已固化计划并冻结
                    var saveTask = f.Vm.ExecuteSaveAsync();

                    // 尝试在等待期间篡改属性与集合
                    f.Vm.SelectedProfile.DisplayName = "modified-during-save";
                    f.Vm.SelectedProfile.CredentialEditKind = CredentialEditKind.Replace;
                    f.Vm.Profiles.Clear(); // 尝试绕过冻结修改集合

                    f.Vm.Profiles.Count.ShouldBe(1, "保存冻结期间受控集合拒绝修改");

                    release.SetResult();
                    await testTask;
                    await saveTask;

                    f.Edit.LastPlan.ShouldNotBeNull();
                    f.Edit.LastPlan!.TranslationChanges.Single().TargetProfile?.DisplayName.ShouldBe("captured-display-name",
                        "实际提交的计划必须是点击保存时固化的捕获值，不得受等待期间修改的影响");
                    offUiThreadNotification.ShouldBeFalse("所有通知必须在 UI 线程发出");
                }
                finally
                {
                    release.TrySetResult();
                }
            }

            // Case 3b: 保存等待期间关停产生零 Commit
            await using (var fStop = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    fStop.Edit.OnTestDraft = async (d, ct) =>
                    {
                        entered.TrySetResult();
                        await release.Task;
                        return new ProviderTestResult(true, TimeSpan.Zero, "ok");
                    };

                    var testTask = fStop.Vm.ExecuteTestDraftAsync();
                    await entered.Task;

                    fStop.Vm.SelectedProfile!.DisplayName = "captured-to-abort";
                    var saveTask = fStop.Vm.ExecuteSaveAsync();

                    // 保存等待测试退出期间请求关停
                    fStop.SaveCoordinator.StopAcceptanceAndCancelPreCommit();
                    fStop.Vm.StopAcceptance();

                    release.SetResult();
                    await testTask;
                    await saveTask;

                    fStop.Edit.LastPlan.ShouldBeNull("保存等待测试排干期间触发关停，绝不得向 ISettingsEditCoordinator 提交任何计划");
                }
                finally
                {
                    release.TrySetResult();
                }
            }
        });
    }

    // 4. 旧结果回调已排队后切项往返、外部更换 endpoint、编辑/删除/Reload；同身份阳性对照可显示，过期结果不回写且旧工作收到取消
    [Fact]
    public async Task Scenario4_StaleResults_RejectedOnSwitch_EndpointChange_Edit_Delete_OrReload()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // Case 4a: 切项往返 A -> B -> A 拒绝旧 A 的测试结果
            await using (var f = new TestFixture(CreateSampleProfile("p1"), CreateSampleProfile("p2", 1)))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool ctsCancelled = false;

                try
                {
                    f.Edit.OnTestDraft = async (d, ct) =>
                    {
                        using var reg = ct.Register(() => ctsCancelled = true);
                        entered.TrySetResult();
                        await release.Task;
                        return new ProviderTestResult(true, TimeSpan.Zero, "old-p1-result");
                    };

                    var p1 = f.Vm.Profiles[0];
                    var p2 = f.Vm.Profiles[1];

                    f.Vm.SelectedProfile = p1;
                    var testTask = f.Vm.ExecuteTestDraftAsync();
                    await entered.Task;

                    // 切到 p2
                    f.Vm.SelectedProfile = p2;
                    ctsCancelled.ShouldBeTrue("切出当前 Profile 必须请求取消在途测试");

                    // 切回 p1
                    f.Vm.SelectedProfile = p1;

                    // 放行旧测试
                    release.SetResult();
                    await testTask;

                    p1.TestStatusMessage.ShouldBeNull("往返切换后旧测试结果必须被拒绝，不得回写当前项");
                }
                finally
                {
                    release.TrySetResult();
                }
            }

            // Case 4b: 外部更换 endpoint 递增 revision，旧模型列表结果被拒绝
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    f.Edit.OnListDraftModels = async (d, ct) =>
                    {
                        entered.TrySetResult();
                        await release.Task;
                        return ["old-endpoint-model"];
                    };

                    var listTask = f.Vm.ExecuteListModelsAsync();
                    await entered.Task;

                    // 外部配置更新，修改 BaseUrl
                    var oldRev = f.Vm.SelectedProfile!.Revision;
                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            Providers = [CreateSampleProfile("p1") with { BaseUrl = "https://new-endpoint.invalid/v1" }]
                        }
                    });
                    f.Vm.SelectedProfile.Revision.ShouldNotBe(oldRev);

                    release.SetResult();
                    await listTask;

                    f.Vm.SelectedProfile.AvailableModels.ShouldNotContain("old-endpoint-model", "端点变更后陈旧模型结果必须被拒绝");
                }
                finally
                {
                    release.TrySetResult();
                }
            }

            // Case 4c: 测试进行中编辑草稿，触发在途取消
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;

                try
                {
                    f.Edit.OnTestDraft = async (d, ct) =>
                    {
                        using var reg = ct.Register(() => canceled = true);
                        entered.TrySetResult();
                        await release.Task;
                        return new ProviderTestResult(true, TimeSpan.Zero, "ok");
                    };

                    var testTask = f.Vm.ExecuteTestDraftAsync();
                    await entered.Task;

                    f.Vm.SelectedProfile!.DisplayName = "edited-name-during-test";
                    canceled.ShouldBeTrue("编辑草稿属性时必须触发在途操作取消");

                    release.SetResult();
                    await testTask;
                }
                finally
                {
                    release.TrySetResult();
                }
            }

            // Case 4d: 测试进行中删除草稿，取消旧任务并丢弃结果
            await using (var f = new TestFixture(CreateSampleProfile("p1"), CreateSampleProfile("p2", 1)))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;

                try
                {
                    f.Edit.OnTestDraft = async (d, ct) =>
                    {
                        using var reg = ct.Register(() => canceled = true);
                        entered.TrySetResult();
                        await release.Task;
                        return new ProviderTestResult(true, TimeSpan.Zero, "ok-deleted");
                    };

                    var target = f.Vm.Profiles[0];
                    f.Vm.SelectedProfile = target;
                    var testTask = f.Vm.ExecuteTestDraftAsync();
                    await entered.Task;

                    f.Vm.ExecuteDeleteProfile();
                    canceled.ShouldBeTrue("删除草稿必须取消在途测试任务");

                    release.SetResult();
                    await testTask;
                    target.TestStatusMessage.ShouldBeNull("已删除草稿不得接受旧测试结果");
                }
                finally
                {
                    release.TrySetResult();
                }
            }

            // Case 4e: 列模型进行中执行 Reload，取消旧任务并丢弃结果
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;

                try
                {
                    f.Edit.OnListDraftModels = async (d, ct) =>
                    {
                        using var reg = ct.Register(() => canceled = true);
                        entered.TrySetResult();
                        await release.Task;
                        return ["stale-after-reload"];
                    };

                    var listTask = f.Vm.ExecuteListModelsAsync();
                    await entered.Task;

                    f.Vm.ExecuteReload();
                    canceled.ShouldBeTrue("Reload 必须取消在途模型请求");

                    release.SetResult();
                    await listTask;
                    f.Vm.SelectedProfile!.AvailableModels.ShouldNotContain("stale-after-reload", "Reload 后陈旧模型列表必须被丢弃");
                }
                finally
                {
                    release.TrySetResult();
                }
            }
        });
    }

    // 5. 外部新增/删除/排序与本地字段/顺序冲突；无关更新保留不完整数字草稿；仅改显示名保留隐藏字段和 Options；真实 ComboBox 绑定 Prompt 刷新保护
    [Fact]
    public async Task Scenario5_ExternalSettingsChanged_SynchronizesMembersAndOrder_PreservesUnfinishedDraftsAndHiddenFields()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // Case 5a: 外部更新成员，VM 列表完整同步，IsOrderDirty 保持 false
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                f.Settings.Publish(f.Settings.Current with
                {
                    Translation = f.Settings.Current.Translation with
                    {
                        Providers = [CreateSampleProfile("p2")]
                    }
                });

                f.Vm.Profiles.Count.ShouldBe(1);
                f.Vm.Profiles[0].Id.ShouldBe("p2");
                f.Vm.IsOrderDirty.ShouldBeFalse("外部更新未触及本地排序时，IsOrderDirty 必须保持 false");
            }

            // Case 5b: 未完成数值文本，保持 dirty 且无关外部变更不冲掉
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                f.Vm.SelectedProfile!.TimeoutText = "incomplete-number";
                f.Vm.SelectedProfile.IsDirty.ShouldBeTrue("无法解析的数值文本必须标记为 dirty");

                // 外部变更 General 设置（无关）
                f.Settings.Publish(f.Settings.Current with
                {
                    General = f.Settings.Current.General with { ShowStartupNotification = false }
                });

                f.Vm.SelectedProfile.TimeoutText.ShouldBe("incomplete-number", "无关外部设置变更不得覆盖未完成数值文本草稿");
            }

            // Case 5c: 隐藏字段保留（Anthropic 的 Temperature 与 Options）
            {
                var profile = CreateSampleProfile("anthropic-1") with
                {
                    Kind = TranslationProviderKind.Anthropic,
                    Temperature = 0.8,
                    OpenAIOptions = new OpenAICompatibleOptions { Stream = false },
                    AnthropicOptions = new AnthropicOptions { Effort = "low" }
                };

                var draft = TranslationProfileDraftViewModel.FromExisting(profile);
                draft.DisplayName = "renamed-anthropic";

                var target = draft.BuildTargetProfile(0);
                target.Temperature.ShouldBe(0.8, "修改 DisplayName 必须保留隐藏的 Temperature");
                target.OpenAIOptions.ShouldNotBeNull();
                target.OpenAIOptions!.Stream.ShouldBeFalse();
                target.AnthropicOptions.ShouldNotBeNull();
                target.AnthropicOptions!.Effort.ShouldBe("low");
            }

            // Case 5d: ComboBox 刷新不擅改手输型号
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                f.Vm.SelectedProfile!.Model = "user-custom-model";
                f.Edit.OnListDraftModels = (d, ct) => Task.FromResult<IReadOnlyList<string>>(["auto-discovered-model-1", "auto-discovered-model-2"]);

                await f.Vm.ExecuteListModelsAsync();

                f.Vm.SelectedProfile.Model.ShouldBe("user-custom-model", "模型列表刷新不得覆盖用户已选择/手输的型号");
                f.Vm.SelectedProfile.AvailableModels.ShouldContain("auto-discovered-model-1");
            }

            // Case 5e: 真实 XAML ComboBox 双向绑定下的 Prompt 列表刷新保护
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var window = new SettingsWindow { DataContext = f.Vm, Left = -20000, Top = -20000, ShowActivated = false };
                try
                {
                    f.Vm.CurrentPageKey = "translation";
                    window.Show();
                    window.UpdateLayout();

                    // 用户显式选择内置模板
                    f.Vm.SelectedProfile!.PromptTemplateId = BuiltInPrompts.DefaultTemplateId;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    // 外部发布无关设置（常规通知项变更）
                    f.Settings.Publish(f.Settings.Current with
                    {
                        General = f.Settings.Current.General with
                        {
                            ShowStartupNotification = !f.Settings.Current.General.ShowStartupNotification
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.SelectedProfile.PromptTemplateId.ShouldBe(BuiltInPrompts.DefaultTemplateId,
                        "无关外部更新不得经由 ComboBox 双向绑定将显式 Prompt 冲刷为 null");

                    // 真实添加自定义模板，用户显式选中
                    var custom = new PromptTemplate
                    {
                        Id = "custom-test-5e",
                        Name = "Custom Test 5e",
                        System = "Translate system",
                        User = "{{text}}"
                    };
                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            CustomPrompts = [custom]
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.SelectedProfile.PromptTemplateId = custom.Id;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    f.Vm.SelectedProfile.PromptTemplateId.ShouldBe(custom.Id);

                    // 外部删除该自定义模板，验证 ComboBox 选中的模板 ID 仍保留且原地更新为 [已移除]
                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            CustomPrompts = []
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.SelectedProfile.PromptTemplateId.ShouldBe(custom.Id, "引用的模板已被删除时必须保留原选择并提示，不得静默置空");
                    var opt = f.Vm.AvailablePrompts.FirstOrDefault(p => p.Id == custom.Id);
                    opt.ShouldNotBeNull();
                    opt.DisplayName.ShouldContain("[已移除]");

                    // 外部重新添加并重命名该自定义模板，验证原地更新 DisplayName 且保持选择身份
                    var renamed = custom with { Name = "Renamed Custom 5e" };
                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            CustomPrompts = [renamed]
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.SelectedProfile.PromptTemplateId.ShouldBe(custom.Id);
                    opt.DisplayName.ShouldBe("Renamed Custom 5e", "模板重命名时必须原地更新 DisplayName 且保持选择身份");
                }
                finally
                {
                    window.DataContext = null;
                    window.Close();
                }
            }

            // Case 5f: 挂起服务重试、自启重试、保存的异步工作，期间外部新增/替换服务，完成/失败后选择新项实际编辑并再次保存；关闭交错不允许新变更
            // 1. 服务重试挂起期间外部新增服务，完成后新项可编辑并保存
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? retryTask = null;
                try
                {
                    f.Edit.OnRetryApply = async r =>
                    {
                        entered.SetResult();
                        await release.Task;
                        return new SettingsApplyResult(true);
                    };
                    f.Receipts.TryReserveSlot();
                    f.Receipts.ConvertReservationToPending(new TestReceipt(), null);
                    retryTask = f.Vm.ExecuteRetryPendingApplyAsync();
                    await entered.Task;

                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            Providers = [CreateSampleProfile("p1"), CreateSampleProfile("p2", 1)]
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    var pendingP2 = f.Vm.Profiles.Single(p => p.Id == "p2");
                    pendingP2.IsFrozen.ShouldBeTrue("重试进行中外部新增项必须处于冻结状态");

                    release.SetResult();
                    await retryTask;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.CanEdit.ShouldBeTrue();
                    pendingP2.IsFrozen.ShouldBeFalse("重试完成后外部新增项必须解冻");
                    f.Vm.IsOrderDirty.ShouldBeFalse();

                    // 实际编辑新项并再次保存
                    f.Vm.SelectedProfile = pendingP2;
                    pendingP2.DisplayName = "p2-edited-after-retry";
                    pendingP2.DisplayName.ShouldBe("p2-edited-after-retry");
                    await f.Vm.ExecuteSaveAsync();
                    f.Edit.LastPlan.ShouldNotBeNull();
                    f.Edit.LastPlan!.TranslationChanges.Any(c => c.TargetProfile?.DisplayName == "p2-edited-after-retry").ShouldBeTrue();
                }
                finally
                {
                    release.TrySetResult();
                    if (retryTask != null)
                    {
                        try { await retryTask; } catch { }
                    }
                }
            }

            // 2. 自启重试挂起期间外部新增服务，完成后新项解冻且可编辑
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? autoRetryTask = null;
                try
                {
                    f.Auto.OnSetEnabled = async val =>
                    {
                        entered.SetResult();
                        await release.Task;
                    };
                    autoRetryTask = f.Vm.ExecuteRetryAutoStartAsync();
                    await entered.Task;

                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            Providers = [CreateSampleProfile("p1"), CreateSampleProfile("p3", 1)]
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    var pendingP3 = f.Vm.Profiles.Single(p => p.Id == "p3");
                    pendingP3.IsFrozen.ShouldBeTrue("自启重试挂起期间外部新增项必须冻结");

                    release.SetResult();
                    await autoRetryTask;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.CanEdit.ShouldBeTrue();
                    pendingP3.IsFrozen.ShouldBeFalse("自启重试完成后新增项必须解冻");
                    pendingP3.DisplayName = "p3-edited-after-autostart";
                    pendingP3.DisplayName.ShouldBe("p3-edited-after-autostart");
                }
                finally
                {
                    release.TrySetResult();
                    if (autoRetryTask != null)
                    {
                        try { await autoRetryTask; } catch { }
                    }
                }
            }

            // 3. 保存挂起期间外部替换服务，完成后解冻且可编辑；会话停止关闭交错后保持冻结拒绝变更
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? saveTask = null;
                try
                {
                    f.Edit.OnCommit = async plan =>
                    {
                        entered.SetResult();
                        await release.Task;
                        return new SettingsEditResult(SettingsEditStatus.Applied);
                    };
                    f.Vm.SelectedProfile!.DisplayName = "p1-dirty";
                    saveTask = f.Vm.ExecuteSaveAsync();
                    await entered.Task;

                    f.Settings.Publish(f.Settings.Current with
                    {
                        Translation = f.Settings.Current.Translation with
                        {
                            Providers = [CreateSampleProfile("p1") with { Model = "external-replaced-model" }, CreateSampleProfile("p4", 1)]
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    var p4 = f.Vm.Profiles.Single(p => p.Id == "p4");
                    p4.IsFrozen.ShouldBeTrue("保存进行中外部新增项必须冻结");

                    release.SetResult();
                    await saveTask;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.CanEdit.ShouldBeTrue();
                    var currentP4 = f.Vm.Profiles.Single(p => p.Id == "p4");
                    currentP4.IsFrozen.ShouldBeFalse("保存完成后新增项解冻");
                    currentP4.DisplayName = "p4-edited";
                    currentP4.DisplayName.ShouldBe("p4-edited");

                    // 关闭会话（StopAcceptance）：草稿必须保持冻结，拒绝后续修改
                    f.Vm.StopAcceptance();
                    f.Vm.CanEdit.ShouldBeFalse();
                    currentP4.IsFrozen.ShouldBeTrue("会话停止后必须保持全部草稿冻结");
                    currentP4.DisplayName = "p4-attempt-modify-after-stop";
                    currentP4.DisplayName.ShouldBe("p4-edited", "已停止会话严禁接受属性修改");
                }
                finally
                {
                    release.TrySetResult();
                    if (saveTask != null)
                    {
                        try { await saveTask; } catch { }
                    }
                }
            }
        });
    }

    // 6. 注入带假密钥的未知 Test 异常，安全提示/日志无标记；Pending 保存过程中 IsEnabled 始终后台运行
    [Fact]
    public async Task Scenario6_TestDraftException_MasksMessageAndSecrets_AndAutostartQueryRunsInBackground()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // Case 6a: 异常消息包含假密钥，UI 文案脱敏，不泄露任何标记
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                f.Edit.OnTestDraft = (d, ct) => throw new InvalidOperationException("CRITICAL_FAKE_KEY_LEAK_MARKER_9999");

                await f.Vm.ExecuteTestDraftAsync();

                f.Vm.SelectedProfile!.TestStatusMessage.ShouldNotBeNull();
                f.Vm.SelectedProfile.TestStatusMessage.ShouldBe(UiText.SettingsTestDraftFailed);
                f.Vm.SelectedProfile.TestStatusMessage!.ShouldNotContain("CRITICAL_FAKE_KEY_LEAK_MARKER_9999");
            }

            // Case 6b: Pending 保存过程中开机自启状态查询始终在后台线程运行
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                f.Edit.CommitResult = new SettingsEditResult(SettingsEditStatus.ApplyPending, PendingApply: new TestReceipt());
                f.Vm.SelectedProfile!.DisplayName = "new-display-name";

                await f.Vm.ExecuteSaveAsync();

                f.Auto.QueriedOnUi.ShouldBeFalse("开机自启状态查询严禁在 UI 线程同步执行");
            }
        });
    }

    // 7. 真实 SettingsWindow + WpfUiDispatcher + Presenter 关闭重开与实际单例回执：删除最后 Profile 真实提交、32 名额在途竞争与取消释放、双重试真实失败独立性、回执与停止 UI 交错
    [Fact]
    public async Task Scenario7_RealWindowPresenter_HandlesClosingReopening_AndReceipt32QuotaExhaustion()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // Case 7a: 删除最后一个已保存服务 → 真实提交写入磁盘/Current → 观察者刷新受控失败返回 ApplyPending → 应用单例保留回执 → 关闭重开后空列表仍有重试入口 → 重试成功且不再触发 UpdateAsync
            var tempDir = Path.Combine(Path.GetTempPath(), "iris-test-reg7-del-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new TestAppPaths(tempDir);
                var secretStore = new MemorySecretStore();
                var autoStartService = new TestAutoStartService();
                var receiptManager = new SettingsApplyReceiptManager();
                var dispatcher = Dispatcher.CurrentDispatcher;
                var uiDispatcher = new WpfUiDispatcher(() => dispatcher);
                var controlledObserver = new ControlledEditObserver();
                var providerFactory = new FakeTranslationProviderFactory();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IAppPaths>(paths);
                services.AddSingleton<ISecretStore>(secretStore);
                services.AddSingleton<IAutoStartService>(autoStartService);
                services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
                services.AddSingleton<IUiDispatcher>(uiDispatcher);
                services.AddSingleton<ThemeService>();
                services.AddSingleton<ITranslationProviderFactory>(providerFactory);
                services.AddSingleton<IProviderConfigurationObserver>(controlledObserver);

                services.AddIrisInfrastructure();
                services.AddIrisCore();
        // This legacy UI fixture supplies no OCR factories; keep OCR as an external boundary.
        services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrProviderRegistry>());

                // 统一单容器生命周期管理：装饰 ISettingsService 以便统计 UpdateCount
                var jsonDescriptor = services.First(d => d.ServiceType == typeof(ISettingsService));
                services.Remove(jsonDescriptor);
                services.AddSingleton(jsonDescriptor.ImplementationType!, jsonDescriptor.ImplementationType!);
                services.AddSingleton<CountingSettingsService>(sp => new CountingSettingsService((ISettingsService)sp.GetRequiredService(jsonDescriptor.ImplementationType!)));
                services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<CountingSettingsService>());

                services.AddScoped<GeneralSettingsSaveCoordinator>();
                services.AddScoped<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();

                using var rootProvider = services.BuildServiceProvider();
                var countingSettingsService = rootProvider.GetRequiredService<CountingSettingsService>();
                await countingSettingsService.LoadAsync();

                // 初始保存一个 Provider p1
                var initialProfile = CreateSampleProfile("p1");
                await countingSettingsService.UpdateAsync(s => s with
                {
                    Translation = s.Translation with { Providers = [initialProfile] }
                });

                // 验证初始真实 Registry 状态（由 AddIrisCore 注册的真实 TranslationProviderRegistry）
                var realRegistry = rootProvider.GetRequiredService<ITranslationProviderRegistry>();
                await realRegistry.RefreshAsync();
                using (var initialSnapshot = await realRegistry.AcquireSnapshotAsync())
                {
                    initialSnapshot.Providers.Count.ShouldBe(1, "初始真实 Registry 必须包含 p1 实例");
                    initialSnapshot.Providers[0].Profile.Id.ShouldBe("p1");
                }

                // 验证保存前磁盘文件真实反序列化解析包含 p1
                var diskSettingsBefore = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(paths.SettingsFile), IrisJson.Options);
                diskSettingsBefore.ShouldNotBeNull();
                diskSettingsBefore.Translation.Providers.Count.ShouldBe(1, "保存前磁盘必须包含 1 个 Provider");
                diskSettingsBefore.Translation.Providers[0].Id.ShouldBe("p1", "保存前磁盘 Provider ID 为 p1");

                var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

                try
                {
                    // 1. 打开窗口 1，显示一个 Profile
                    await presenter.ShowAsync("translation");
                    presenter.ActiveSession.ShouldNotBeNull();
                    var session1 = presenter.ActiveSession!;
                    var vm1 = session1.ViewModel;
                    vm1.Profiles.Count.ShouldBe(1);

                    // 删除最后一个服务
                    vm1.SelectedProfile = vm1.Profiles[0];
                    vm1.ExecuteDeleteProfile();
                    vm1.Profiles.Count.ShouldBe(0);

                    // 注入观察者刷新受控失败
                    controlledObserver.FailRefresh = true;

                    // 实际提交保存！
                    await vm1.ExecuteSaveAsync();

                    // 断言：Current 与磁盘 settings.json 已真正删除该服务，但由于刷新未完成返回 ApplyPending，单例保留回执
                    countingSettingsService.Current.Translation.Providers.Count.ShouldBe(0, "实际提交后 Current 中的服务已被删除");

                    // 严格按 JSON 格式反序列化检查 Providers 集合为空，杜绝字符串包含大小写掩盖错误
                    var diskSettingsAfter = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(paths.SettingsFile), IrisJson.Options);
                    diskSettingsAfter.ShouldNotBeNull();
                    diskSettingsAfter.Translation.Providers.ShouldBeEmpty("实际提交后磁盘 Providers 集合已持久化清空");

                    vm1.StatusMessage.ShouldBe("配置已提交保存，但部分服务提供商刷新未完全完成，旧凭据已保留");
                    vm1.CanRetryPendingApply.ShouldBeTrue("观察者失败导致返回待办状态");
                    receiptManager.PendingCount.ShouldBe(1, "单例回执管理器成功记录该回执");

                    // 关闭窗口 1 会话
                    session1.Window.ForceClose = true;
                    session1.Window.Close();
                    await session1.ShutdownAsync();

                    // 重新打开窗口 2：空列表仍有重试入口
                    await presenter.ShowAsync("translation");
                    presenter.ActiveSession.ShouldNotBeNull();
                    var session2 = presenter.ActiveSession!;
                    var vm2 = session2.ViewModel;
                    vm2.Profiles.Count.ShouldBe(0, "跨窗口会话重新打开后空列表依然保持");
                    vm2.CanRetryPendingApply.ShouldBeTrue("跨窗口会话重新打开后空列表依然具备待办重试入口");

                    // 恢复观察者刷新成功，执行重试
                    controlledObserver.FailRefresh = false;
                    int updateCountBeforeRetry = countingSettingsService.UpdateCount;

                    await vm2.ExecuteRetryPendingApplyAsync();

                    // 断言：重试成功，待办回执清空，且重试仅做刷新/清理，绝不再触发 UpdateAsync
                    receiptManager.PendingCount.ShouldBe(0, "重试完成后待办回执清空");
                    vm2.CanRetryPendingApply.ShouldBeFalse();
                    countingSettingsService.UpdateCount.ShouldBe(updateCountBeforeRetry, "重试阶段严禁再次调用 UpdateAsync");

                    using (var finalSnapshot = await realRegistry.AcquireSnapshotAsync())
                    {
                        finalSnapshot.Providers.Count.ShouldBe(0, "重试完成后真实 Registry 中已无 Provider 实例");
                    }

                    session2.Window.ForceClose = true;
                    session2.Window.Close();
                    await session2.ShutdownAsync();
                }
                finally
                {
                    await presenter.ShutdownAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }

            // Case 7b: 真正的容量在途竞争与取消释放；满额 General-only 对照保持
            {
                var receiptManager = new SettingsApplyReceiptManager();
                var initialSettings = new AppSettings
                {
                    Translation = new TranslationSettings { Providers = [CreateSampleProfile("p1")] }
                };
                var settingsService = new MutableSettingsService(initialSettings);
                var autoStartService = new TestAutoStartService();
                var editCoordinator = new TestEditCoordinator();

                var saveCoordinator = new GeneralSettingsSaveCoordinator(
                    settingsService,
                    editCoordinator,
                    autoStartService,
                    receiptManager,
                    NullLogger<GeneralSettingsSaveCoordinator>.Instance);

                // 填入 31 个待办回执，预留 1 个名额
                var testReceipts = new List<TestReceipt>();
                for (int i = 0; i < 31; i++)
                {
                    var r = new TestReceipt();
                    testReceipts.Add(r);
                    receiptManager.TryReserveSlot().ShouldBeTrue();
                    receiptManager.ConvertReservationToPending(r, null);
                }
                receiptManager.PendingCount.ShouldBe(31);

                // 准备挂起保存 A 的 Core CommitAsync
                var gateAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var gateARelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int coreCommitCalls = 0;
                editCoordinator.OnCommitWithToken = async (plan, ct) =>
                {
                    Interlocked.Increment(ref coreCommitCalls);
                    gateAEntered.SetResult();
                    await gateARelease.Task;
                    ct.ThrowIfCancellationRequested();
                    return new SettingsEditResult(SettingsEditStatus.Applied);
                };

                var emptyGeneralPatch = new GeneralSettingsPatch(null, null, null, null);
                var planA = new SettingsEditPlan(emptyGeneralPatch, [new TranslationProfileChange(TranslationProfileChangeKind.Add, null, CreateSampleProfile("pA"), new ProviderCredentialEdit(CredentialEditKind.Keep))], null);
                var planB = new SettingsEditPlan(emptyGeneralPatch, [new TranslationProfileChange(TranslationProfileChangeKind.Add, null, CreateSampleProfile("pB"), new ProviderCredentialEdit(CredentialEditKind.Keep))], null);

                using var ctsA = new CancellationTokenSource();
                var taskA = saveCoordinator.CommitPlanAsync(planA, false, false, ctsA.Token);
                await gateAEntered.Task;

                TestReceipt? r32 = null;
                try
                {
                    // 此时保存 A 持有在途预留：31 pending + 1 in-flight = 32（满额）
                    receiptManager.InFlightReservations.ShouldBe(1, "保存 A 必须持有在途预留名额");
                    receiptManager.PendingCount.ShouldBe(31);

                    // 保存 B 竞争第 33 个名额：因满额被直接拒绝，且绝对没有调用 Core
                    var resultB = await saveCoordinator.CommitPlanAsync(planB, false, false, CancellationToken.None);
                    resultB.Status.ShouldBe(GeneralSaveStatus.Uncommitted);
                    resultB.FailureReason.ShouldBe(UiText.SettingsStatusPendingApplyQuotaFull);
                    coreCommitCalls.ShouldBe(1, "容量满额时竞争者必须被拒绝且绝对不得调用 Core CommitAsync");

                    // 取消保存 A：触发取消令牌，断言在清理闸门放行前保存 A 尚未退出且预留名额仍被占用
                    ctsA.Cancel();
                    receiptManager.InFlightReservations.ShouldBe(1, "在实际退出前保存 A 的在途预留依然占用");
                    taskA.IsCompleted.ShouldBeFalse("保存 A 尚未完成退出");

                    // 放行清理闸门并等待 A 实际退出
                    gateARelease.SetResult();
                    var resultA = await taskA;

                    // 断言 A 最终为真正的 Uncommitted / 取消结果
                    resultA.Status.ShouldBe(GeneralSaveStatus.Uncommitted, "取消后保存 A 必须进入 Uncommitted 取消分支");
                    resultA.FailureReason.ShouldBe(UiText.SettingsStatusCanceled, "保存 A 必须明确返回已取消原因");

                    // 确认在实际退出后在途预留释放且容量可再次预留
                    receiptManager.InFlightReservations.ShouldBe(0, "取消退出后在途预留名额必须归零");
                    receiptManager.PendingCount.ShouldBe(31, "取消退出后 PendingCount 绝不得增长");
                    (receiptManager.PendingCount + receiptManager.InFlightReservations).ShouldBe(31);
                    receiptManager.TryReserveSlot().ShouldBeTrue("容量释放后名额可再次预留");
                    receiptManager.ReleaseReservation();

                    // 对照：验证独立成功 Applied 分支释放名额
                    var planNormal = new SettingsEditPlan(emptyGeneralPatch, [new TranslationProfileChange(TranslationProfileChangeKind.Add, null, CreateSampleProfile("pNormal"), new ProviderCredentialEdit(CredentialEditKind.Keep))], null);
                    editCoordinator.OnCommitWithToken = (plan, ct) => Task.FromResult(new SettingsEditResult(SettingsEditStatus.Applied));
                    var resultNormal = await saveCoordinator.CommitPlanAsync(planNormal, false, false, CancellationToken.None);
                    resultNormal.Status.ShouldBe(GeneralSaveStatus.SavedAndApplied);
                    receiptManager.InFlightReservations.ShouldBe(0, "成功 Applied 后在途预留同样归零释放");
                    receiptManager.PendingCount.ShouldBe(31);

                    // 满额 General-only 对照：占满第 32 个槽位后，仅修改 General 不受阻断正常提交
                    r32 = new TestReceipt();
                    receiptManager.TryReserveSlot().ShouldBeTrue();
                    receiptManager.ConvertReservationToPending(r32, null);
                    receiptManager.PendingCount.ShouldBe(32);

                    var generalPlan = new SettingsEditPlan(new GeneralSettingsPatch(new SettingsFieldEdit<AppTheme>(AppTheme.Light, AppTheme.Dark), null, null, null), [], null);
                    var generalResult = await saveCoordinator.CommitPlanAsync(generalPlan, false, false, CancellationToken.None);
                    generalResult.Status.ShouldBe(GeneralSaveStatus.SavedAndApplied, "满额时 General-only 保存不预留 Provider 槽位正常提交");
                }
                finally
                {
                    ctsA.Cancel();
                    gateARelease.TrySetResult();
                    try
                    {
                        await taskA;
                    }
                    catch
                    {
                        // 忽略测试失败时 taskA 抛出的取消或其它异常，保证其完全退出
                    }
                    await saveCoordinator.WaitForActiveWorkAsync();
                    saveCoordinator.Dispose();
                    foreach (var r in testReceipts) receiptManager.RemoveReceipt(r);
                    if (r32 != null) receiptManager.RemoveReceipt(r32);
                }
            }

            // Case 7c: 两种系统应用真实失败的独立状态与未保存状态保留
            await using (var f = new TestFixture(CreateSampleProfile("p1"), CreateSampleProfile("p2", 1)))
            {
                // 真实故障注入：SetEnabled 显式抛错，Core 提交返回 ApplyPending
                f.Auto.ThrowOnSet = true;
                f.Edit.CommitResult = new SettingsEditResult(SettingsEditStatus.ApplyPending, PendingApply: new TestReceipt());

                f.Vm.LaunchAtStartup = !f.Vm.LaunchAtStartup;
                f.Vm.SelectedProfile!.DisplayName = "p1-dirty-dual";
                await f.Vm.ExecuteSaveAsync();

                f.Vm.CanRetryPendingApply.ShouldBeTrue("Provider 处于 ApplyPending");
                f.Vm.CanRetryAutoStart.ShouldBeTrue("AutoStart 真实显式抛错处于 pending");

                // 用户在界面输入未保存的普通字段（Theme）、未提交凭据（Key）、未保存顺序
                f.Vm.Theme = AppTheme.Dark;
                f.Vm.Theme.ShouldBe(AppTheme.Dark);

                f.Vm.SelectedProfile.CredentialEditKind = CredentialEditKind.Replace;
                f.Vm.SelectedProfile.SetReplacementApiKey("uncommitted-test-key-7c");
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBe("uncommitted-test-key-7c");

                f.Vm.ExecuteMoveDown();
                f.Vm.IsOrderDirty.ShouldBeTrue();
                var orderBefore = f.Vm.Profiles.Select(p => p.Id).ToList();

                // 1. 单独重试 Provider 且受控失败
                f.Edit.OnRetryApply = _ => Task.FromResult(new SettingsApplyResult(false, new Iris.Core.Errors.ErrorInfo(Iris.Core.Errors.ErrorCode.StorageFailure, "provider-retry-failed")));
                await f.Vm.ExecuteRetryPendingApplyAsync();

                f.Vm.CanRetryPendingApply.ShouldBeTrue("Provider 重试失败后保持待办");
                f.Vm.CanRetryAutoStart.ShouldBeTrue("Provider 重试失败不得清除 AutoStart 待办状态");
                f.Vm.Theme.ShouldBe(AppTheme.Dark, "Provider 重试失败绝不得清除未保存普通字段");
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBe("uncommitted-test-key-7c", "Provider 重试失败绝不得清除未保存 Key");
                f.Vm.IsOrderDirty.ShouldBeTrue("Provider 重试失败绝不得清除未保存顺序脏标记");
                f.Vm.Profiles.Select(p => p.Id).SequenceEqual(orderBefore).ShouldBeTrue();

                // 2. 单独重试 AutoStart 且 ThrowOnSet 真实失败
                await f.Vm.ExecuteRetryAutoStartAsync();

                f.Vm.CanRetryAutoStart.ShouldBeTrue("AutoStart 真实重试失败后保持待办");
                f.Vm.CanRetryPendingApply.ShouldBeTrue("AutoStart 重试失败不得清除 Provider 待办状态");
                f.Vm.Theme.ShouldBe(AppTheme.Dark, "AutoStart 重试失败绝不得清除未保存普通字段");
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBe("uncommitted-test-key-7c", "AutoStart 重试失败绝不得清除未保存 Key");
                f.Vm.IsOrderDirty.ShouldBeTrue("AutoStart 重试失败绝不得清除未保存顺序脏标记");
                f.Vm.Profiles.Select(p => p.Id).SequenceEqual(orderBefore).ShouldBeTrue();

                // 3. 恢复 AutoStart 并重试成功
                f.Auto.ThrowOnSet = false;
                await f.Vm.ExecuteRetryAutoStartAsync();
                f.Vm.CanRetryAutoStart.ShouldBeFalse("AutoStart 重试成功后清除待办");
                f.Vm.CanRetryPendingApply.ShouldBeTrue("AutoStart 成功不干扰 Provider 待办");
                f.Vm.Theme.ShouldBe(AppTheme.Dark);
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBe("uncommitted-test-key-7c");
                f.Vm.IsOrderDirty.ShouldBeTrue();

                // 4. 恢复 Provider 并重试成功
                f.Edit.OnRetryApply = _ => Task.FromResult(new SettingsApplyResult(true));
                await f.Vm.ExecuteRetryPendingApplyAsync();
                f.Vm.CanRetryPendingApply.ShouldBeFalse("Provider 重试成功后清除待办");
                f.Vm.Theme.ShouldBe(AppTheme.Dark);
                f.Vm.SelectedProfile.ReplacementApiKey.ShouldBe("uncommitted-test-key-7c");
                f.Vm.IsOrderDirty.ShouldBeTrue();
            }

            // Case 7d: 回执发布与停止 UI 交错：真实 SettingsWindowPresenter 两个独立窗口 scope、退出 witness、新窗口实际重试
            var tempDir7d = Path.Combine(Path.GetTempPath(), "iris-test-reg7d-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir7d);
            try
            {
                lock (TestScopedWitness.Created) { TestScopedWitness.Created.Clear(); }

                var paths = new TestAppPaths(tempDir7d);
                var secretStore = new MemorySecretStore();
                var autoStartService = new TestAutoStartService();
                var receiptManager = new SettingsApplyReceiptManager();
                var dispatcher = Dispatcher.CurrentDispatcher;
                var uiDispatcher = new WpfUiDispatcher(() => dispatcher);
                var editCoordinator = new TestEditCoordinator();
                var initialProfile = CreateSampleProfile("p1");

                var initialSettings = new AppSettings
                {
                    Translation = new TranslationSettings { Providers = [initialProfile] }
                };
                var settingsService = new MutableSettingsService(initialSettings);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IAppPaths>(paths);
                services.AddSingleton<ISecretStore>(secretStore);
                services.AddSingleton<IAutoStartService>(autoStartService);
                services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
                services.AddSingleton<IUiDispatcher>(uiDispatcher);
                services.AddSingleton<ThemeService>();
                services.AddSingleton<ISettingsService>(settingsService);
                services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);

                services.AddScoped<TestScopedWitness>();
                services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                {
                    // 绑定 TestScopedWitness 到该 session scope
                    _ = sp.GetRequiredService<TestScopedWitness>();
                    return new GeneralSettingsSaveCoordinator(
                        sp.GetRequiredService<ISettingsService>(),
                        sp.GetRequiredService<ISettingsEditCoordinator>(),
                        sp.GetRequiredService<IAutoStartService>(),
                        sp.GetRequiredService<ISettingsApplyReceiptManager>(),
                        NullLogger<GeneralSettingsSaveCoordinator>.Instance);
                });
                services.AddScoped<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();

                using var rootProvider = services.BuildServiceProvider();
                var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

                var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var commitRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? saveTask = null;
                Task? shutdownTask = null;

                try
                {
                    var orphanedReceipt = new TestReceipt();
                    int coreCommitCalls = 0;

                    editCoordinator.OnCommitWithToken = async (plan, ct) =>
                    {
                        Interlocked.Increment(ref coreCommitCalls);
                        commitEntered.SetResult();
                        await commitRelease.Task;
                        return new SettingsEditResult(SettingsEditStatus.ApplyPending, PendingApply: orphanedReceipt);
                    };

                    // 打开会话 1
                    await presenter.ShowAsync("translation");
                    presenter.ActiveSession.ShouldNotBeNull();
                    var session1 = presenter.ActiveSession!;
                    var vm1 = session1.ViewModel;

                    TestScopedWitness? witness1;
                    lock (TestScopedWitness.Created)
                    {
                        TestScopedWitness.Created.Count.ShouldBe(1);
                        witness1 = TestScopedWitness.Created[0];
                    }
                    witness1.IsDisposed.ShouldBeFalse("会话 1 进行中 scope 未释放");

                    vm1.SelectedProfile!.DisplayName = "p1-interleaved";
                    saveTask = vm1.ExecuteSaveAsync();
                    await commitEntered.Task;

                    // 在提交在途期间关闭窗口并请求停止
                    session1.Window.ForceClose = true;
                    session1.Window.Close();
                    shutdownTask = session1.ShutdownAsync();

                    // 证明在提交返回前，旧 scope 尚未被释放（等待提交及 UI 收尾）
                    witness1.IsDisposed.ShouldBeFalse("提交在途未返回时旧 scope 绝不得被提前释放");

                    // 放行提交返回 ApplyPending
                    commitRelease.SetResult();
                    await saveTask;
                    await shutdownTask;

                    // 等待旧工作及 UI 收尾完全退出
                    await vm1.WaitForActiveWorkAsync();

                    // 断言：收尾完成后旧 scope 正确释放！
                    witness1.IsDisposed.ShouldBeTrue("提交及 UI 收尾完成后旧 scope 必须正确释放");

                    // 证明保存协调器即使在 VM 停止后依然将回执登记到单例 receiptManager
                    receiptManager.HasPendingReceipts.ShouldBeTrue("VM 停止后保存服务仍必须正确登记回执");
                    receiptManager.PendingCount.ShouldBe(1);

                    // 重新打开窗口（会话 2），Presenter 创建全新的独立 scope
                    await presenter.ShowAsync("translation");
                    presenter.ActiveSession.ShouldNotBeNull();
                    var session2 = presenter.ActiveSession!;
                    var vm2 = session2.ViewModel;

                    TestScopedWitness? witness2;
                    lock (TestScopedWitness.Created)
                    {
                        TestScopedWitness.Created.Count.ShouldBe(2);
                        witness2 = TestScopedWitness.Created[1];
                    }
                    witness2.IsDisposed.ShouldBeFalse("会话 2 启动后新 scope 活跃");
                    ReferenceEquals(witness1, witness2).ShouldBeFalse("两次会话必须使用不同的独立 scope");
                    ReferenceEquals(vm1, vm2).ShouldBeFalse("两次会话必须创建不同的 ViewModel 实例");

                    vm2.CanRetryPendingApply.ShouldBeTrue("新会话读取到同一应用级单例回执并展示重试入口");

                    // 实际调用新窗口的重试入口，验证 RetryApply 收到原回执、完成后才移除，且没有再次 Commit/设置写入
                    ISettingsApplyReceipt? receivedReceipt = null;
                    editCoordinator.OnRetryApply = r =>
                    {
                        receivedReceipt = r;
                        return Task.FromResult(new SettingsApplyResult(true));
                    };

                    await vm2.ExecuteRetryPendingApplyAsync();

                    receivedReceipt.ShouldBe(orphanedReceipt, "重试必须将原孤立回执传给协调器");
                    receiptManager.PendingCount.ShouldBe(0, "重试成功后待办回执被产品逻辑移除");
                    vm2.CanRetryPendingApply.ShouldBeFalse();
                    coreCommitCalls.ShouldBe(1, "重试过程严禁再次触发 CommitAsync 写入设置");

                    session2.Window.ForceClose = true;
                    session2.Window.Close();
                    await session2.ShutdownAsync();
                }
                finally
                {
                    commitRelease.TrySetResult();
                    if (saveTask != null)
                    {
                        try { await saveTask; } catch { }
                    }
                    if (shutdownTask != null)
                    {
                        try { await shutdownTask; } catch { }
                    }
                    await presenter.ShutdownAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir7d))
                {
                    try { Directory.Delete(tempDir7d, recursive: true); } catch { }
                }
            }
        });
    }

    // 8. 真实 XAML TextBox 输入且保持焦点，通过实际保存入口保存到隔离 JsonSettingsService
    [Fact]
    public async Task Scenario8_RealFocusedTextBox_FlushesInputBeforeSave_PersistsToIsolatedJsonSettingsService()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "iris-test-reg8-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new TestAppPaths(tempDir);
                var secretStore = Substitute.For<ISecretStore>();
                var autoStart = Substitute.For<IAutoStartService>();
                var receiptManager = new SettingsApplyReceiptManager();
                var uiDispatcher = new WpfUiDispatcher();
                var registry = Substitute.For<ITranslationProviderRegistry>();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IAppPaths>(paths);
                services.AddSingleton<ISecretStore>(secretStore);
                services.AddSingleton<IAutoStartService>(autoStart);
                services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
                services.AddSingleton<IUiDispatcher>(uiDispatcher);
                services.AddSingleton<ThemeService>();
                services.AddSingleton(registry);

                services.AddIrisInfrastructure();
                services.AddIrisCore();
        // This legacy UI fixture supplies no OCR factories; keep OCR as an external boundary.
        services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrProviderRegistry>());

                services.AddScoped<GeneralSettingsSaveCoordinator>();
                services.AddScoped<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();

                using var rootProvider = services.BuildServiceProvider();
                var realSettingsService = rootProvider.GetRequiredService<ISettingsService>();
                await realSettingsService.LoadAsync();

                // 初始化一个已有 Profile
                var existingProfile = CreateSampleProfile("p-initial");
                await realSettingsService.UpdateAsync(s => s with
                {
                    Translation = s.Translation with { Providers = [existingProfile] }
                });
                using var scope = rootProvider.CreateScope();
                var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
                window.DataContext = vm;
                window.Show();
                vm.CurrentPageKey = "translation";

                vm.Profiles.Count.ShouldBe(1);
                vm.SelectedProfile = vm.Profiles[0];
                window.UpdateLayout();

                // 查找 XAML 中的 DisplayNameTextBox
                var tb = (TextBox)window.FindName("DisplayNameTextBox");
                tb.ShouldNotBeNull();

                // 模拟输入并保持焦点（不手动 UpdateSource）
                System.Windows.Input.FocusManager.SetFocusedElement(window, tb);
                tb.Text = "persisted-display-name-via-flush";

                // 执行真实保存入口
                await vm.ExecuteSaveAsync();
                vm.StatusMessage.ShouldBe(UiText.SettingsStatusSaved);

                // 验证已持久化至 realSettingsService 与磁盘
                var reloadedSettings = realSettingsService.Current;
                reloadedSettings.Translation.Providers.Single().DisplayName.ShouldBe("persisted-display-name-via-flush");

                // 读取磁盘 JSON 验证物理持久化
                var jsonContent = await File.ReadAllTextAsync(paths.SettingsFile);
                jsonContent.ShouldContain("persisted-display-name-via-flush");

                window.DataContext = null;
                window.Close();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        });
    }

    // 9. 受控故障注入验证：在放行闸门前注入测试失败异常，验证 finally 路径成功退出在途任务、释放窗口并完成收尾，无挂起与死锁
    [Fact]
    public async Task Scenario9_FaultInjection_CleansUpTasksAndWindows_WhenAssertionFailsBeforeGateRelease()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // 9a: Case 7b 清理路径受控故障注入验证
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var gateAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var gateARelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var ctsA = new CancellationTokenSource();
                f.Edit.OnCommitWithToken = async (plan, ct) =>
                {
                    gateAEntered.SetResult();
                    await gateARelease.Task;
                    ct.ThrowIfCancellationRequested();
                    return new SettingsEditResult(SettingsEditStatus.Applied);
                };

                var planA = new SettingsEditPlan(new GeneralSettingsPatch(null, null, null, null), [new TranslationProfileChange(TranslationProfileChangeKind.Add, null, CreateSampleProfile("pA"), new ProviderCredentialEdit(CredentialEditKind.Keep))], null);
                var taskA = f.SaveCoordinator.CommitPlanAsync(planA, false, false, ctsA.Token);
                await gateAEntered.Task;

                bool injectedExceptionCaught = false;
                try
                {
                    try
                    {
                        // 注入受控断言失败异常（在放行 gateARelease 之前）
                        throw new InvalidOperationException("INJECTED_ASSERTION_FAILURE_BEFORE_GATE_A_RELEASE");
                    }
                    finally
                    {
                        ctsA.Cancel();
                        gateARelease.TrySetResult();
                        try
                        {
                            await taskA;
                        }
                        catch
                        {
                        }
                        await f.SaveCoordinator.WaitForActiveWorkAsync();
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message == "INJECTED_ASSERTION_FAILURE_BEFORE_GATE_A_RELEASE")
                {
                    injectedExceptionCaught = true;
                }

                injectedExceptionCaught.ShouldBeTrue("受控注入异常成功触发");
                taskA.IsCompleted.ShouldBeTrue("finally 清理后 taskA 必须已经完成退出");
            }

            // 9b: Case 7d 清理路径受控故障注入验证
            var tempDir9b = Path.Combine(Path.GetTempPath(), "iris-test-reg9b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir9b);
            try
            {
                var paths = new TestAppPaths(tempDir9b);
                var secretStore = new MemorySecretStore();
                var autoStartService = new TestAutoStartService();
                var receiptManager = new SettingsApplyReceiptManager();
                var dispatcher = Dispatcher.CurrentDispatcher;
                var uiDispatcher = new WpfUiDispatcher(() => dispatcher);
                var editCoordinator = new TestEditCoordinator();
                var initialProfile = CreateSampleProfile("p1");

                var initialSettings = new AppSettings
                {
                    Translation = new TranslationSettings { Providers = [initialProfile] }
                };
                var settingsService = new MutableSettingsService(initialSettings);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IAppPaths>(paths);
                services.AddSingleton<ISecretStore>(secretStore);
                services.AddSingleton<IAutoStartService>(autoStartService);
                services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
                services.AddSingleton<IUiDispatcher>(uiDispatcher);
                services.AddSingleton<ThemeService>();
                services.AddSingleton<ISettingsService>(settingsService);
                services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);

                services.AddScoped<GeneralSettingsSaveCoordinator>();
                services.AddScoped<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();

                using var rootProvider = services.BuildServiceProvider();
                var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

                var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var commitRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? saveTask = null;
                Task? shutdownTask = null;
                bool injectedExceptionCaught7d = false;

                editCoordinator.OnCommitWithToken = async (plan, ct) =>
                {
                    commitEntered.SetResult();
                    await commitRelease.Task;
                    return new SettingsEditResult(SettingsEditStatus.ApplyPending, PendingApply: new TestReceipt());
                };

                try
                {
                    try
                    {
                        await presenter.ShowAsync("translation");
                        var session = presenter.ActiveSession!;
                        var vm = session.ViewModel;
                        vm.SelectedProfile!.DisplayName = "p1-injected";
                        saveTask = vm.ExecuteSaveAsync();
                        await commitEntered.Task;

                        session.Window.ForceClose = true;
                        session.Window.Close();
                        shutdownTask = session.ShutdownAsync();

                        // 注入受控断言失败异常（在放行 commitRelease 之前）
                        throw new InvalidOperationException("INJECTED_ASSERTION_FAILURE_BEFORE_COMMIT_RELEASE");
                    }
                    finally
                    {
                        commitRelease.TrySetResult();
                        if (saveTask != null)
                        {
                            try { await saveTask; } catch { }
                        }
                        if (shutdownTask != null)
                        {
                            try { await shutdownTask; } catch { }
                        }
                        await presenter.ShutdownAsync();
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message == "INJECTED_ASSERTION_FAILURE_BEFORE_COMMIT_RELEASE")
                {
                    injectedExceptionCaught7d = true;
                }

                injectedExceptionCaught7d.ShouldBeTrue("受控注入异常成功触发");
                (saveTask != null).ShouldBeTrue("saveTask 必须已初始化");
                saveTask!.IsCompleted.ShouldBeTrue("finally 清理后 saveTask 必须已经完成退出");
                (shutdownTask != null).ShouldBeTrue("shutdownTask 必须已初始化");
                shutdownTask!.IsCompleted.ShouldBeTrue("finally 清理后 shutdownTask 必须已经完成退出");
                presenter.ActiveSession.ShouldBeNull("finally 清理后 presenter 活跃会话必须已关闭归零");
            }
            finally
            {
                if (Directory.Exists(tempDir9b))
                {
                    try { Directory.Delete(tempDir9b, recursive: true); } catch { }
                }
            }
        });
    }
}
