using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Iris.Core.Actions;
using Iris.Core.Ocr;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Infrastructure.Settings;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class SettingsSnapshotTests
{
    [Fact]
    public void Create_NullInput_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => SettingsSnapshot.Create(null!));
    }

    [Fact]
    public void Create_AllCollections_AreCopiedAndIsolatedFromInputMutations()
    {
        var headers = new Dictionary<string, string> { ["X-Header-1"] = "val-1", ["X-Header-2"] = "val-2" };
        var openAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers };
        var profile = new TranslationProviderProfile
        {
            Id = "tp-openai",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "OpenAI",
            Model = "gpt-4o",
            BaseUrl = "https://api.openai.com/v1",
            OpenAIOptions = openAIOptions,
        };
        var providers = new List<TranslationProviderProfile> { profile };

        var customPrompts = new List<PromptTemplate>
        {
            new() { Id = "cp-1", Name = "Custom 1", System = "Sys", User = "Usr" }
        };

        var ocrProviders = new List<OcrProviderProfile>
        {
            new() { Id = "ocr-1", Kind = OcrProviderKind.BaiduCloud, DisplayName = "Baidu" }
        };

        var readOrder = new List<SelectionReadMethod> { SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard };
        var excluded = new List<string> { "proc1.exe" };
        var clipboardUnsafe = new List<string> { "term1.exe" };

        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D")
        };

        var input = new AppSettings
        {
            Hotkeys = hotkeys,
            Translation = new TranslationSettings
            {
                Providers = providers,
                CustomPrompts = customPrompts,
            },
            Ocr = new OcrSettings
            {
                Providers = ocrProviders,
            },
            Selection = new SelectionSettings
            {
                ReadOrder = readOrder,
                ExcludedProcesses = excluded,
                ClipboardUnsafeProcesses = clipboardUnsafe,
            }
        };

        var snapshot = SettingsSnapshot.Create(input);

        // Mutate all input collections
        headers["X-Header-1"] = "mutated-1";
        headers["X-Header-New"] = "bad";
        providers.Clear();
        customPrompts.Clear();
        ocrProviders.Clear();
        readOrder.Clear();
        excluded.Add("hacked.exe");
        clipboardUnsafe.Clear();
        hotkeys[IrisAction.SelectionTranslate] = new(HotkeyModifiers.None, "Z");
        hotkeys[IrisAction.InputTranslate] = new(HotkeyModifiers.Control, "K");

        // Verify snapshot collections are untouched
        snapshot.Hotkeys.Count.ShouldBe(1);
        snapshot.Hotkeys[IrisAction.SelectionTranslate].Key.ShouldBe("D");
        snapshot.Hotkeys[IrisAction.SelectionTranslate].Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        snapshot.Hotkeys.ContainsKey(IrisAction.InputTranslate).ShouldBeFalse();

        snapshot.Translation.Providers.Count.ShouldBe(1);
        snapshot.Translation.Providers[0].Id.ShouldBe("tp-openai");
        snapshot.Translation.Providers[0].OpenAIOptions.ShouldNotBeNull();
        snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders.ShouldNotBeNull();
        snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!["X-Header-1"].ShouldBe("val-1");
        snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!.ContainsKey("X-Header-New").ShouldBeFalse();

        snapshot.Translation.CustomPrompts.Count.ShouldBe(1);
        snapshot.Translation.CustomPrompts[0].Id.ShouldBe("cp-1");

        snapshot.Ocr.Providers.Count.ShouldBe(1);
        snapshot.Ocr.Providers[0].Id.ShouldBe("ocr-1");

        snapshot.Selection.ReadOrder.Count.ShouldBe(2);
        snapshot.Selection.ReadOrder[0].ShouldBe(SelectionReadMethod.UIAutomation);
        snapshot.Selection.ReadOrder[1].ShouldBe(SelectionReadMethod.Clipboard);

        snapshot.Selection.ExcludedProcesses.Count.ShouldBe(1);
        snapshot.Selection.ExcludedProcesses[0].ShouldBe("proc1.exe");

        snapshot.Selection.ClipboardUnsafeProcesses.Count.ShouldBe(1);
        snapshot.Selection.ClipboardUnsafeProcesses[0].ShouldBe("term1.exe");
    }

    [Fact]
    public void Create_ReadOnlyDictionaryWrapperInput_IsStillIsolatedFromUnderlyingMutation()
    {
        var underlyingHeaders = new Dictionary<string, string> { ["X-Auth"] = "secret" };
        var wrappedHeaders = new ReadOnlyDictionary<string, string>(underlyingHeaders);

        var underlyingHotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = new(HotkeyModifiers.Control, "F")
        };
        var wrappedHotkeys = new ReadOnlyDictionary<IrisAction, HotkeyBinding>(underlyingHotkeys);

        var input = new AppSettings
        {
            Hotkeys = wrappedHotkeys,
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-wrapped",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "Wrapped",
                        Model = "test",
                        OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = wrappedHeaders }
                    }
                ]
            }
        };

        var snapshot = SettingsSnapshot.Create(input);

        // Mutate underlying dictionaries
        underlyingHeaders["X-Auth"] = "stolen";
        underlyingHotkeys[IrisAction.InputTranslate] = new(HotkeyModifiers.Alt, "X");

        snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!["X-Auth"].ShouldBe("secret");
        snapshot.Hotkeys[IrisAction.InputTranslate].Key.ShouldBe("F");
    }

    [Fact]
    public void Create_AllCollections_RejectDirectInPlaceMutation_ThroughInterfaces()
    {
        var headers = new Dictionary<string, string> { ["X-Header"] = "val" };
        var input = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "P1",
                        Model = "m1",
                        OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers }
                    }
                ],
                CustomPrompts =
                [
                    new PromptTemplate { Id = "cp-1", Name = "CP1", System = "Sys", User = "Usr" }
                ]
            }
        };

        var snapshot = SettingsSnapshot.Create(input);

        // Hotkeys
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)snapshot.Hotkeys).Clear());
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)snapshot.Hotkeys)[IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.None, "A"));
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<IrisAction, HotkeyBinding>)snapshot.Hotkeys).Remove(IrisAction.InputTranslate));

        // Translation.Providers
        Should.Throw<NotSupportedException>(() =>
            ((IList<TranslationProviderProfile>)snapshot.Translation.Providers).Clear());
        Should.Throw<NotSupportedException>(() =>
            ((IList<TranslationProviderProfile>)snapshot.Translation.Providers).Add(new TranslationProviderProfile { Id = "tp-bad", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "Bad", Model = "bad" }));
        Should.Throw<NotSupportedException>(() =>
            ((IList<TranslationProviderProfile>)snapshot.Translation.Providers)[0] = new TranslationProviderProfile { Id = "tp-bad", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "Bad", Model = "bad" });

        // Translation.Providers[0].OpenAIOptions.ExtraHeaders
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<string, string>)snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!).Clear());
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<string, string>)snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!)["X-New"] = "bad");

        // Translation.CustomPrompts
        Should.Throw<NotSupportedException>(() =>
            ((IList<PromptTemplate>)snapshot.Translation.CustomPrompts).Clear());

        // Ocr.Providers
        Should.Throw<NotSupportedException>(() =>
            ((IList<OcrProviderProfile>)snapshot.Ocr.Providers).Clear());

        // Selection.ReadOrder
        Should.Throw<NotSupportedException>(() =>
            ((IList<SelectionReadMethod>)snapshot.Selection.ReadOrder).Clear());

        // Selection.ExcludedProcesses
        Should.Throw<NotSupportedException>(() =>
            ((IList<string>)snapshot.Selection.ExcludedProcesses).Add("hacked.exe"));

        // Selection.ClipboardUnsafeProcesses
        Should.Throw<NotSupportedException>(() =>
            ((IList<string>)snapshot.Selection.ClipboardUnsafeProcesses).RemoveAt(0));
    }

    [Fact]
    public void Create_NullExtraHeaders_RemainsNull_EmptyCollections_RemainEmpty()
    {
        var input = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-no-options",
                        Kind = TranslationProviderKind.Anthropic,
                        DisplayName = "Anthropic",
                        Model = "claude-sonnet-5",
                        OpenAIOptions = null,
                    },
                    new TranslationProviderProfile
                    {
                        Id = "tp-null-headers",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "OpenAI",
                        Model = "gpt-4o",
                        OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = null },
                    }
                ],
                CustomPrompts = [],
            },
            Selection = new SelectionSettings
            {
                ExcludedProcesses = [],
            }
        };

        var snapshot = SettingsSnapshot.Create(input);

        snapshot.Translation.Providers[0].OpenAIOptions.ShouldBeNull();
        snapshot.Translation.Providers[1].OpenAIOptions.ShouldNotBeNull();
        snapshot.Translation.Providers[1].OpenAIOptions!.ExtraHeaders.ShouldBeNull();
        snapshot.Translation.CustomPrompts.Count.ShouldBe(0);
        snapshot.Selection.ExcludedProcesses.Count.ShouldBe(0);
    }

    [Fact]
    public void Create_AnthropicProfile_WithExtraHeaders_IsAlsoFrozen()
    {
        var headers = new Dictionary<string, string> { ["X-Anthropic-Beta"] = "custom-beta" };
        var profile = new TranslationProviderProfile
        {
            Id = "tp-anthropic",
            Kind = TranslationProviderKind.Anthropic,
            DisplayName = "Anthropic with OpenAI headers",
            Model = "claude-sonnet-5",
            OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers }
        };
        var input = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [profile] }
        };

        var snapshot = SettingsSnapshot.Create(input);

        headers["X-Anthropic-Beta"] = "mutated";
        snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!["X-Anthropic-Beta"].ShouldBe("custom-beta");
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<string, string>)snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!).Clear());
    }

    [Fact]
    public void Create_PreservesDictionaryIterationOrder_And_UsesOrdinalComparer()
    {
        var headers = new Dictionary<string, string>
        {
            ["Z-Key"] = "z",
            ["A-Key"] = "a",
            ["M-Key"] = "m",
        };

        var input = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "P1",
                        Model = "m",
                        OpenAIOptions = new OpenAICompatibleOptions { ExtraHeaders = headers }
                    }
                ]
            }
        };

        var snapshot = SettingsSnapshot.Create(input);
        var snapshotHeaders = snapshot.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!;

        // Order preserved
        snapshotHeaders.Keys.ToArray().ShouldBe(["Z-Key", "A-Key", "M-Key"]);

        // Ordinal comparer
        snapshotHeaders.ContainsKey("z-key").ShouldBeFalse();
        snapshotHeaders.ContainsKey("Z-Key").ShouldBeTrue();
    }

    [Fact]
    public void Create_SerializationCompatibility_ProducesIdenticalIrisJson()
    {
        var input = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-test",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "Test",
                        Model = "gpt-4o",
                        BaseUrl = "https://example.com/v1",
                        OpenAIOptions = new OpenAICompatibleOptions
                        {
                            ExtraHeaders = new Dictionary<string, string> { ["X-Header"] = "val" }
                        }
                    }
                ]
            }
        };

        var jsonBefore = JsonSerializer.Serialize(input, IrisJson.Options);
        var snapshot = SettingsSnapshot.Create(input);
        var jsonAfter = JsonSerializer.Serialize(snapshot, IrisJson.Options);

        jsonAfter.ShouldBe(jsonBefore);

        var roundTripped = JsonSerializer.Deserialize<AppSettings>(jsonAfter, IrisJson.Options)!;
        roundTripped.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!["X-Header"].ShouldBe("val");
    }

    [Fact]
    public void Reflection_AllReachableCollectionPaths_AreCoveredAndFrozen()
    {
        // C10B-1: 使用覆盖全部路径（包括嵌套 ExtraHeaders）的非空 fixture
        var fixture = new AppSettings
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>
            {
                [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control, "D")
            },
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "OpenAI",
                        Model = "gpt-4o",
                        OpenAIOptions = new OpenAICompatibleOptions
                        {
                            ExtraHeaders = new Dictionary<string, string> { ["X-Header"] = "val" }
                        }
                    }
                ],
                CustomPrompts =
                [
                    new PromptTemplate { Id = "cp-1", Name = "CP", System = "Sys", User = "Usr" }
                ]
            },
            Ocr = new OcrSettings
            {
                Providers =
                [
                    new OcrProviderProfile { Id = "ocr-1", Kind = OcrProviderKind.BaiduCloud, DisplayName = "Baidu" }
                ]
            },
            Selection = new SelectionSettings
            {
                ReadOrder = [SelectionReadMethod.UIAutomation],
                ExcludedProcesses = ["proc.exe"],
                ClipboardUnsafeProcesses = ["term.exe"]
            }
        };

        var snapshot = SettingsSnapshot.Create(fixture);

        // 1. 递归类型图，收集 AppSettings 声明模型中所有可达集合属性路径
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        void DiscoverCollectionPathsInTypeGraph(Type type, string currentPath)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            {
                return;
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var propType = prop.PropertyType;
                var fullPath = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";

                if (typeof(IEnumerable).IsAssignableFrom(propType) && propType != typeof(string))
                {
                    expectedPaths.Add(fullPath);
                    // 如果集合元素类型属于 Core 模型，也继续检查其内部可能嵌套的集合属性
                    var elementType = propType.IsGenericType ? propType.GetGenericArguments()[0] : null;
                    if (elementType != null && elementType.Assembly == typeof(AppSettings).Assembly && !elementType.IsEnum)
                    {
                        DiscoverCollectionPathsInTypeGraph(elementType, $"{fullPath}[]");
                    }
                }
                else if (propType.Assembly == typeof(AppSettings).Assembly && !propType.IsEnum)
                {
                    DiscoverCollectionPathsInTypeGraph(propType, fullPath);
                }
            }
        }

        DiscoverCollectionPathsInTypeGraph(typeof(AppSettings), "");

        // 2. 遍历已构建的 snapshot 对象图，收集所有实际集合实例
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var actualCollections = new Dictionary<string, object>(StringComparer.Ordinal);

        void TraverseInstance(object? obj, string currentPath)
        {
            if (obj == null || !visited.Add(obj))
            {
                return;
            }

            var type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            {
                return;
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var propType = prop.PropertyType;
                var propValue = prop.GetValue(obj);
                var fullPath = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";

                if (typeof(IEnumerable).IsAssignableFrom(propType) && propType != typeof(string))
                {
                    if (propValue != null)
                    {
                        actualCollections[fullPath] = propValue;
                        if (propValue is IEnumerable enumerable)
                        {
                            var index = 0;
                            foreach (var item in enumerable)
                            {
                                if (item != null)
                                {
                                    TraverseInstance(item, $"{fullPath}[{index}]");
                                }
                                index++;
                            }
                        }
                    }
                }
                else if (propType.Assembly == typeof(AppSettings).Assembly && !propType.IsEnum)
                {
                    TraverseInstance(propValue, fullPath);
                }
            }
        }

        TraverseInstance(snapshot, "");

        // 3. 验证所有 8 处集合路径全部被实际 fixture 覆盖
        actualCollections.ContainsKey("Hotkeys").ShouldBeTrue();
        actualCollections.ContainsKey("Translation.Providers").ShouldBeTrue();
        actualCollections.ContainsKey("Translation.Providers[0].OpenAIOptions.ExtraHeaders").ShouldBeTrue();
        actualCollections.ContainsKey("Translation.CustomPrompts").ShouldBeTrue();
        actualCollections.ContainsKey("Ocr.Providers").ShouldBeTrue();
        actualCollections.ContainsKey("Selection.ReadOrder").ShouldBeTrue();
        actualCollections.ContainsKey("Selection.ExcludedProcesses").ShouldBeTrue();
        actualCollections.ContainsKey("Selection.ClipboardUnsafeProcesses").ShouldBeTrue();
        actualCollections.Count.ShouldBe(8);

        // 4. 验证不可修改性检查采用公开集合接口/行为，不绑定具体实现类型
        foreach (var (path, collection) in actualCollections)
        {
            if (collection is IDictionary dict)
            {
                dict.IsReadOnly.ShouldBeTrue($"Collection at '{path}' must report IsReadOnly=true");
                Should.Throw<NotSupportedException>(() => dict.Clear());
            }
            else if (collection is IList list)
            {
                list.IsReadOnly.ShouldBeTrue($"Collection at '{path}' must report IsReadOnly=true");
                Should.Throw<NotSupportedException>(() => list.Clear());
            }
            else
            {
                throw new InvalidOperationException($"Collection at '{path}' of type {collection.GetType()} should implement IList or IDictionary");
            }
        }
    }
}
