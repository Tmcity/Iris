using System.ComponentModel;
using System.Globalization;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed record SelectionOrderOption(int Id, string Label);

internal sealed class SelectionSettingsDraftViewModel : INotifyPropertyChanged
{
    private readonly Func<bool> _canEdit;
    private readonly Action _changed;
    private IReadOnlyList<SelectionReadMethod>? _baseOrder;
    private IReadOnlyList<string>? _baseProcesses;
    private bool _baseRestore, _restore, _baseInput, _input;
    private int _baseTimeout;
    private int? _order;
    private string _timeout = "", _processes = "", _baseProcessText = "";
    private bool _orderDirty, _processesDirty;
    private string? _error;
    public bool IsFrozen { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public static IReadOnlyList<SelectionOrderOption> Orders { get; } =
    [
        new(0, UiText.SelectionOrderUiaFirst), new(1, UiText.SelectionOrderClipboardFirst),
        new(2, UiText.SelectionOrderUiaOnly), new(3, UiText.SelectionOrderClipboardOnly)
    ];
    public SelectionSettingsDraftViewModel(SelectionSettings current, Func<bool> canEdit, Action changed)
    {
        _canEdit = canEdit; _changed = changed;
        Merge(current, reset: true, notify: false);
    }
    public int? ReadOrderChoice
    {
        get => _order;
        set
        {
            if (!CanChange || value == _order) return;
            _order = value; _orderDirty = !OrderEqual(_baseOrder, ToOrder(value)); Changed(nameof(ReadOrderChoice));
        }
    }
    public bool RestoreClipboard { get => _restore; set { if (!CanChange || _restore == value) return; _restore = value; Changed(nameof(RestoreClipboard)); } }
    public bool OpenInputWhenEmpty { get => _input; set { if (!CanChange || _input == value) return; _input = value; Changed(nameof(OpenInputWhenEmpty)); } }
    public string ClipboardTimeoutText
    {
        get => _timeout;
        set { if (!CanChange || _timeout == value) return; _timeout = value ?? ""; Changed(nameof(ClipboardTimeoutText)); }
    }
    public string ClipboardUnsafeProcessesText
    {
        get => _processes;
        set
        {
            string next = (value ?? "").ReplaceLineEndings("\n");
            if (!CanChange || _processes == next) return;
            _processes = next;
            _processesDirty = _baseProcesses == null || _baseProcesses.Any(name => name == null) || _processes != _baseProcessText;
            Changed(nameof(ClipboardUnsafeProcessesText));
        }
    }
    public string? ErrorMessage => _error;
    public bool HasError => _error != null;
    public bool IsDirty => _orderDirty || _processesDirty || TimeoutDirty || _restore != _baseRestore || _input != _baseInput;
    private bool TimeoutDirty => _timeout != _baseTimeout.ToString(CultureInfo.InvariantCulture);
    private bool CanChange => !IsFrozen && _canEdit();

    public bool TryCapture(out SelectionSettingsPatch? patch)
    {
        patch = null;
        var order = ToOrder(_order);
        if (_orderDirty && order == null) return Invalid(UiText.SelectionOrderError);
        int timeout = _baseTimeout;
        if (TimeoutDirty && (!int.TryParse(_timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout) || timeout is < 100 or > 2000))
            return Invalid(UiText.SelectionTimeoutError);
        string[] names = _processes.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (_processesDirty && (names.Length > 256 || names.Any(raw =>
        {
            string name = raw.Trim();
            return name.Length is < 1 or > 255 || name is "." or ".." || name.EndsWith('.')
                || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c));
        }))) return Invalid(UiText.SelectionProcessesError);
        SetError(null);
        if (!IsDirty) return true;
        patch = new(
            _orderDirty ? new(_baseOrder?.ToArray()!, order!) : null,
            _restore != _baseRestore ? new(_baseRestore, _restore) : null,
            TimeoutDirty ? new(_baseTimeout, timeout) : null,
            _processesDirty ? new(_baseProcesses?.ToArray()!, names) : null,
            _input != _baseInput ? new(_baseInput, _input) : null);
        return true;
    }

    public void Merge(SelectionSettings current, bool reset = false, bool notify = true)
    {
        if (reset || !_orderDirty) { _baseOrder = current.ReadOrder?.ToArray(); _order = FromOrder(_baseOrder); _orderDirty = false; }
        if (reset || !_processesDirty)
        {
            _baseProcesses = current.ClipboardUnsafeProcesses?.ToArray();
            _processes = _baseProcessText = string.Join('\n', _baseProcesses ?? []).ReplaceLineEndings("\n");
            _processesDirty = false;
        }
        if (reset || !TimeoutDirty) { _baseTimeout = current.ClipboardTimeoutMs; _timeout = _baseTimeout.ToString(CultureInfo.InvariantCulture); }
        if (reset || _restore == _baseRestore) _restore = _baseRestore = current.RestoreClipboard;
        if (reset || _input == _baseInput) _input = _baseInput = current.OpenInputWhenEmpty;
        if (reset) _error = null;
        if (notify) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); _changed(); }
    }
    private bool Invalid(string message) { SetError(message); return false; }
    private void SetError(string? message)
    { _error = message; PropertyChanged?.Invoke(this, new(nameof(ErrorMessage))); PropertyChanged?.Invoke(this, new(nameof(HasError))); }
    private void Changed(string property)
    { SetError(null); PropertyChanged?.Invoke(this, new(property)); PropertyChanged?.Invoke(this, new(nameof(IsDirty))); _changed(); }
    private static SelectionReadMethod[]? ToOrder(int? choice) => choice switch
    {
        0 => [SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard],
        1 => [SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation],
        2 => [SelectionReadMethod.UIAutomation], 3 => [SelectionReadMethod.Clipboard], _ => null
    };
    private static int? FromOrder(IReadOnlyList<SelectionReadMethod>? order)
    {
        for (int i = 0; i < 4; i++) if (OrderEqual(order, ToOrder(i))) return i;
        return null;
    }
    private static bool OrderEqual(IReadOnlyList<SelectionReadMethod>? a, IReadOnlyList<SelectionReadMethod>? b) =>
        ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b));
    public override string ToString() => "SelectionSettingsDraft";
}
