using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Core.Serialization;

/// <summary>
/// 设置文件 / IPC 使用的统一 JSON 选项：camelCase 属性名、枚举为字符串（PascalCase 名称）、
/// 忽略 null、允许注释与尾逗号、缩进输出。Provider 的线协议 DTO 不使用本选项（见 ProviderJson）。
/// </summary>
public static class IrisJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
