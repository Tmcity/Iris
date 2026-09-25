using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Iris.Core.Actions;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Shouldly;

namespace Iris.Infrastructure.Tests.TestSupport;

public static class SettingsTestInvariants
{
    public static void AssertInvariants(AppSettings settings)
    {
        settings.ShouldNotBeNull();

        // 1. Hotkeys 中没有 null 值
        settings.Hotkeys.ShouldNotBeNull();
        foreach (var (action, binding) in settings.Hotkeys)
        {
            binding.ShouldNotBeNull($"Hotkeys[{action}] must not be null");
            Enum.IsDefined(action).ShouldBeTrue($"Hotkeys action {(int)action} must be defined");
            const HotkeyModifiers allModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win;
            (binding.Modifiers & ~allModifiers).ShouldBe((HotkeyModifiers)0);
            string.IsNullOrWhiteSpace(binding.Key).ShouldBeFalse();
        }

        // 2. 所有枚举值都已定义
        Enum.IsDefined(settings.General.Theme).ShouldBeTrue();
        Enum.IsDefined(settings.Translation.AutoCopy).ShouldBeTrue();
        Enum.IsDefined(settings.Translation.SelectionLineBreaks).ShouldBeTrue();
        foreach (var p in settings.Translation.Providers)
        {
            Enum.IsDefined(p.Kind).ShouldBeTrue();
            if (p.OpenAIOptions != null)
            {
                Enum.IsDefined(p.OpenAIOptions.MaxTokensField).ShouldBeTrue();
            }
        }

        Enum.IsDefined(settings.Ocr.TextLayout).ShouldBeTrue();
        foreach (var p in settings.Ocr.Providers)
        {
            Enum.IsDefined(p.Kind).ShouldBeTrue();
            if (p.BaiduOptions != null)
            {
                Enum.IsDefined(p.BaiduOptions.Mode).ShouldBeTrue();
            }
        }

        Enum.IsDefined(settings.Selection.MouseMode).ShouldBeTrue();
        foreach (var m in settings.Selection.ReadOrder)
        {
            Enum.IsDefined(m).ShouldBeTrue();
        }

        Enum.IsDefined(settings.FloatingBall.ClickAction).ShouldBeTrue();
        Enum.IsDefined(settings.TranslateWindow.Placement).ShouldBeTrue();
        Enum.IsDefined(settings.Network.ProxyMode).ShouldBeTrue();

        // 3. ActiveProviderId 能按序数比较找到对应 Profile
        settings.Ocr.Providers.Any(p => string.Equals(p.Id, settings.Ocr.ActiveProviderId, StringComparison.Ordinal)).ShouldBeTrue(
            $"ActiveProviderId '{settings.Ocr.ActiveProviderId}' must match an OCR profile with exact ordinal comparison");

        if (settings.Ocr.FallbackProviderId != null)
        {
            settings.Ocr.Providers.Any(p => string.Equals(p.Id, settings.Ocr.FallbackProviderId, StringComparison.Ordinal)).ShouldBeTrue(
                $"FallbackProviderId '{settings.Ocr.FallbackProviderId}' must match an OCR profile with exact ordinal comparison");
            string.Equals(settings.Ocr.FallbackProviderId, settings.Ocr.ActiveProviderId, StringComparison.Ordinal).ShouldBeFalse();
        }

        // 4. 无 null 的非可空成员
        var nullability = new NullabilityInfoContext();
        AssertNoNullInNonNullableMembers(settings, nullability, new HashSet<object>());
    }

    public static void AssertNoNullInNonNullableMembers(object? obj, NullabilityInfoContext nullabilityContext, HashSet<object> visited)
    {
        if (obj == null) return;
        if (!visited.Add(obj)) return;

        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string)) return;

        if (obj is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                AssertNoNullInNonNullableMembers(entry.Key, nullabilityContext, visited);
                AssertNoNullInNonNullableMembers(entry.Value, nullabilityContext, visited);
            }
            return;
        }

        if (obj is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                AssertNoNullInNonNullableMembers(item, nullabilityContext, visited);
            }
            return;
        }

        if (type.Namespace == null || !type.Namespace.StartsWith("Iris.Core", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var nullability = nullabilityContext.Create(prop);
            var value = prop.GetValue(obj);

            if (!prop.PropertyType.IsValueType)
            {
                if (nullability.WriteState == NullabilityState.NotNull || nullability.ReadState == NullabilityState.NotNull)
                {
                    value.ShouldNotBeNull($"{type.Name}.{prop.Name} is non-nullable but was null");
                }
            }

            if (value != null)
            {
                AssertNoNullInNonNullableMembers(value, nullabilityContext, visited);
            }
        }
    }
}
