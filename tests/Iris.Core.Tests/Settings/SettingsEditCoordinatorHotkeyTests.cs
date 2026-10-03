using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Settings;

public sealed class SettingsEditCoordinatorHotkeyTests
{
    private sealed class TestSettingsService : ISettingsService
    {
        private AppSettings _current;
        public int UpdateCallCount { get; private set; }
        public Func<AppSettings, Task>? BeforeMutateHookAsync { get; set; }
        public Func<AppSettings, AppSettings>? BeforeMutateHook { get; set; }
        public Func<AppSettings, Task>? AfterUpdateHookAsync { get; set; }

        public TestSettingsService(AppSettings initial)
        {
            _current = initial;
        }

        public AppSettings Current => _current;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCallCount++;

            if (BeforeMutateHookAsync != null)
            {
                await BeforeMutateHookAsync(_current);
            }

            var old = _current;
            if (BeforeMutateHook != null)
            {
                _current = BeforeMutateHook(_current);
                old = _current;
            }

            var next = mutate(old);
            if (ReferenceEquals(old, next))
            {
                return;
            }

            _current = next;
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, _current));

            if (AfterUpdateHookAsync != null)
            {
                await AfterUpdateHookAsync(_current);
            }
        }

        public event EventHandler<SettingsChangedEventArgs>? Changed;
    }

    private sealed class TestSecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);
        public List<string> SetCalls { get; } = new();
        public List<string> RemoveCalls { get; } = new();

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

    private sealed class TestConfigurationObserver : IProviderConfigurationObserver
    {
        public int RefreshCount { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }
    }

    private static (SettingsEditCoordinator coordinator, TestSettingsService settingsService, TestSecretStore secretStore, TestConfigurationObserver observer)
        CreateHarness(AppSettings? initial = null)
    {
        var settings = initial ?? new AppSettings();
        var settingsService = new TestSettingsService(settings);
        var secretStore = new TestSecretStore();
        var registry = Substitute.For<ITranslationProviderRegistry>();
        registry.RefreshAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var observer = new TestConfigurationObserver();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        var timeProvider = new FakeTimeProvider();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsEditCoordinator>.Instance;
        var hotkeyPolicy = new HotkeyBindingPolicy();

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new[] { observer },
            promptProvider,
            timeProvider,
            logger,
            hotkeyPolicy);

        return (coordinator, settingsService, secretStore, observer);
    }

    [Fact]
    public async Task HotkeyOnly_SingleUpdateAsync_Applied_DoesNotTouchSecretStoreOrObservers()
    {
        var (coordinator, settingsService, secretStore, observer) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    baseBinding,
                    new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "D", Enabled: true))
            ]
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        result.IsConflict.ShouldBeFalse();
        result.Error.ShouldBeNull();

        settingsService.UpdateCallCount.ShouldBe(1);
        settingsService.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "D", Enabled: true));

        // 快捷键-only 严禁触碰凭据与观察者屏障
        secretStore.SetCalls.ShouldBeEmpty();
        secretStore.RemoveCalls.ShouldBeEmpty();
        observer.RefreshCount.ShouldBe(0);
    }

    [Fact]
    public async Task GeneralAndHotkey_SingleUpdateAsync_Applied()
    {
        var (coordinator, settingsService, secretStore, observer) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.InputTranslate];

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(Theme: new SettingsFieldEdit<AppTheme>(AppTheme.System, AppTheme.Dark)),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.InputTranslate,
                    baseBinding,
                    new HotkeyBinding(HotkeyModifiers.Alt, "F", Enabled: true))
            ]
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.UpdateCallCount.ShouldBe(1);
        settingsService.Current.General.Theme.ShouldBe(AppTheme.Dark);
        settingsService.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.Alt, "F", Enabled: true));

        secretStore.SetCalls.ShouldBeEmpty();
        observer.RefreshCount.ShouldBe(0);
    }

    [Fact]
    public async Task ProviderAndHotkey_SingleJointCommit_Applied()
    {
        var (coordinator, settingsService, secretStore, observer) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];

        var newProfile = new TranslationProviderProfile
        {
            Id = "tp-" + Guid.NewGuid().ToString("N")[..12],
            DisplayName = "Test LLM",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.example.com/v1",
            Model = "test-model",
            Enabled = true
        };

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    null,
                    newProfile,
                    new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test-key", null)))
            ],
            new TranslationOrderChange([], [newProfile.Id]))
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    baseBinding,
                    new HotkeyBinding(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "S", Enabled: true))
            ]
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.UpdateCallCount.ShouldBe(1); // 严格保证一次原子联合提交
        settingsService.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "S", Enabled: true));
        settingsService.Current.Translation.Providers.Any(p => p.Id == newProfile.Id).ShouldBeTrue();

        secretStore.SetCalls.Count.ShouldBe(1);
        observer.RefreshCount.ShouldBe(1);
    }

    [Fact]
    public async Task HotkeySwap_EvaluatedOnCandidateDictionary_Applied()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var selBase = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate]; // Ctrl+Alt+D
        var inBase = settingsService.Current.Hotkeys[IrisAction.InputTranslate];        // Ctrl+Alt+F

        // 相互交换快捷键：SelectionTranslate 改为 Ctrl+Alt+F，InputTranslate 改为 Ctrl+Alt+D
        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    selBase,
                    new HotkeyBinding(inBase.Modifiers, inBase.Key, Enabled: true)),
                new HotkeyBindingChange(
                    IrisAction.InputTranslate,
                    inBase,
                    new HotkeyBinding(selBase.Modifiers, selBase.Key, Enabled: true))
            ]
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
            new HotkeyBinding(inBase.Modifiers, inBase.Key, Enabled: true));
        settingsService.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(
            new HotkeyBinding(selBase.Modifiers, selBase.Key, Enabled: true));
    }

    [Fact]
    public async Task DuplicateEnabledTarget_ReturnsInvalidArgument()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var selBase = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];
        var inBase = settingsService.Current.Hotkeys[IrisAction.InputTranslate];

        // 尝试将 InputTranslate 也改为与 SelectionTranslate 相同的组合 (Ctrl+Alt+D)
        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.InputTranslate,
                    inBase,
                    new HotkeyBinding(selBase.Modifiers, selBase.Key, Enabled: true))
            ]
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        result.IsConflict.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        result.Error.Message.ShouldContain("快捷键组合已分配给其他动作，不能重复使用");
    }

    [Fact]
    public async Task DisabledTarget_DoesNotCollideWithOtherBindings()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var selBase = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];
        var inBase = settingsService.Current.Hotkeys[IrisAction.InputTranslate];

        // 将两个动作都设为禁用空键，或设为禁用的相同按键
        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    selBase,
                    new HotkeyBinding(HotkeyModifiers.None, "", Enabled: false)),
                new HotkeyBindingChange(
                    IrisAction.InputTranslate,
                    inBase,
                    new HotkeyBinding(HotkeyModifiers.None, "", Enabled: false))
            ]
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.None, string.Empty, Enabled: false));
        settingsService.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.None, string.Empty, Enabled: false));
    }

    [Fact]
    public async Task IdempotentTarget_DoesNotForceVersionBump()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate]; // Ctrl+D

        // 目标为语义完全相同的绑定（仅大小写和空白差异）
        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    baseBinding,
                    new HotkeyBinding(baseBinding.Modifiers, "  d  ", Enabled: true))
            ]
        };

        bool changedFired = false;
        settingsService.Changed += (_, _) => changedFired = true;

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        changedFired.ShouldBeFalse(); // 幂等操作不触发 Changed 事件
    }

    [Fact]
    public async Task ConcurrencyConflict_SameActionModified_ReturnsConflict()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    baseBinding,
                    new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true))
            ]
        };

        // 模拟外部并发修改：SelectionTranslate 被改成了 Ctrl+Shift+D
        await settingsService.UpdateAsync(cur =>
        {
            var dict = new Dictionary<IrisAction, HotkeyBinding>(cur.Hotkeys)
            {
                [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "D", Enabled: true)
            };
            return cur with { Hotkeys = dict };
        }, TestContext.Current.CancellationToken);

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        result.IsConflict.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error.Message.ShouldBe("配置已在其他位置发生更改，请刷新后重试");
    }

    [Fact]
    public async Task UnrelatedActionConcurrentChange_PreservedOnCommit()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var selBase = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    selBase,
                    new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true))
            ]
        };

        // 并发修改了无关动作 InputTranslate
        await settingsService.UpdateAsync(cur =>
        {
            var dict = new Dictionary<IrisAction, HotkeyBinding>(cur.Hotkeys)
            {
                [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K", Enabled: true)
            };
            return cur with { Hotkeys = dict };
        }, TestContext.Current.CancellationToken);

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        // 本计划编辑的 SelectionTranslate 成功应用
        settingsService.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true));
        // 无关动作 InputTranslate 的并发修改得以保留！
        settingsService.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K", Enabled: true));
    }

    [Fact]
    public async Task DefensiveCapture_CallerModifyingList_DoesNotAffectCapturedPlan()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];

        var list = new List<HotkeyBindingChange>
        {
            new(IrisAction.SelectionTranslate, baseBinding, new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true))
        };

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges = list
        };

        var mutateEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutateRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        settingsService.BeforeMutateHookAsync = async _ =>
        {
            mutateEntered.TrySetResult(true);
            await mutateRelease.Task;
        };

        var commitTask = coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        try
        {
            // 确认已在入口完成捕获并已进入 UpdateAsync，但在 mutate 之前被闸门阻塞
            await mutateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            commitTask.IsCompleted.ShouldBeFalse();

            // 在提交在途中清空并污染调用方持有的原列表引用
            list.Clear();
            list.Add(null!);
        }
        finally
        {
            mutateRelease.TrySetResult(true);
        }

        var result = await commitTask;

        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true));
    }

    [Fact]
    public async Task StructuralErrors_RejectedWithInvalidArgument()
    {
        var (coordinator, _, _, _) = CreateHarness();

        // 1. HotkeyChanges 为 null
        var planNullList = new SettingsEditPlan(new GeneralSettingsPatch(), [])
        {
            HotkeyChanges = null!
        };
        var res1 = await coordinator.CommitAsync(planNullList, TestContext.Current.CancellationToken);
        res1.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res1.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 2. 包含 null 元素
        var planNullItem = new SettingsEditPlan(new GeneralSettingsPatch(), [])
        {
            HotkeyChanges = [null!]
        };
        var res2 = await coordinator.CommitAsync(planNullItem, TestContext.Current.CancellationToken);
        res2.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res2.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 3. 未定义的 Action
        var planUndefinedAction = new SettingsEditPlan(new GeneralSettingsPatch(), [])
        {
            HotkeyChanges = [new HotkeyBindingChange((IrisAction)999, null, new HotkeyBinding(HotkeyModifiers.Control, "K"))]
        };
        var res3 = await coordinator.CommitAsync(planUndefinedAction, TestContext.Current.CancellationToken);
        res3.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res3.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 4. TargetBinding 为 null
        var planNullTarget = new SettingsEditPlan(new GeneralSettingsPatch(), [])
        {
            HotkeyChanges = [new HotkeyBindingChange(IrisAction.InputTranslate, null, null!)]
        };
        var res4 = await coordinator.CommitAsync(planNullTarget, TestContext.Current.CancellationToken);
        res4.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res4.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 5. 重复的 Action 变更
        var planDuplicateAction = new SettingsEditPlan(new GeneralSettingsPatch(), [])
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(IrisAction.InputTranslate, null, new HotkeyBinding(HotkeyModifiers.Control, "K")),
                new HotkeyBindingChange(IrisAction.InputTranslate, null, new HotkeyBinding(HotkeyModifiers.Alt, "K"))
            ]
        };
        var res5 = await coordinator.CommitAsync(planDuplicateAction, TestContext.Current.CancellationToken);
        res5.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res5.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task PreStagingInputError_ZeroCredentialsStaged()
    {
        var (coordinator, _, secretStore, _) = CreateHarness();

        var newProfile = new TranslationProviderProfile
        {
            Id = "tp-" + Guid.NewGuid().ToString("N")[..12],
            DisplayName = "Test LLM",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.example.com/v1",
            Model = "test-model",
            Enabled = true
        };

        // 目标快捷键包含非法按键 "F12"（启用状态下 F12 非法）
        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    null,
                    newProfile,
                    new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test-key", null)))
            ],
            new TranslationOrderChange([], [newProfile.Id]))
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    null,
                    new HotkeyBinding(HotkeyModifiers.Control, "F12", Enabled: true))
            ]
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        result.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);
        result.Error.Message.ShouldBe("快捷键目标绑定无效");

        // 凭据 staging 阶段根本未执行，零副作用
        secretStore.SetCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task StagingRollback_OnHotkeyConflictInMutate()
    {
        var (coordinator, settingsService, secretStore, _) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];

        var newProfile = new TranslationProviderProfile
        {
            Id = "tp-" + Guid.NewGuid().ToString("N")[..12],
            DisplayName = "Test LLM",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.example.com/v1",
            Model = "test-model",
            Enabled = true
        };

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    null,
                    newProfile,
                    new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test-key", null)))
            ],
            new TranslationOrderChange([], [newProfile.Id]))
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    baseBinding,
                    new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true))
            ]
        };

        // 在 UpdateAsync mutate 前夕制造并发冲突：SelectionTranslate 被并发更改
        settingsService.BeforeMutateHook = cur =>
        {
            var dict = new Dictionary<IrisAction, HotkeyBinding>(cur.Hotkeys)
            {
                [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Z", Enabled: true)
            };
            return cur with { Hotkeys = dict };
        };

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        result.IsConflict.ShouldBeTrue();
        result.Error!.Message.ShouldBe("配置已在其他位置发生更改，请刷新后重试");

        // staging 已暂存凭据，但在发现快捷键并发冲突后已成功回滚清理！
        secretStore.SetCalls.Count.ShouldBe(1);
        secretStore.RemoveCalls.Count.ShouldBe(1);
        secretStore.Storage.ShouldBeEmpty();
    }

    [Fact]
    public async Task LateCancellation_AfterCommitPoint_KeepsCommitted()
    {
        var (coordinator, settingsService, _, _) = CreateHarness();
        var baseBinding = settingsService.Current.Hotkeys[IrisAction.SelectionTranslate];
        using var cts = new CancellationTokenSource();

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [],
            null)
        {
            HotkeyChanges =
            [
                new HotkeyBindingChange(
                    IrisAction.SelectionTranslate,
                    baseBinding,
                    new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true))
            ]
        };

        // 在 UpdateAsync 完成后触发取消
        settingsService.AfterUpdateHookAsync = _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var result = await coordinator.CommitAsync(plan, cts.Token);

        // 提交点达成后忽略迟到取消，结果为 Applied 且提交事实不撤回
        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
            new HotkeyBinding(HotkeyModifiers.Alt, "S", Enabled: true));
    }
}
