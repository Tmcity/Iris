using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Iris.Core.Actions;
using Windows.Win32;

namespace Iris.Platform.Windows.SystemIntegration;

internal sealed record IpcIdentity(string PipeName, string MutexName)
{
    internal static IpcIdentity ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw IpcProtocol.Unavailable();
        return new(IpcConstants.PipeNamePrefix + sid, IpcConstants.MutexNamePrefix + sid);
    }
    public override string ToString() => "IpcIdentity";
}

/// <summary>Owns the real stream; narrow virtual I/O boundaries permit deterministic cleanup fault tests.</summary>
internal class IpcPipe(PipeStream stream) : IAsyncDisposable
{
    internal virtual Stream Stream => stream;
    internal virtual Task ConnectAsync(CancellationToken token) => ((NamedPipeClientStream)stream).ConnectAsync(token);
    internal virtual Task AcceptAsync(CancellationToken token) => ((NamedPipeServerStream)stream).WaitForConnectionAsync(token);
    internal virtual void VerifyPeer(bool server)
    {
        uint session;
        bool valid = server ? PInvoke.GetNamedPipeClientSessionId(stream.SafePipeHandle, out session)
            : PInvoke.GetNamedPipeServerSessionId(stream.SafePipeHandle, out session);
        using var process = Process.GetCurrentProcess();
        if (!valid || session != (uint)process.SessionId) throw IpcProtocol.Unavailable();
    }
    internal virtual void Disconnect() => ((NamedPipeServerStream)stream).Disconnect();
    public virtual ValueTask DisposeAsync() => stream.DisposeAsync();
}

internal class IpcPipeFactory
{
    internal virtual IpcPipe CreateServer(IpcIdentity identity) => new(new NamedPipeServerStream(identity.PipeName,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
        inBufferSize: 4096, outBufferSize: IpcProtocol.MaxReplyBytes));
    internal virtual IpcPipe CreateClient(IpcIdentity identity) => new(new NamedPipeClientStream(".", identity.PipeName,
        PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification));
}
