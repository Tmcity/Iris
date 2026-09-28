using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Iris.Core.Translation;

internal static class TranslationCacheKeyFactory
{
    public static TranslationCacheKey Create(TranslationProviderRegistration registration, ProviderTranslateRequest request)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(request);

        if (request.PreparedPrompt == null)
        {
            throw new ArgumentException("PreparedPrompt must not be null.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Text, nameof(request.Text));
        ArgumentNullException.ThrowIfNull(request.SourceLanguage, nameof(request.SourceLanguage));
        ArgumentNullException.ThrowIfNull(request.TargetLanguage, nameof(request.TargetLanguage));
        ArgumentNullException.ThrowIfNull(request.PreparedPrompt.System, nameof(request.PreparedPrompt.System));
        ArgumentNullException.ThrowIfNull(request.PreparedPrompt.User, nameof(request.PreparedPrompt.User));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendField(hasher, request.Text);
        AppendField(hasher, request.SourceLanguage);
        AppendField(hasher, request.TargetLanguage);
        AppendField(hasher, request.PreparedPrompt.System);
        AppendField(hasher, request.PreparedPrompt.User);

        Span<byte> hash = stackalloc byte[32];
        hasher.GetHashAndReset(hash);
        var fingerprint = Convert.ToHexStringLower(hash);

        return new TranslationCacheKey(registration.Profile.Id, registration.InstanceId, fingerprint);
    }

    private static void AppendField(IncrementalHash hasher, string value)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)value.Length);
        hasher.AppendData(lengthBytes);

        if (value.Length == 0)
        {
            return;
        }

        if (BitConverter.IsLittleEndian)
        {
            hasher.AppendData(MemoryMarshal.AsBytes(value.AsSpan()));
        }
        else
        {
            byte[] charBytes = new byte[value.Length * 2];
            for (int i = 0; i < value.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(charBytes.AsSpan(i * 2, 2), value[i]);
            }
            hasher.AppendData(charBytes);
        }
    }
}
