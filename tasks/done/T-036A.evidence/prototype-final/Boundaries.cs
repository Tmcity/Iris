using System.Collections.Concurrent;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Microsoft.Extensions.Logging;

internal sealed class MemorySettings : ISettingsService
{
    internal AppSettings Value = new();
    internal Func<AppSettings>? OnRead;
    public AppSettings Current => OnRead?.Invoke() ?? Value;
    public event EventHandler<SettingsChangedEventArgs>? Changed;
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); var old = Value; Value = mutate(old); Changed?.Invoke(this, new(old, Value)); return Task.CompletedTask; }
}
internal sealed class MemorySecrets : ISecretStore
{
    internal ConcurrentDictionary<string, string> Values = new();
    internal bool FailSet = false, FailRemove = false;
    internal int Sets, Removes;
    public event EventHandler<string>? Changed;
    public string? Get(string key) => Values.GetValueOrDefault(key);
    public void Set(string key, string value)
    { if (FailSet) throw new IOException("synthetic stage failure"); Values[key] = value; Sets++; Changed?.Invoke(this, key); }
    public bool Remove(string key)
    { if (FailRemove) throw new IOException("synthetic cleanup failure"); Removes++; return Values.TryRemove(key, out _); }
}
internal sealed class TestPaths(string directory) : IAppPaths
{
    public bool IsPortable => true;
    public string DataDirectory => directory;
    public string SettingsFile => Path.Combine(directory, "settings.json");
    public string SecretsFile => Path.Combine(directory, "secrets.dat");
    public string DatabaseFile => Path.Combine(directory, "history.db");
    public string LogDirectory => Path.Combine(directory, "logs");
}
internal sealed class TempArea : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "owned-data", Guid.NewGuid().ToString("N"));
    internal TestPaths Paths { get; }
    internal TempArea() { Directory.CreateDirectory(DirectoryPath); Paths = new(DirectoryPath); }
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
internal sealed class Gate
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Pause() { Entered.TrySetResult(); return _release.Task; }
    internal void Open() => _release.TrySetResult();
}
internal sealed class LogCapture : ILoggerProvider
{
    internal ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }
    private sealed class Logger(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => owner.Messages.Enqueue(formatter(state, error));
    }
}
