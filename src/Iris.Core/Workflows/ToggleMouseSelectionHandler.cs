using Iris.Core.Actions;
using Iris.Core.Settings;

namespace Iris.Core.Workflows;

/// <summary>一次原子相对切换；最后启用模式只在本次运行中记忆。</summary>
public sealed class ToggleMouseSelectionHandler : IActionHandler, IDisposable
{
    private readonly ISettingsService _settings;
    private int _lastMode = (int)MouseSelectionMode.ShowIcon;
    public ToggleMouseSelectionHandler(ISettingsService settings)
    {
        _settings = settings;
        Remember(settings.Current.Selection.MouseMode);
        settings.Changed += OnChanged;
    }
    public IrisAction Action => IrisAction.ToggleMouseSelection;
    public Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => _settings.UpdateAsync(current =>
    {
        var mode = current.Selection.MouseMode;
        Remember(mode); // Includes settings loaded before Changed subscriptions observe a non-Off value.
        return current with { Selection = current.Selection with
        {
            MouseMode = mode == MouseSelectionMode.Off ? (MouseSelectionMode)Volatile.Read(ref _lastMode) : MouseSelectionMode.Off,
        } };
    }, cancellationToken);
    private void OnChanged(object? sender, SettingsChangedEventArgs args) => Remember(args.NewSettings.Selection.MouseMode);
    private void Remember(MouseSelectionMode mode)
    {
        if (mode is MouseSelectionMode.ShowIcon or MouseSelectionMode.AutoTranslate) Volatile.Write(ref _lastMode, (int)mode);
    }
    public void Dispose() => _settings.Changed -= OnChanged;
}
