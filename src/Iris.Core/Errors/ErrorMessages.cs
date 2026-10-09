using Iris.Core.Localization;

namespace Iris.Core.Errors;

/// <summary>Safe user-facing messages in the explicit language selected for this Iris run.</summary>
public static class ErrorMessages
{
    /// <summary>Returns the localized message; undefined codes retain the Unknown fallback.</summary>
    public static string GetDefault(ErrorCode code) => CoreText.Get("Error" + (Enum.IsDefined(code) ? code : ErrorCode.Unknown));
}
