using System;
using System.Security.Cryptography;
using System.Text;

namespace Iris.Core.Translation;

internal static class PromptFingerprint
{
    public static string Compute(PromptTemplate template)
    {
        var combined = $"{template.System}\0{template.User}";
        var bytes = Encoding.UTF8.GetBytes(combined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash)[..16];
    }
}
