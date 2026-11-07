using System.Text.Json.Serialization;
using Iris.Core.Actions;
using Iris.Core.Settings;

namespace Iris.Core.Serialization;

// Keep generated accessors in the shared assembly instead of emitting a native
// getter/setter method for every settings member at runtime. Unknown caller DTOs
// retain the normal reflection fallback in IrisJson.Create.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(IpcCommand))]
internal sealed partial class IrisJsonContext : JsonSerializerContext;
