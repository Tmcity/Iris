using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;

internal static class RuntimeChecks
{
    internal static async Task Run()
    {
        await CallbackCredentialsCrossing();
        await CredentialRotationAndPools();
        await ThrowingProxyFallsBack();
    }
    private static async Task CallbackCredentialsCrossing()
    {
        string current = "A"; var events = new ConcurrentQueue<string>();
        var credentials = new CredentialLookup(() => current, events);
        var proxy = new CallbackProxy(new("http://proxy-a.example.test:8080"), credentials, events) { AfterSelect = () => current = "B" };
        var wire = new ProxyWire(); using var handler = wire.Handler(proxy); using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.SendAsync(Request(), HttpCompletionOption.ResponseHeadersRead);
        Check.That(wire.Endpoints.Single() == "proxy-a.example.test:8080", "actual runtime selected A endpoint");
        Check.That(wire.AuthorizationLabels.Single() == "B", "actual runtime sent current B credentials to selected A");
        Check.That(events.Any(e => e == "credential:proxy-a.example.test:Basic:B"), "actual GetCredential uses selected proxy URI");
        Console.WriteLine("COUNTEREXAMPLE runtime mutable callback selectedProxy=A sentCredential=B actualSocketsHttpHandler=true socketCount=0");
        Console.WriteLine("CALLS " + string.Join(" -> ", events));
    }
    private static async Task CredentialRotationAndPools()
    {
        string value = "A"; var events = new ConcurrentQueue<string>();
        var proxy = new CallbackProxy(new("http://proxy-a.example.test:8080"), new CredentialLookup(() => "A", events), events);
        var wire = new ProxyWire(); using var handler = wire.Handler(proxy); using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using (await client.SendAsync(Request(), HttpCompletionOption.ResponseHeadersRead)) { }
        value = "C"; proxy.CredentialsValue = new CredentialLookup(() => value, events);
        using (await client.SendAsync(Request(), HttpCompletionOption.ResponseHeadersRead)) { }
        Check.That(wire.Connects == 1, "actual runtime should reuse HTTP connection");
        Check.That(wire.AuthorizationLabels.Count == 2 && wire.AuthorizationLabels.All(x => x == "A"), "cached credentials persist on handler despite getter replacement");
        Console.WriteLine($"COUNTEREXAMPLE runtime sameProxyCredentialRotation desired=C actual=A connections={wire.Connects} credentialsGetterCalls={proxy.CredentialReads}");

        using var newHandler = wire.Handler(new CallbackProxy(proxy.Address, new CredentialLookup(() => "C", events), events));
        using var newClient = new HttpClient(newHandler) { Timeout = Timeout.InfiniteTimeSpan };
        using (await newClient.SendAsync(Request(), HttpCompletionOption.ResponseHeadersRead)) { }
        Check.That(wire.Connects == 2 && wire.AuthorizationLabels.Last() == "C", "new immutable handler creates separate credential/pool generation");
        Check.Pass("runtime immutable handler generation same proxy URI rotates credentials and connection pool; actual ConnectCallback streams, no sockets");
    }
    private static async Task ThrowingProxyFallsBack()
    {
        var events = new ConcurrentQueue<string>();
        var proxy = new CallbackProxy(new("http://proxy-a.example.test:8080"), null, events) { ThrowGet = true };
        var wire = new ProxyWire(requireAuth: false); using var handler = wire.Handler(proxy); using var client = new HttpClient(handler);
        using (await client.SendAsync(Request(), HttpCompletionOption.ResponseHeadersRead)) { }
        Check.That(wire.Endpoints.Single() == "service.example.test:80", "runtime swallows GetProxy exception and chooses direct");
        Console.WriteLine("COUNTEREXAMPLE runtime GetProxy exception swallowed directConnectSelected=true socketCount=0");
    }
    internal static HttpRequestMessage Request() => new(HttpMethod.Get, "http://service.example.test/probe") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
}

internal sealed class CallbackProxy(Uri address, ICredentials? credentials, ConcurrentQueue<string> events) : IWebProxy
{
    internal Uri Address = address;
    internal ICredentials? CredentialsValue = credentials;
    internal Action? AfterSelect;
    internal bool ThrowGet;
    internal int CredentialReads;
    public ICredentials? Credentials { get { CredentialReads++; events.Enqueue("Credentials"); return CredentialsValue; } set => CredentialsValue = value; }
    public bool IsBypassed(Uri host) { events.Enqueue("IsBypassed"); return false; }
    public Uri? GetProxy(Uri destination)
    {
        events.Enqueue("GetProxy"); if (ThrowGet) throw new InvalidOperationException("synthetic proxy resolution failure");
        var selected = Address; AfterSelect?.Invoke(); return selected;
    }
}
internal sealed class CredentialLookup(Func<string> label, ConcurrentQueue<string> events, Uri? allowed = null) : ICredentials
{
    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        if (allowed != null && uri != allowed) return null;
        string current = label(); events.Enqueue($"credential:{uri.Host}:{authType}:{current}");
        return new("synthetic-" + current, "synthetic-secret");
    }
}
internal sealed class ProxyWire(bool requireAuth = true, string? firstChunk = null)
{
    internal int Connects;
    internal ConcurrentQueue<string> Endpoints = new(), AuthorizationLabels = new();
    internal ConcurrentQueue<string> Methods = new();
    internal bool OriginAuthorizationSeen;
    internal ConcurrentQueue<ScriptedStream> Streams = new();
    internal SocketsHttpHandler Handler(IWebProxy? proxy) => new()
    {
        UseProxy = proxy != null, Proxy = proxy, AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = (context, ct) =>
        {
            Interlocked.Increment(ref Connects); Endpoints.Enqueue(context.DnsEndPoint.Host + ":" + context.DnsEndPoint.Port);
            var stream = new ScriptedStream(headers =>
            {
                Methods.Enqueue(headers.Split(' ')[0]);
                OriginAuthorizationSeen |= headers.Split("\r\n").Any(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("x-api-key:", StringComparison.OrdinalIgnoreCase));
                string? auth = headers.Split("\r\n").FirstOrDefault(h => h.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                if (requireAuth && auth == null) return "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"synthetic\"\r\nContent-Length: 0\r\n\r\n";
                if (auth != null)
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[(auth.LastIndexOf(' ') + 1)..]));
                    string user = decoded.Split(':')[0]; AuthorizationLabels.Enqueue(user.Replace("synthetic-", "", StringComparison.Ordinal));
                }
                return firstChunk == null ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n" + ScriptedStream.Chunk(firstChunk);
            });
            Streams.Enqueue(stream); return ValueTask.FromResult<Stream>(stream);
        }
    };
}
internal sealed class ScriptedStream(Func<string, string> respond) : Stream
{
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
    private readonly StringBuilder _request = new();
    private byte[]? _current;
    private int _offset;
    private Exception? _readFailure;
    internal TaskCompletionSource ReadWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static string Chunk(string data) => Encoding.ASCII.GetByteCount(data).ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\r\n" + data + "\r\n";
    internal void EmitChunk(string data) => _responses.Writer.TryWrite(Encoding.ASCII.GetBytes(Chunk(data)));
    internal void EndChunks() => _responses.Writer.TryWrite("0\r\n\r\n"u8.ToArray());
    internal void FailRead() { _readFailure = new IOException("Synthetic wire read failure"); _responses.Writer.TryComplete(_readFailure); }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Prototype uses async transport only");
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current == null || _offset == _current.Length)
        {
            try
            {
                var read = _responses.Reader.ReadAsync(cancellationToken);
                if (!read.IsCompleted) ReadWaiting.TrySetResult();
                _current = await read; _offset = 0;
            }
            catch (ChannelClosedException) { if (_readFailure != null) throw _readFailure; return 0; }
        }
        int count = Math.Min(buffer.Length, _current.Length - _offset); _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        _request.Append(Encoding.ASCII.GetString(buffer));
        while (_request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end && end >= 0)
        {
            string request = _request.ToString(0, end + 4); _request.Remove(0, end + 4);
            _responses.Writer.TryWrite(Encoding.ASCII.GetBytes(respond(request)));
        }
    }
    public override void Write(byte[] buffer, int offset, int count) => WriteCore(buffer.AsSpan(offset, count));
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { WriteCore(buffer.Span); return ValueTask.CompletedTask; }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { WriteCore(buffer.AsSpan(offset, count)); return Task.CompletedTask; }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected override void Dispose(bool disposing) { if (disposing) _responses.Writer.TryComplete(); base.Dispose(disposing); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
