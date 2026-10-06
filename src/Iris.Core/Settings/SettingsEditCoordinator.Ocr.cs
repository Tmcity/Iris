using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Ocr;

namespace Iris.Core.Settings;

internal sealed partial class SettingsEditCoordinator
{
    private readonly record struct CredentialTarget(bool IsOcr, string Id);

    private static bool IsSetReferenced(AppSettings settings, string id) =>
        settings.Translation.Providers.Any(p => p.CredentialSetId == id) || settings.Ocr.Providers.Any(p => p.CredentialSetId == id);
    private static bool IsLegacyReferenced(AppSettings settings, string id) =>
        settings.Translation.Providers.Any(p => p.Id == id && p.CredentialSetId == null)
        || settings.Ocr.Providers.Any(p => p.Id == id && p.CredentialSetId == null);

    private static ErrorInfo InvalidOcr() => new(ErrorCode.InvalidArgument, ErrorMessages.GetDefault(ErrorCode.InvalidArgument));
    private static bool HasOcrEdits(SettingsEditPlan plan) => plan.OcrChanges.Count != 0 || plan.Ocr is { } p &&
        (p.ActiveProviderId != null || p.FallbackProviderId != null || p.TextLayout != null || p.LanguageHint != null
         || p.AutoCopyOnOcr != null || p.SilentOcrShowNotification != null);
    private static bool HasScreenshotEdits(ScreenshotSettingsPatch? p) => p != null &&
        (p.ConfirmOnMouseUp != null || p.ShowMagnifier != null || p.HideIrisWindows != null
         || p.CopyImageToClipboard != null || p.MaskOpacity != null);

    private static OcrSettingsPatch? NormalizeOcrPatch(OcrSettingsPatch? patch) => patch?.LanguageHint is { } language
        ? patch with { LanguageHint = language with { Target = string.IsNullOrWhiteSpace(language.Target) ? LanguageCodes.Auto : language.Target.Trim() } }
        : patch;

    private static OcrProviderProfile CaptureOcrProfile(OcrProviderProfile profile, bool defaults)
    {
        var frozen = OcrRegistryFreeze.Profile(profile);
        return !defaults ? frozen : frozen.Kind switch
        {
            OcrProviderKind.BaiduCloud => frozen with { BaiduOptions = frozen.BaiduOptions ?? new() },
            OcrProviderKind.WindowsLocal => frozen with { WindowsOptions = frozen.WindowsOptions ?? new() },
            _ => frozen
        };
    }

    private static OcrProfileChange CloneOcrChange(OcrProfileChange change) => change with
    {
        BaseProfile = change.BaseProfile is { } b ? CaptureOcrProfile(b, false) : null,
        TargetProfile = change.TargetProfile is { } t ? CaptureOcrProfile(t, true) : null,
        Credentials = change.Credentials with { Replacement = change.Credentials.Replacement is { } c ? c with { } : null }
    };

    private static ErrorInfo? ValidateOcrPlanStructure(SettingsEditPlan plan)
    {
        if (plan.OcrChanges == null) return InvalidOcr();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.OcrChanges)
        {
            if (change == null || !Enum.IsDefined(change.Kind) || change.Credentials == null
                || !Enum.IsDefined(change.Credentials.Kind)) return InvalidOcr();
            var id = (change.BaseProfile ?? change.TargetProfile)?.Id;
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) return InvalidOcr();
            switch (change.Kind)
            {
                case OcrProfileChangeKind.Add:
                    if (change.BaseProfile != null || change.TargetProfile == null || change.TargetProfile.CredentialSetId != null) return InvalidOcr();
                    break;
                case OcrProfileChangeKind.Update:
                    if (change.BaseProfile == null || change.TargetProfile == null
                        || change.BaseProfile.Id != change.TargetProfile.Id || change.BaseProfile.Kind != change.TargetProfile.Kind
                        || change.BaseProfile.CredentialSetId != change.TargetProfile.CredentialSetId) return InvalidOcr();
                    break;
                case OcrProfileChangeKind.Delete:
                    if (change.BaseProfile == null || change.TargetProfile != null || change.Credentials.Kind != CredentialEditKind.Keep
                        || string.Equals(id, BuiltInProviderIds.BaiduOcr, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(id, BuiltInProviderIds.WindowsOcr, StringComparison.OrdinalIgnoreCase)) return InvalidOcr();
                    break;
            }
            var credentialError = ValidateOcrCredentials((change.TargetProfile ?? change.BaseProfile)!, change.Credentials);
            if (credentialError != null) return credentialError;
        }
        return null;
    }

    private static ErrorInfo? ValidateOcrCredentials(OcrProviderProfile profile, ProviderCredentialEdit edit)
    {
        if (!Enum.IsDefined(edit.Kind)) return InvalidOcr();
        if (edit.Kind != CredentialEditKind.Replace) return edit.Replacement == null ? null : InvalidOcr();
        return profile.Kind == OcrProviderKind.BaiduCloud && edit.Replacement is { } c
            && !string.IsNullOrWhiteSpace(c.ApiKey) && !string.IsNullOrWhiteSpace(c.SecretKey) ? null : InvalidOcr();
    }

    private static ErrorInfo? ValidateOcrProfile(OcrProviderProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DisplayName)
            || !Enum.IsDefined(profile.Kind) || profile.TimeoutSeconds is < 5 or > 120) return InvalidOcr();
        if (profile.Kind == OcrProviderKind.BaiduCloud)
        {
            var b = profile.BaiduOptions!;
            if (!Enum.IsDefined(b.Mode) || !Uri.TryCreate(b.BaseUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
                || b.BaseUrl.Contains('?') || b.BaseUrl.Contains('#')) return InvalidOcr();
        }
        return null;
    }

    private static ErrorInfo? ValidateOcrPlan(SettingsEditPlan plan, AppSettings current)
    {
        if (plan.Screenshot?.MaskOpacity is { } mask && (!double.IsFinite(mask.Target) || mask.Target is < 0 or > 0.9)) return InvalidOcr();
        if (!HasOcrEdits(plan)) return null;
        if (plan.Ocr?.ActiveProviderId is { } active && string.IsNullOrWhiteSpace(active.Target)) return InvalidOcr();
        if (plan.Ocr?.TextLayout is { } layout && !Enum.IsDefined(layout.Target)) return InvalidOcr();
        foreach (var change in plan.OcrChanges)
        {
            if (change.TargetProfile is { } target && ValidateOcrProfile(target) is { } error) return error;
        }
        var candidate = BuildOcr(plan, current.Ocr, null, checkConflicts: false);
        return OcrReferencesValid(candidate) ? null : InvalidOcr();
    }

    private static bool OcrReferencesValid(OcrSettings settings) =>
        settings.Providers.Any(p => p.Id == settings.ActiveProviderId) && (settings.FallbackProviderId == null
        || settings.FallbackProviderId != settings.ActiveProviderId && settings.Providers.Any(p => p.Id == settings.FallbackProviderId));

    private static bool FieldConflict<T>(SettingsFieldEdit<T>? edit, T current) => edit != null
        && !EqualityComparer<T>.Default.Equals(current, edit.Base) && !EqualityComparer<T>.Default.Equals(current, edit.Target);

    private static bool OcrFieldsConflict(OcrSettingsPatch? p, OcrSettings c) => p != null &&
        (FieldConflict(p.ActiveProviderId, c.ActiveProviderId) || FieldConflict(p.FallbackProviderId, c.FallbackProviderId)
        || FieldConflict(p.TextLayout, c.TextLayout) || FieldConflict(p.LanguageHint, c.LanguageHint)
        || FieldConflict(p.AutoCopyOnOcr, c.AutoCopyOnOcr) || FieldConflict(p.SilentOcrShowNotification, c.SilentOcrShowNotification));

    private static OcrSettings BuildOcr(SettingsEditPlan plan, OcrSettings current,
        Dictionary<CredentialTarget, string?>? credentials, bool checkConflicts)
    {
        if (!HasOcrEdits(plan)) return current;
        if (checkConflicts && OcrFieldsConflict(plan.Ocr, current)) throw OcrConflict();
        var profiles = current.Providers.ToList();
        foreach (var change in plan.OcrChanges)
        {
            var id = (change.BaseProfile ?? change.TargetProfile)!.Id;
            int index = profiles.FindIndex(p => p.Id == id);
            if (checkConflicts && (change.Kind == OcrProfileChangeKind.Add
                ? profiles.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                : index < 0 || profiles[index] != change.BaseProfile)) throw OcrConflict();
            if (change.Kind == OcrProfileChangeKind.Delete)
            {
                if (index >= 0) profiles.RemoveAt(index);
                continue;
            }
            var target = change.TargetProfile!;
            if (credentials != null && credentials.TryGetValue(new(true, id), out var set)) target = target with { CredentialSetId = set };
            if (change.Kind == OcrProfileChangeKind.Add) profiles.Add(target);
            else if (index >= 0) profiles[index] = target;
        }
        var patch = plan.Ocr;
        var result = current with
        {
            Providers = profiles.SequenceEqual(current.Providers) ? current.Providers : profiles.ToArray(),
            ActiveProviderId = patch?.ActiveProviderId?.Target ?? current.ActiveProviderId,
            FallbackProviderId = patch?.FallbackProviderId is { } fallback ? fallback.Target : current.FallbackProviderId,
            TextLayout = patch?.TextLayout?.Target ?? current.TextLayout,
            LanguageHint = patch?.LanguageHint?.Target ?? current.LanguageHint,
            AutoCopyOnOcr = patch?.AutoCopyOnOcr?.Target ?? current.AutoCopyOnOcr,
            SilentOcrShowNotification = patch?.SilentOcrShowNotification?.Target ?? current.SilentOcrShowNotification
        };
        return result == current ? current : result;
    }

    private static StagingConflictException OcrConflict() => new(new(ErrorCode.Unknown, ConflictMessage));

    private static OcrSettings ApplyOcr(SettingsEditPlan plan, OcrSettings current, Dictionary<CredentialTarget, string?>? credentials)
    {
        if (!HasOcrEdits(plan)) return current;
        var result = BuildOcr(plan, current, credentials, checkConflicts: true);
        if (!OcrReferencesValid(result)) throw OcrConflict();
        return result;
    }

    private static ScreenshotSettings ApplyScreenshot(ScreenshotSettingsPatch? p, ScreenshotSettings c)
    {
        if (!HasScreenshotEdits(p)) return c;
        if (FieldConflict(p!.ConfirmOnMouseUp, c.ConfirmOnMouseUp) || FieldConflict(p.ShowMagnifier, c.ShowMagnifier)
            || FieldConflict(p.HideIrisWindows, c.HideIrisWindows) || FieldConflict(p.CopyImageToClipboard, c.CopyImageToClipboard)
            || FieldConflict(p.MaskOpacity, c.MaskOpacity)) throw OcrConflict();
        var result = c with
        {
            ConfirmOnMouseUp = p.ConfirmOnMouseUp?.Target ?? c.ConfirmOnMouseUp,
            ShowMagnifier = p.ShowMagnifier?.Target ?? c.ShowMagnifier,
            HideIrisWindows = p.HideIrisWindows?.Target ?? c.HideIrisWindows,
            CopyImageToClipboard = p.CopyImageToClipboard?.Target ?? c.CopyImageToClipboard,
            MaskOpacity = p.MaskOpacity?.Target ?? c.MaskOpacity
        };
        return result == c ? c : result;
    }

    private static StagingPlan PrepareOcrStaging(SettingsEditPlan plan, AppSettings current, StagingPlan staging)
    {
        try { _ = ApplyOcr(plan, current.Ocr, null); _ = ApplyScreenshot(plan.Screenshot, current.Screenshot); }
        catch (StagingConflictException error) { return StagingPlan.Conflict(error.ErrorInfo); }
        foreach (var change in plan.OcrChanges)
        {
            var previous = change.BaseProfile;
            if (change.Kind != OcrProfileChangeKind.Delete)
            {
                var key = new CredentialTarget(true, change.TargetProfile!.Id);
                if (change.Kind == OcrProfileChangeKind.Update && change.Credentials.Kind == CredentialEditKind.Keep)
                    staging.NewCredentialSetMap[key] = previous!.CredentialSetId;
                else
                {
                    // Clear/new Keep publish the existing protocol's explicit empty set;
                    // a local profile never stages a nonempty credential pair.
                    var set = "cs-" + Guid.NewGuid().ToString("N");
                    var values = change.Credentials.Replacement;
                    staging.CredentialsToStage.Add((set, values?.ApiKey, values?.SecretKey));
                    staging.NewCredentialSetMap[key] = set;
                }
            }
            if (previous != null && (change.Kind == OcrProfileChangeKind.Delete || change.Credentials.Kind != CredentialEditKind.Keep))
            {
                if (previous.CredentialSetId == null) staging.CandidateLegacyProfileIds.Add(previous.Id);
                else if (CredentialSetIdRegex().IsMatch(previous.CredentialSetId)) staging.CandidateSetIds.Add(previous.CredentialSetId);
            }
        }
        return staging;
    }
}
