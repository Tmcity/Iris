using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Iris.Core.Actions;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Core.Translation;

namespace Iris.Infrastructure.Settings;

/// <summary>
/// 纯设置快照构建器（ARCH v1.14 §12.2，T-010B）。
/// 负责对规范化后的 AppSettings 进行全量可达集合的深度复制与只读封装，构建深度不可变的运行时发布快照。
/// 不执行规范化、I/O、事件派发或日志记录。
/// </summary>
internal static class SettingsSnapshot
{
    public static AppSettings Create(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 1. Hotkeys：复制字典后只读封装，保留绑定与枚举顺序
        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>();
        if (settings.Hotkeys != null)
        {
            foreach (var pair in settings.Hotkeys)
            {
                hotkeys[pair.Key] = pair.Value;
            }
        }
        var readOnlyHotkeys = new ReadOnlyDictionary<IrisAction, HotkeyBinding>(hotkeys);

        // 2. Translation：复制 Providers（含每个 Profile 中可能存在的 ExtraHeaders）与 CustomPrompts
        var translation = settings.Translation ?? new TranslationSettings();
        var frozenProviders = new List<TranslationProviderProfile>();
        if (translation.Providers != null)
        {
            foreach (var profile in translation.Providers)
            {
                if (profile.OpenAIOptions != null)
                {
                    IReadOnlyDictionary<string, string>? frozenHeaders = null;
                    if (profile.OpenAIOptions.ExtraHeaders != null)
                    {
                        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var pair in profile.OpenAIOptions.ExtraHeaders)
                        {
                            headers[pair.Key] = pair.Value;
                        }
                        frozenHeaders = new ReadOnlyDictionary<string, string>(headers);
                    }

                    var frozenOpenAIOptions = profile.OpenAIOptions with { ExtraHeaders = frozenHeaders };
                    frozenProviders.Add(profile with { OpenAIOptions = frozenOpenAIOptions });
                }
                else
                {
                    frozenProviders.Add(profile);
                }
            }
        }

        var frozenCustomPrompts = translation.CustomPrompts != null
            ? Array.AsReadOnly(translation.CustomPrompts.ToArray())
            : Array.AsReadOnly(Array.Empty<PromptTemplate>());

        var frozenTranslation = translation with
        {
            Providers = Array.AsReadOnly(frozenProviders.ToArray()),
            CustomPrompts = frozenCustomPrompts,
        };

        // 3. Ocr：复制 Providers 列表
        var ocr = settings.Ocr ?? new OcrSettings();
        var frozenOcrProviders = ocr.Providers != null
            ? Array.AsReadOnly(ocr.Providers.ToArray())
            : Array.AsReadOnly(Array.Empty<OcrProviderProfile>());

        var frozenOcr = ocr with
        {
            Providers = frozenOcrProviders,
        };

        // 4. Selection：复制 ReadOrder、ExcludedProcesses、ClipboardUnsafeProcesses
        var selection = settings.Selection ?? new SelectionSettings();
        var frozenReadOrder = selection.ReadOrder != null
            ? Array.AsReadOnly(selection.ReadOrder.ToArray())
            : Array.AsReadOnly(Array.Empty<SelectionReadMethod>());

        var frozenExcluded = selection.ExcludedProcesses != null
            ? Array.AsReadOnly(selection.ExcludedProcesses.ToArray())
            : Array.AsReadOnly(Array.Empty<string>());

        var frozenClipboardUnsafe = selection.ClipboardUnsafeProcesses != null
            ? Array.AsReadOnly(selection.ClipboardUnsafeProcesses.ToArray())
            : Array.AsReadOnly(Array.Empty<string>());

        var frozenSelection = selection with
        {
            ReadOrder = frozenReadOrder,
            ExcludedProcesses = frozenExcluded,
            ClipboardUnsafeProcesses = frozenClipboardUnsafe,
        };

        return settings with
        {
            Hotkeys = readOnlyHotkeys,
            Translation = frozenTranslation,
            Ocr = frozenOcr,
            Selection = frozenSelection,
        };
    }
}
