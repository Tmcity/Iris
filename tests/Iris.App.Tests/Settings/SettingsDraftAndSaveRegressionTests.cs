using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            Enabled = value;
        }
    }

    private sealed class TestReceipt : ISettingsApplyReceipt
    {
        public Guid CommitId { get; } = Guid.NewGuid();
    }

    private sealed class TestEditCoordinator : ISettingsEditCoordinator
    {
        public int TestCalls { get; private set; }
        public int ListCalls { get; private set; }
        public SettingsEditPlan? LastPlan { get; private set; }
        public SettingsEditResult CommitResult = new(SettingsEditStatus.Applied);

        public Func<TranslationProviderDraft, CancellationToken, Task<ProviderTestResult>>? OnTestDraft;
        public Func<TranslationProviderDraft, CancellationToken, Task<IReadOnlyList<string>>>? OnListDraftModels;

        public Func<SettingsEditPlan, Task<SettingsEditResult>>? OnCommit;
        public Func<ISettingsApplyReceipt, Task<SettingsApplyResult>>? OnRetryApply;

        public Task<SettingsEditResult> CommitAsync(SettingsEditPlan plan, CancellationToken cancellationToken = default)
        {
            LastPlan = plan;
            return OnCommit != null ? OnCommit(plan) : Task.FromResult(CommitResult);
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

            // Case 3b: 保存等待期间关停产生零 Commit
            await using (var fStop = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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

            // Case 4b: 外部更换 endpoint 递增 revision，旧模型列表结果被拒绝
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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

            // Case 4c: 测试进行中编辑草稿，触发在途取消
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;

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

            // Case 4d: 测试进行中删除草稿，取消旧任务并丢弃结果
            await using (var f = new TestFixture(CreateSampleProfile("p1"), CreateSampleProfile("p2", 1)))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;

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

            // Case 4e: 列模型进行中执行 Reload，取消旧任务并丢弃结果
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;

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

                    // 模拟引用了一个已被外部移除的自定义模板
                    f.Vm.SelectedProfile.PromptTemplateId = "deleted-custom-template-id";
                    f.Settings.Publish(f.Settings.Current with
                    {
                        General = f.Settings.Current.General with
                        {
                            ShowStartupNotification = !f.Settings.Current.General.ShowStartupNotification
                        }
                    });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    f.Vm.SelectedProfile.PromptTemplateId.ShouldBe("deleted-custom-template-id",
                        "引用的模板已被删除时必须保留原选择并提示，不得静默置空");
                    f.Vm.AvailablePrompts.Any(p => p.Id == "deleted-custom-template-id").ShouldBeTrue("失效模板必须包含在下拉占位项中");
                }
                finally
                {
                    window.DataContext = null;
                    window.Close();
                }
            }

            // Case 5f: 异步重试期间外部新增服务，内部合并正常同步，不产生伪 dirty 排序
            await using (var f = new TestFixture(CreateSampleProfile("p1")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Edit.OnRetryApply = async r =>
                {
                    entered.SetResult();
                    await release.Task;
                    return new SettingsApplyResult(true);
                };
                f.Receipts.TryReserveSlot();
                f.Receipts.ConvertReservationToPending(new TestReceipt(), null);
                var retryTask = f.Vm.ExecuteRetryPendingApplyAsync();
                await entered.Task;

                f.Settings.Publish(f.Settings.Current with
                {
                    Translation = f.Settings.Current.Translation with
                    {
                        Providers = [CreateSampleProfile("p1"), CreateSampleProfile("p2", 1)]
                    }
                });
                release.SetResult();
                await retryTask;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                f.Vm.Profiles.Count.ShouldBe(2, "重试期间外部新增项必须被受控安全接纳");
                f.Vm.Profiles.Select(p => p.Id).ShouldBe(["p1", "p2"]);
                f.Vm.IsOrderDirty.ShouldBeFalse("重试期间外部更新基线与列表保持一致，不得标记排序脏状态");
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

    // 7. 真实 SettingsWindow + WpfUiDispatcher + Presenter 关闭重开与实际单例回执：删除最后 Profile、32 名额含预留的竞争、取消释放、满额 General-only、双重试互不干扰
    [Fact]
    public async Task Scenario7_RealWindowPresenter_HandlesClosingReopening_AndReceipt32QuotaExhaustion()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var initialSettings = new AppSettings
            {
                Translation = new TranslationSettings { Providers = [CreateSampleProfile("p1")] }
            };
            var settingsService = new MutableSettingsService(initialSettings);
            var autoStartService = new TestAutoStartService();
            var editCoordinator = new TestEditCoordinator();
            var receiptManager = new SettingsApplyReceiptManager();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var uiDispatcher = new WpfUiDispatcher(() => dispatcher);

            var services = new ServiceCollection();
            services.AddSingleton<ISettingsService>(settingsService);
            services.AddSingleton<IAutoStartService>(autoStartService);
            services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);
            services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            services.AddScoped<GeneralSettingsSaveCoordinator>(sp => new GeneralSettingsSaveCoordinator(
                settingsService,
                editCoordinator,
                autoStartService,
                receiptManager,
                NullLogger<GeneralSettingsSaveCoordinator>.Instance));

            services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
                settingsService,
                sp.GetRequiredService<ThemeService>(),
                sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
                editCoordinator,
                receiptManager,
                uiDispatcher,
                NullLogger<SettingsViewModel>.Instance));

            services.AddTransient<SettingsWindow>();

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                // 打开窗口 1
                await presenter.ShowAsync("translation");
                presenter.ActiveSession.ShouldNotBeNull();
                var session1 = presenter.ActiveSession!;
                var vm1 = session1.ViewModel;

                // 填满 31 个回执槽位，预留 1 个名额供在途测试
                for (int i = 0; i < 31; i++)
                {
                    receiptManager.TryReserveSlot().ShouldBeTrue();
                    receiptManager.ConvertReservationToPending(new TestReceipt(), null);
                }
                receiptManager.PendingCount.ShouldBe(31);

                // 在途竞争与取消释放：第 32 个槽位在途预留，保存失败后应释放该预留
                editCoordinator.CommitResult = new SettingsEditResult(SettingsEditStatus.Uncommitted);
                vm1.SelectedProfile!.DisplayName = "try-failing-save-with-reservation";
                await vm1.ExecuteSaveAsync();
                receiptManager.PendingCount.ShouldBe(31, "保存未提交失败后必须释放已预留的名额");

                // 现在将第 32 个槽位变为正式待处理回执
                receiptManager.TryReserveSlot().ShouldBeTrue();
                receiptManager.ConvertReservationToPending(new TestReceipt(), null);
                receiptManager.PendingCount.ShouldBe(32);

                // 第 33 次包含 Provider 的保存：因满额被拒绝
                vm1.SelectedProfile.DisplayName = "provider-save-33";
                await vm1.ExecuteSaveAsync();
                vm1.StatusMessage.ShouldBe(UiText.SettingsStatusPendingApplyQuotaFull);

                // General-only 保存：无预留名额，允许正常提交
                vm1.SelectedProfile.DisplayName = "p1"; // 撤销 provider dirty
                vm1.ShowStartupNotification = !vm1.ShowStartupNotification; // 仅改 general
                editCoordinator.CommitResult = new SettingsEditResult(SettingsEditStatus.Applied);
                await vm1.ExecuteSaveAsync();
                vm1.StatusMessage.ShouldBe(UiText.SettingsStatusSaved);

                // 关闭窗口 1 会话
                session1.Window.ForceClose = true;
                session1.Window.Close();
                await session1.ShutdownAsync();

                // 重新打开窗口 2：回执依然存在，可重试
                await presenter.ShowAsync("translation");
                presenter.ActiveSession.ShouldNotBeNull();
                var vm2 = presenter.ActiveSession!.ViewModel;
                vm2.CanRetryPendingApply.ShouldBeTrue("跨窗口会话后待办回执依然保留且允许重试");

                // 删除最后 Profile 并保存提交产生清理回执
                vm2.SelectedProfile = vm2.Profiles[0];
                vm2.ExecuteDeleteProfile();
                vm2.Profiles.Count.ShouldBe(0);

                // 双重试独立性：让 pending apply 失败，auto-start 也失败，两者互不干扰
                editCoordinator.OnRetryApply = r => Task.FromResult(new SettingsApplyResult(false, new Iris.Core.Errors.ErrorInfo(Iris.Core.Errors.ErrorCode.StorageFailure, "retry-failed")));
                await vm2.ExecuteRetryPendingApplyAsync();
                vm2.CanRetryPendingApply.ShouldBeTrue("重试失败后必须保持 CanRetryPendingApply = true");

                // 双重试中的开机自启重试
                autoStartService.Enabled = false;
                await vm2.ExecuteRetryAutoStartAsync();
                // 验证自启重试失败不会抹除 pending apply 状态
                vm2.CanRetryPendingApply.ShouldBeTrue("自启重试状态变更不得干扰待办应用重试入口");

                // 最终重试成功，清理全部回执
                editCoordinator.OnRetryApply = r => Task.FromResult(new SettingsApplyResult(true));
                await vm2.ExecuteRetryPendingApplyAsync();
                receiptManager.PendingCount.ShouldBe(0, "重试完成后待办回执清空");
                vm2.CanRetryPendingApply.ShouldBeFalse();
            }
            finally
            {
                await presenter.ShutdownAsync();
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

                var bootstrapSp = services.BuildServiceProvider();
                var realSettingsService = bootstrapSp.GetRequiredService<ISettingsService>();
                await realSettingsService.LoadAsync();

                // 初始化一个已有 Profile
                var existingProfile = CreateSampleProfile("p-initial");
                await realSettingsService.UpdateAsync(s => s with
                {
                    Translation = s.Translation with { Providers = [existingProfile] }
                });

                // 替换注册已有且加载了数据的 realSettingsService 实例
                services.Remove(services.First(d => d.ServiceType == typeof(ISettingsService)));
                services.AddSingleton<ISettingsService>(realSettingsService);

                services.AddScoped<GeneralSettingsSaveCoordinator>();
                services.AddScoped<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();

                using var rootProvider = services.BuildServiceProvider();
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
}
