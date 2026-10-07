using Iris.Core.Network;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Providers.Http;

/// <summary>Bounded ownership of real transport handlers, logical sends and every response content.</summary>
internal sealed partial class NetworkTransportManager : INetworkTransportLifetime, IDisposable
{
    private readonly Lock _gate = new();
    private readonly NetworkTransportFactory _factory;
    private readonly NetworkRouteResolver _resolver;
    private readonly ILogger<NetworkTransportManager> _logger;
    private readonly Dictionary<Guid, Generation> _generations = [];
    private readonly HashSet<NetworkResponseContent> _contents = [];
    private Guid _expectedGeneration;
    private long _useCounter;
    private bool _accepting = true;
    private int _admissions;
    private TaskCompletionSource _changed = NewSignal();
    private Task? _stopTask;

    public NetworkTransportManager(NetworkTransportFactory factory, NetworkRouteResolver resolver, ILogger<NetworkTransportManager> logger)
    { _factory = factory; _resolver = resolver; _logger = logger; }

    internal Guid ExpectedGeneration { get { lock (_gate) return _expectedGeneration; } }
    internal int GenerationCount { get { lock (_gate) return _generations.Count; } }
    internal int HandlerCount { get { lock (_gate) return _generations.Values.Sum(g => g.Entries.Count); } }
    internal int ContentCount { get { lock (_gate) return _contents.Count; } }

    internal void Apply(NetworkSettings settings, Guid generation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            // Only the serialized Core observer can publish. Acquire never writes this field.
            _expectedGeneration = generation;
            bool retired = RetireIdleOldEntries();
            EnsureGeneration(generation);
            Signal();
            if (!retired) throw NetworkProxyCodec.Unavailable();
        }
    }

    internal Admission BeginRequest()
    {
        lock (_gate)
        {
            if (!_accepting) throw NetworkProxyCodec.Unavailable();
            _admissions++;
            return new Admission(this);
        }
    }

    internal Lease Acquire(string family, NetworkProxySnapshot snapshot, Admission? admission = null)
    {
        lock (_gate) { if (!_accepting && admission?.Owner != this) throw NetworkProxyCodec.Unavailable(); }
        // System callbacks can fail or block. They never run under the manager lock or inside Sockets.
        var route = _resolver.Resolve(snapshot);
        lock (_gate)
        {
            if (!_accepting && admission?.Owner != this) throw NetworkProxyCodec.Unavailable();
            RetireIdleOldEntries();
            var generation = EnsureGeneration(snapshot.GenerationId);
            var key = (family, route.Identity);
            if (!generation.Entries.TryGetValue(key, out var entry) || entry.Retired)
            {
                if (entry != null) throw NetworkProxyCodec.Unavailable();
                var familyEntries = generation.Entries.Values.Where(e => e.Family == family).ToArray();
                if (familyEntries.Length >= 16)
                {
                    var victim = familyEntries.Where(e => e.References == 0).OrderBy(e => e.LastUse).FirstOrDefault();
                    if (victim == null || !DisposeEntry(victim)) throw NetworkProxyCodec.Unavailable();
                }
                HttpMessageHandler handler;
                try { handler = _factory.Create(route); }
                catch { throw NetworkProxyCodec.Unavailable(); }
                entry = new Entry(snapshot.GenerationId, family, route.Identity, handler);
                generation.Entries.Add(key, entry);
            }
            entry.References++;
            entry.LastUse = ++_useCounter;
            return new Lease(this, entry);
        }
    }

    private Generation EnsureGeneration(Guid id)
    {
        if (_generations.TryGetValue(id, out var generation)) return generation;
        if (_generations.Count >= 8)
        {
            foreach (var old in _generations.Where(p => p.Key != _expectedGeneration)
                         .OrderBy(p => p.Value.Entries.Values.Select(e => e.LastUse).DefaultIfEmpty(0).Max()).ToArray())
            {
                foreach (var entry in old.Value.Entries.Values.Where(e => e.References == 0).ToArray()) DisposeEntry(entry);
                if (old.Value.Entries.Count == 0) _generations.Remove(old.Key);
                if (_generations.Count < 8) break;
            }
            if (_generations.Count >= 8) throw NetworkProxyCodec.Unavailable();
        }
        generation = new Generation();
        _generations.Add(id, generation);
        return generation;
    }

    private bool RetireIdleOldEntries()
    {
        bool complete = true;
        foreach (var pair in _generations.ToArray())
        {
            foreach (var entry in pair.Value.Entries.Values.Where(e => e.References == 0 && (e.Retired || pair.Key != _expectedGeneration)).ToArray())
                if (!DisposeEntry(entry)) complete = false;
            if (pair.Value.Entries.Count == 0 && pair.Key != _expectedGeneration) _generations.Remove(pair.Key);
        }
        return complete;
    }

    private bool DisposeEntry(Entry entry)
    {
        entry.Retired = true;
        try
        {
            // HttpMessageInvoker uses disposeHandler:false. Retry the actual failed handler, never the invoker's disposed flag.
            entry.Handler.Dispose();
            entry.Invoker.Dispose();
            _generations[entry.Generation].Entries.Remove((entry.Family, entry.Identity));
            return true;
        }
        catch (Exception ex) { LogCleanupFailure(_logger, "Handler", ex.GetType().Name); return false; }
    }

    internal NetworkResponseContent OwnContent(Lease lease, HttpContent content)
    {
        lock (_gate)
        {
            lease.Entry.References++;
            var owned = new NetworkResponseContent(this, lease.Entry, content);
            _contents.Add(owned);
            return owned;
        }
    }

    internal void ContentCompleted(NetworkResponseContent content, Entry entry)
    {
        lock (_gate)
        {
            if (_contents.Remove(content)) Release(entry);
            Signal();
        }
    }

    internal void ContentChanged() { lock (_gate) Signal(); }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0 && (entry.Retired || entry.Generation != _expectedGeneration || !_accepting)) DisposeEntry(entry);
            Signal();
        }
    }

    internal void MarkFailed(Entry entry) { lock (_gate) { entry.Retired = true; Signal(); } }

    public void StopAcceptance() { lock (_gate) { _accepting = false; Signal(); } }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _accepting = false;
            if (_stopTask == null || _stopTask.IsFaulted || _stopTask.IsCanceled) _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        // Keep synchronous StopAsync admission short; resource disposal runs off the calling/UI thread.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        while (true)
        {
            NetworkResponseContent[] retry;
            Task changed;
            lock (_gate) { retry = _contents.Where(c => c.CleanupRequested && c.ActiveOperations == 0).ToArray(); changed = _changed.Task; }
            bool failed = false;
            foreach (var content in retry)
            {
                try { content.Dispose(); }
                catch (Exception ex) { LogCleanupFailure(_logger, "Content", ex.GetType().Name); failed = true; }
            }
            lock (_gate)
            {
                foreach (var generation in _generations.Values.ToArray())
                    foreach (var entry in generation.Entries.Values.Where(e => e.References == 0).ToArray())
                        if (!DisposeEntry(entry)) failed = true;
                foreach (var id in _generations.Where(p => p.Value.Entries.Count == 0).Select(p => p.Key).ToArray()) _generations.Remove(id);
                if (failed) throw NetworkProxyCodec.Unavailable();
                if (_admissions == 0 && _contents.Count == 0 && _generations.Count == 0) return;
            }
            await changed.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        StopAcceptance();
        lock (_gate)
        {
            // Normal Host disposal follows awaited StopAsync. Never force-release a live owner on a fallback path.
            if (_admissions != 0 || _contents.Count != 0 || _generations.Values.Any(g => g.Entries.Values.Any(e => e.References != 0))) throw NetworkProxyCodec.Unavailable();
            foreach (var entry in _generations.Values.SelectMany(g => g.Entries.Values).ToArray())
                if (!DisposeEntry(entry)) throw NetworkProxyCodec.Unavailable();
            _generations.Clear();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var old = _changed; _changed = NewSignal(); old.TrySetResult(); }

    internal sealed class Entry(Guid generation, string family, string identity, HttpMessageHandler handler)
    {
        internal Guid Generation { get; } = generation;
        internal string Family { get; } = family;
        internal string Identity { get; } = identity;
        internal HttpMessageHandler Handler { get; } = handler;
        internal HttpMessageInvoker Invoker { get; } = new(handler, disposeHandler: false);
        internal int References { get; set; }
        internal long LastUse { get; set; }
        internal bool Retired { get; set; }
    }
    private sealed class Generation { internal Dictionary<(string Family, string Identity), Entry> Entries { get; } = []; }
    internal sealed class Admission : IDisposable
    {
        private readonly NetworkTransportManager _owner;
        internal NetworkTransportManager? Owner { get; private set; }
        internal Admission(NetworkTransportManager owner) { _owner = owner; Owner = owner; }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (Owner == null) return;
                Owner = null; _owner._admissions--; _owner.Signal();
            }
        }
    }
    internal sealed class Lease(NetworkTransportManager owner, Entry entry) : IDisposable
    {
        private int _released;
        internal Entry Entry { get; } = entry;
        internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Entry.Invoker.SendAsync(request, cancellationToken);
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(Entry); }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Network {ResourceKind} cleanup incomplete: {ExceptionType}")]
    private static partial void LogCleanupFailure(ILogger logger, string resourceKind, string exceptionType);
}

internal sealed class NetworkConfigurationObserver(NetworkTransportManager manager) : INetworkConfigurationObserver
{
    public Task ApplyAsync(NetworkSettings settings, Guid generationId, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); manager.Apply(settings, generationId); return Task.CompletedTask; }
}
