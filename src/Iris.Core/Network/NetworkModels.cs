namespace Iris.Core.Network;

using Iris.Core.Errors;
using Iris.Core.Settings;

public sealed record ProxyDraft(
    NetworkSettings BaseSettings,
    NetworkSettings TargetSettings,
    ProxyCredentialEdit? Credentials = null)
{
    public override string ToString() => "ProxyDraft";
}

public enum ProxyRouteKind
{
    Proxy = 0,
    DirectByMode = 1,
    DirectByLocalBypass = 2,
    DirectBySystemBypass = 3
}

// 纯托管值，没有 IDisposable/租约；Destination 可能带请求查询串，禁止序列化/日志输出。
// Mode 为本次目标的有效模式；None 可以由显式 None 或本地绕过产生。
// System 的实际代理 URI/默认凭据由 Providers 在进入 SocketsHttpHandler 前解析并冻结。
public sealed record NetworkProxySnapshot(
    Guid GenerationId,
    Uri Destination,
    ProxyMode Mode,
    Uri? CustomProxyUri,
    ProxyCredentials? Credentials,
    ProxyRouteKind RouteKind)
{
    public override string ToString() => "NetworkProxySnapshot";
}

// 与 SettingsEditCoordinator 是同一实例/同一短编辑门闩；不注入回协调器自身。
public interface INetworkProxySnapshotSource
{
    Task<NetworkProxySnapshot> CaptureCurrentAsync(
        Uri destination, CancellationToken cancellationToken = default);

    Task<NetworkProxySnapshot> CaptureDraftAsync(
        ProxyDraft draft, Uri destination, CancellationToken cancellationToken = default);
}

// 接收协调器已经固定的配置身份，不反向调用 Capture/Commit，避免 DI 环和门闩自等待。
// 不以连通成功作为应用成功条件；这里只发布/准备传输代次及退休规则。
public interface INetworkConfigurationObserver
{
    Task ApplyAsync(NetworkSettings settings, Guid generationId,
        CancellationToken cancellationToken = default);
}

// 所有实际传输资源归 Providers，纯快照不承担该接口。
public interface INetworkTransportLifetime
{
    void StopAcceptance();
    Task StopAsync();
}

public enum NetworkEndpointKind { Translation = 1, Ocr = 2 }

public sealed record NetworkEndpointSelection(NetworkEndpointKind Kind, string ProviderId)
{
    public override string ToString() => "NetworkEndpointSelection";
}

public enum ProxyConnectivityStatus
{
    EndpointResponded = 1,
    RedirectNotFollowed = 2,
    ProxyAuthenticationRequired = 3,
    TimedOut = 4,
    TransportFailed = 5,
    InvalidConfiguration = 6
}

public sealed record ProxyConnectivityResult(
    Guid SessionId,
    Guid SnapshotGenerationId,
    ProxyConnectivityStatus Status,
    ProxyRouteKind RouteKind,
    TimeSpan Elapsed,
    int? HttpStatusCode = null,
    ErrorInfo? Error = null)
{
    public override string ToString() => $"ProxyConnectivityResult {Status}";
}

public interface IProxyConnectivityTester
{
    // 同步复制不可变输入/分配会话身份，不访问设置、SecretStore、Provider 或 HTTP。
    IProxyConnectivityTestSession Create(ProxyDraft draft, NetworkEndpointSelection endpoint);
}

public interface IProxyConnectivityTestSession : IAsyncDisposable
{
    Guid Id { get; }
    // 一次逻辑 HEAD，不配置业务重试；底层认证挑战可能有多次线路交换。
    // caller 取消抛 OCE；内部 10 秒软期限返回 TimedOut，但始终等真实 Send 退出。
    Task<ProxyConnectivityResult> RunAsync(CancellationToken cancellationToken = default);
    // DisposeAsync 关闭准入/请求取消，等待 Run、响应及传输清理；失败保有原资源，原实例重试，不重发 HEAD。
}
