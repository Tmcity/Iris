using System.Collections.ObjectModel;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Settings;

namespace Iris.Core.Ocr;

internal sealed class OcrRegistryException(string stage, string? detail = null)
    : IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown), detail: $"stage={stage};{detail}");

// Core cannot depend on Infrastructure.SettingsSnapshot. Apply the same collection-freezing
// rules here, without normalization or I/O; scalar nested records are immutable.
internal static class OcrRegistryFreeze
{
    internal static OcrProviderProfile Profile(OcrProviderProfile profile) => profile with
    {
        BaiduOptions = profile.BaiduOptions is { } b ? b with { } : null,
        WindowsOptions = profile.WindowsOptions is { } w ? w with { } : null
    };
    internal static AppSettings Settings(AppSettings value) => value with
    {
        Hotkeys = new ReadOnlyDictionary<Actions.IrisAction, HotkeyBinding>(value.Hotkeys.ToDictionary(p => p.Key, p => p.Value)),
        Translation = value.Translation with
        {
            Providers = Array.AsReadOnly(value.Translation.Providers.Select(p => p.OpenAIOptions is { } o ? p with
            {
                OpenAIOptions = o with { ExtraHeaders = o.ExtraHeaders == null ? null : new ReadOnlyDictionary<string, string>(o.ExtraHeaders.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)) }
            } : p).ToArray()),
            CustomPrompts = Array.AsReadOnly(value.Translation.CustomPrompts.ToArray())
        },
        Ocr = value.Ocr with { Providers = Array.AsReadOnly(value.Ocr.Providers.Select(Profile).ToArray()) },
        Selection = value.Selection with
        {
            ReadOrder = Array.AsReadOnly(value.Selection.ReadOrder.ToArray()),
            ExcludedProcesses = Array.AsReadOnly(value.Selection.ExcludedProcesses.ToArray()),
            ClipboardUnsafeProcesses = Array.AsReadOnly(value.Selection.ClipboardUnsafeProcesses.ToArray())
        }
    };
}

internal sealed class UnavailableOcrProvider : IOcrProvider
{
    public ProviderDescriptor Descriptor { get; }
    public ImageConstraints Constraints { get; } = new(285212672, false, 32768, 40, Array.AsReadOnly(new[] { ImageFormat.Png, ImageFormat.Bmp }));
    internal UnavailableOcrProvider(OcrProviderProfile profile) => Descriptor = new(profile.Id,
        string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName, profile.Kind.ToString(), null, false);
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new IrisException(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured), providerId: Descriptor.Id);
    }
    public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProviderTestResult(false, TimeSpan.Zero, Error: new(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured), ProviderId: Descriptor.Id)));
    }
}

internal sealed class OcrProviderSnapshot : IOcrProviderSnapshot
{
    private readonly object _gate = new();
    private OcrProviderRegistry? _owner;
    private AppSettings? _settings;
    private IReadOnlyList<OcrProviderRegistration>? _providers;
    private OcrProviderRegistry.Instance[]? _instances;
    private bool _released;
    private Task? _release;
    internal OcrProviderSnapshot(OcrProviderRegistry owner, AppSettings settings, OcrProviderRegistry.Instance[] instances, IReadOnlyList<OcrProviderRegistration> providers)
    { _owner = owner; _settings = settings; _instances = instances; _providers = providers; }
    public AppSettings Settings { get { lock (_gate) { ObjectDisposedException.ThrowIf(_released, this); return _settings!; } } }
    public IReadOnlyList<OcrProviderRegistration> Providers { get { lock (_gate) { ObjectDisposedException.ThrowIf(_released, this); return _providers!; } } }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_release is { IsCompleted: false }) return new(_release);
            if (_owner == null) return ValueTask.CompletedTask;
            if (!_released)
            {
                _released = true; _settings = null; _providers = null;
                _instances = _owner.ReleaseReferences(_instances!);
            }
            // Publication references do not make this lease wait for future retirement.
            if (_instances!.Length == 0) { _instances = null; _owner = null; return ValueTask.CompletedTask; }
            return new(_release = ReleaseAsync(_owner, _instances));
        }
    }
    private async Task ReleaseAsync(OcrProviderRegistry owner, OcrProviderRegistry.Instance[] instances)
    {
        await owner.CleanupInstancesAsync(instances).ConfigureAwait(false);
        lock (_gate) { _instances = null; _owner = null; }
    }
}
