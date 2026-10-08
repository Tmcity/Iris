using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Platform.Windows.SystemIntegration;

namespace Iris.App.Hosting;

internal enum CommandLineKind { Run, Help, Invalid }
internal sealed record CommandLineRequest(CommandLineKind Kind, IpcCommand? Command = null)
{
    public override string ToString() => $"CommandLineRequest {Kind}";
}
internal static class CommandLineParser
{
    internal static CommandLineRequest Parse(string[] args)
    {
        if (args.Length == 0) return new(CommandLineKind.Run);
        if (args.Length == 1 && args[0] is "--help" or "-h") return new(CommandLineKind.Help);
        if (args.Length != 2) return new(CommandLineKind.Invalid);
        IpcCommand command;
        if (args[0] == "--action" && IpcProtocol.TryAction(args[1], out var action) && action != IrisAction.TranslateText) command = new(action);
        else if (args[0] == "--translate" && !string.IsNullOrWhiteSpace(args[1])) command = new(IrisAction.TranslateText, args[1]);
        else return new(CommandLineKind.Invalid);
        try { var encoded = IpcProtocol.Encode(command); Array.Clear(encoded); }
        catch (IrisException) { return new(CommandLineKind.Invalid); }
        return new(CommandLineKind.Run, command);
    }
}
