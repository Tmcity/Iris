using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Iris.Core.Serialization;

// Generated record constructors otherwise supply null/zero for absent init-only
// members. Retain the established parameterless-constructor defaults and bind
// directly to the existing setters, without Reflection.Emit's per-member stubs.
internal static class IrisJsonDefaults
{
    private static readonly MethodInfo SetterFactory = typeof(IrisJsonDefaults)
        .GetMethod(nameof(CreateSetter), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static void PreserveInitializers(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !info.Type.IsClass || info.Type.GetConstructor(Type.EmptyTypes) is null)
            return;

        var members = info.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) == true)
            .ToArray();
        if (members.Length == 0) return;

        info.CreateObject = () => Activator.CreateInstance(info.Type)!;
        foreach (var member in members)
        {
            if (member.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
            string name = member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? info.Options.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
            var property = info.Properties.FirstOrDefault(candidate => candidate.Name == name);
            if (property is null) continue;
            property.Set = (Action<object, object?>)SetterFactory
                .MakeGenericMethod(member.DeclaringType!, member.PropertyType)
                .Invoke(null, new object[] { member.SetMethod! })!;
        }
    }

    private static Action<object, object?> CreateSetter<TInstance, TValue>(MethodInfo method) where TInstance : class
    {
        var setter = method.CreateDelegate<Action<TInstance, TValue>>();
        return (instance, value) => setter((TInstance)instance, (TValue)value!);
    }
}
