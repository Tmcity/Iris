using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Infrastructure.Settings;
using Iris.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class JsonSettingsServiceHotkeyTests
{
    private static JsonSettingsService CreateService(
        TempDirectory dir,
        IEnumerable<ISettingsMigration>? migrations = null,
        TimeProvider? timeProvider = null)
    {
        var paths = new FakeAppPaths(dir.Path);
        var migrationList = migrations ?? Array.Empty<ISettingsMigration>();
        var time = timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        return new JsonSettingsService(paths, migrationList, time, NullLogger<JsonSettingsService>.Instance);
    }

    [Fact]
    public async Task SaveDisabledEmptyBinding_NewInstanceLoads_PreservesDisabledEmptyBinding()
    {
        using var dir = new TempDirectory();

        // 1. 第一个实例：加载默认设置并将 SelectionTranslate（原本有默认值 Ctrl+D）明确保存为禁用空键
        using (var service1 = CreateService(dir))
        {
            await service1.LoadAsync(TestContext.Current.CancellationToken);
            service1.Current.Hotkeys[IrisAction.SelectionTranslate].Enabled.ShouldBeTrue();

            await service1.UpdateAsync(cur =>
            {
                var dict = new Dictionary<IrisAction, HotkeyBinding>(cur.Hotkeys)
                {
                    [IrisAction.SelectionTranslate] = new(HotkeyModifiers.None, string.Empty, Enabled: false)
                };
                return cur with { Hotkeys = dict };
            }, TestContext.Current.CancellationToken);

            service1.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
                new HotkeyBinding(HotkeyModifiers.None, string.Empty, Enabled: false));
        }

        // 2. 第二个新实例：使用同一目录加载，验证明确禁用的空键不会被 N2 回填为默认绑定
        using (var service2 = CreateService(dir))
        {
            await service2.LoadAsync(TestContext.Current.CancellationToken);

            service2.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
                new HotkeyBinding(HotkeyModifiers.None, string.Empty, Enabled: false));
            service2.Current.Hotkeys[IrisAction.SelectionTranslate].Enabled.ShouldBeFalse();
            service2.Current.Hotkeys[IrisAction.SelectionTranslate].Key.ShouldBeEmpty();

            // 其余未修改的默认动作依然保持默认绑定
            service2.Current.Hotkeys[IrisAction.InputTranslate].ShouldBe(HotkeyDefaults.Create()[IrisAction.InputTranslate]);
        }
    }

    [Fact]
    public async Task EnabledBlankKey_RepairedWithDefaultOnLoad()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        // 构造 JSON：包含 Enabled=true 且 Key 为空白的条目
        var defaults = new AppSettings();
        var customHotkeys = new Dictionary<IrisAction, HotkeyBinding>(defaults.Hotkeys)
        {
            [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control, "   ", Enabled: true)
        };
        var settings = defaults with { Hotkeys = customHotkeys };
        var json = JsonSerializer.Serialize(settings, IrisJson.Options);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        // 加载时应当被 N2 修复为默认绑定
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(HotkeyDefaults.Create()[IrisAction.SelectionTranslate]);
    }

    [Fact]
    public async Task MissingDefaultAction_RepairedWithDefaultOnLoad()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        // 构造 JSON：完全缺失 SelectionTranslate
        var defaults = new AppSettings();
        var customHotkeys = new Dictionary<IrisAction, HotkeyBinding>(defaults.Hotkeys);
        customHotkeys.Remove(IrisAction.SelectionTranslate);

        var settings = defaults with { Hotkeys = customHotkeys };
        var json = JsonSerializer.Serialize(settings, IrisJson.Options);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);

        // 加载时缺失项应当被 N2 补齐为默认绑定
        using var service = CreateService(dir);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.Hotkeys.ContainsKey(IrisAction.SelectionTranslate).ShouldBeTrue();
        service.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(HotkeyDefaults.Create()[IrisAction.SelectionTranslate]);
    }

    [Fact]
    public async Task DisabledNonEmptyBinding_PreservedOnReload()
    {
        using var dir = new TempDirectory();

        using (var service1 = CreateService(dir))
        {
            await service1.LoadAsync(TestContext.Current.CancellationToken);

            await service1.UpdateAsync(cur =>
            {
                var dict = new Dictionary<IrisAction, HotkeyBinding>(cur.Hotkeys)
                {
                    [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control, "F12", Enabled: false)
                };
                return cur with { Hotkeys = dict };
            }, TestContext.Current.CancellationToken);

            service1.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
                new HotkeyBinding(HotkeyModifiers.Control, "F12", Enabled: false));
        }

        using (var service2 = CreateService(dir))
        {
            await service2.LoadAsync(TestContext.Current.CancellationToken);

            service2.Current.Hotkeys[IrisAction.SelectionTranslate].ShouldBe(
                new HotkeyBinding(HotkeyModifiers.Control, "F12", Enabled: false));
        }
    }
}
