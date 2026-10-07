using System.ComponentModel;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed class DraftValue<T>(T initial, Func<bool> canEdit, Action changed) : INotifyPropertyChanged
{
    private T _value = initial;
    public T Base { get; private set; } = initial;
    public T Value
    {
        get => _value;
        set
        {
            if (!canEdit() || EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value; Notify(); changed();
        }
    }
    public bool IsDirty => !EqualityComparer<T>.Default.Equals(Base, _value);
    public SettingsFieldEdit<T>? Edit() => IsDirty ? new(Base, _value) : null;
    public void Merge(T current, bool reset = false)
    {
        if (!reset && IsDirty) return;
        Base = _value = current; Notify();
    }
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(nameof(Value)));
        PropertyChanged?.Invoke(this, new(nameof(IsDirty)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => "DraftValue";
}
