using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Core.Actions;
using Iris.Core.Ocr;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Translation;

namespace Iris.Infrastructure.Settings;

/// <summary>
/// 设置挽救器：在 JsonNode 层执行，使单个非法字段只影响自身，不影响整份设置。
/// </summary>
internal static class SettingsSalvager
{
    public static void Salvage(JsonObject root, ICollection<string> fixes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(fixes);

        // 步骤 1：处理内层数组（providers, customPrompts）
        SalvageTranslationArrays(root, fixes);
        SalvageOcrArrays(root, fixes);

        // 步骤 2：处理热键
        SalvageHotkeys(root, fixes);

        // 步骤 3：处理顶层各节
        SalvageSection<GeneralSettings>(root, "general", fixes);
        SalvageSection<TranslationSettings>(root, "translation", fixes);
        SalvageSection<OcrSettings>(root, "ocr", fixes);
        SalvageSection<SelectionSettings>(root, "selection", fixes);
        SalvageSection<ScreenshotSettings>(root, "screenshot", fixes);
        SalvageSection<FloatingBallSettings>(root, "floatingBall", fixes);
        SalvageSection<TranslateWindowSettings>(root, "translateWindow", fixes);
        SalvageSection<HistorySettings>(root, "history", fixes);
        SalvageSection<NetworkSettings>(root, "network", fixes);

        // 步骤 4：顶层未知属性保留原样（无需操作）
    }

    private static void SalvageTranslationArrays(JsonObject root, ICollection<string> fixes)
    {
        if (!TryGetProperty(root, "translation", out _, out var transNode) || transNode is not JsonObject transObj)
        {
            return;
        }

        if (TryGetProperty(transObj, "providers", out var provKey, out var provNode))
        {
            if (provNode is not JsonArray provArray)
            {
                transObj.Remove(provKey);
                fixes.Add("translation.providers: 已移除（不是数组）");
            }
            else
            {
                for (var i = provArray.Count - 1; i >= 0; i--)
                {
                    var item = provArray[i];
                    var valid = false;
                    if (item is not null)
                    {
                        try
                        {
                            var profile = JsonSerializer.Deserialize<TranslationProviderProfile>(item.ToJsonString(), IrisJson.Options);
                            if (profile != null)
                            {
                                valid = true;
                            }
                        }
                        catch
                        {
                            valid = false;
                        }
                    }

                    if (!valid)
                    {
                        provArray.RemoveAt(i);
                        fixes.Add($"translation.providers[{i}]: 已移除（缺少必填字段或类型错误）");
                    }
                }
            }
        }

        if (TryGetProperty(transObj, "customPrompts", out var cpKey, out var cpNode))
        {
            if (cpNode is not JsonArray cpArray)
            {
                transObj.Remove(cpKey);
                fixes.Add("translation.customPrompts: 已移除（不是数组）");
            }
            else
            {
                for (var i = cpArray.Count - 1; i >= 0; i--)
                {
                    var item = cpArray[i];
                    var valid = false;
                    if (item is not null)
                    {
                        try
                        {
                            var prompt = JsonSerializer.Deserialize<PromptTemplate>(item.ToJsonString(), IrisJson.Options);
                            if (prompt != null)
                            {
                                valid = true;
                            }
                        }
                        catch
                        {
                            valid = false;
                        }
                    }

                    if (!valid)
                    {
                        cpArray.RemoveAt(i);
                        fixes.Add($"translation.customPrompts[{i}]: 已移除（缺少必填字段或类型错误）");
                    }
                }
            }
        }
    }

    private static void SalvageOcrArrays(JsonObject root, ICollection<string> fixes)
    {
        if (!TryGetProperty(root, "ocr", out _, out var ocrNode) || ocrNode is not JsonObject ocrObj)
        {
            return;
        }

        if (TryGetProperty(ocrObj, "providers", out var provKey, out var provNode))
        {
            if (provNode is not JsonArray provArray)
            {
                ocrObj.Remove(provKey);
                fixes.Add("ocr.providers: 已移除（不是数组）");
            }
            else
            {
                for (var i = provArray.Count - 1; i >= 0; i--)
                {
                    var item = provArray[i];
                    var valid = false;
                    if (item is not null)
                    {
                        try
                        {
                            var profile = JsonSerializer.Deserialize<OcrProviderProfile>(item.ToJsonString(), IrisJson.Options);
                            if (profile != null)
                            {
                                valid = true;
                            }
                        }
                        catch
                        {
                            valid = false;
                        }
                    }

                    if (!valid)
                    {
                        ocrArrayRemoveAt(provArray, i);
                        fixes.Add($"ocr.providers[{i}]: 已移除（缺少必填字段或类型错误）");
                    }
                }
            }
        }
    }

    private static void ocrArrayRemoveAt(JsonArray arr, int index) => arr.RemoveAt(index);

    private static void SalvageHotkeys(JsonObject root, ICollection<string> fixes)
    {
        if (!TryGetProperty(root, "hotkeys", out var actualKey, out var hotkeysNode) || hotkeysNode is null)
        {
            return;
        }

        if (hotkeysNode is not JsonObject hotkeysObj)
        {
            root.Remove(actualKey);
            fixes.Add("hotkeys: 已移除（不是对象）");
            return;
        }

        var entries = hotkeysObj.ToList();
        var seenCanonical = new HashSet<string>(StringComparer.Ordinal);
        var newHotkeys = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });

        foreach (var (propName, propVal) in entries)
        {
            if (!Enum.TryParse<IrisAction>(propName, ignoreCase: true, out var action) || !Enum.IsDefined(action))
            {
                fixes.Add($"hotkeys.{propName}: 已移除（非法动作名称）");
                continue;
            }

            var canonicalName = action.ToString();
            var renamed = !string.Equals(propName, canonicalName, StringComparison.Ordinal);

            if (seenCanonical.Contains(canonicalName))
            {
                fixes.Add($"hotkeys.{propName}: 已移除（键名重复）");
                continue;
            }

            if (propVal is null || (propVal is JsonValue jv && jv.TryGetValue<object?>(out var val) && val is null))
            {
                if (renamed)
                {
                    fixes.Add($"hotkeys.{propName}: 键名已规范为 {canonicalName}");
                }

                seenCanonical.Add(canonicalName);
                newHotkeys[canonicalName] = null;
                continue;
            }

            var validBinding = false;
            try
            {
                var binding = JsonSerializer.Deserialize<HotkeyBinding>(propVal.ToJsonString(), IrisJson.Options);
                if (binding != null)
                {
                    const HotkeyModifiers allValid = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win;
                    if ((binding.Modifiers & ~allValid) == 0)
                    {
                        validBinding = true;
                    }
                }
            }
            catch
            {
                validBinding = false;
            }

            if (!validBinding)
            {
                fixes.Add($"hotkeys.{propName}: 已移除（缺少必填字段或类型错误）");
                continue;
            }

            if (renamed)
            {
                fixes.Add($"hotkeys.{propName}: 键名已规范为 {canonicalName}");
            }

            seenCanonical.Add(canonicalName);
            newHotkeys[canonicalName] = JsonNode.Parse(propVal.ToJsonString());
        }

        root[actualKey] = newHotkeys;
    }

    private static void SalvageSection<T>(JsonObject root, string sectionName, ICollection<string> fixes)
    {
        if (!TryGetProperty(root, sectionName, out var actualKey, out var node) || node is null)
        {
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(node.ToJsonString(), IrisJson.Options);
            if (parsed is null)
            {
                root.Remove(actualKey);
                fixes.Add($"{sectionName}: 已重置为默认值（类型错误）");
            }
        }
        catch
        {
            root.Remove(actualKey);
            fixes.Add($"{sectionName}: 已重置为默认值（类型错误）");
        }
    }

    private static bool TryGetProperty(JsonObject obj, string propertyName, out string actualKey, out JsonNode? node)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                actualKey = kvp.Key;
                node = kvp.Value;
                return true;
            }
        }

        actualKey = propertyName;
        node = null;
        return false;
    }
}
