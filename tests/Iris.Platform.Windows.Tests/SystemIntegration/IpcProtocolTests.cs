using System.Text;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Serialization;
using Iris.Platform.Windows.SystemIntegration;
using Shouldly;

namespace Iris.Platform.Windows.Tests.SystemIntegration;

public sealed class IpcProtocolTests
{
    [Theory]
    [InlineData("  中文 😀\r\nquote: \"word\"  ")]
    [InlineData("--action Exit")]
    public async Task CompactRoundTrip_PreservesText_WithoutChangingGlobalOptions(string text)
    {
        bool indented = IrisJson.Options.WriteIndented;
        var command = new IpcCommand(IrisAction.TranslateText, text);
        var frame = IpcProtocol.Encode(command);
        frame.Count(b => b == '\n').ShouldBe(1);
        var line = await IpcProtocol.ReadLineAsync(new MemoryStream(frame), IpcProtocol.MaxFrameBytes, TestContext.Current.CancellationToken);
        IpcProtocol.Decode(line).ShouldBe(command);
        IrisJson.Options.WriteIndented.ShouldBe(indented); indented.ShouldBeTrue();
        command.ToString().ShouldNotContain(text);
    }

    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("{}")]
    [InlineData("{\"action\":1}")][InlineData("{\"action\":\"1\"}")]
    [InlineData("{\"action\":\"Unknown\"}")][InlineData("{\"action\":\"Exit\",\"Action\":\"Exit\"}")]
    [InlineData("{\"action\":\"Exit\",\"extra\":1}")][InlineData("{\"action\":\"Exit\",\"payload\":\"private\"}")]
    [InlineData("{\"action\":\"TranslateText\",\"payload\":\" \"}")]
    [InlineData("{\"action\":\"TranslateText\",\"payload\":1}")]
    [InlineData("{\"action\":\"TranslateText\",\"payload\":\"x\",\"PAYLOAD\":null}")]
    public void InvalidFramesAreRejectedWithFixedSafeError(string json)
    {
        var error = Should.Throw<IrisException>(() => IpcProtocol.Decode(Encoding.UTF8.GetBytes(json)));
        error.Code.ShouldBe(ErrorCode.InvalidArgument); error.ToString().ShouldNotContain("private");
    }
    [Fact]
    public void CaseInsensitiveNames_AndExistingJsonCommentRules_ArePreserved()
    {
        var command = IpcProtocol.Decode("{/*comment*/\"ACTION\":\"eXiT\",\"Payload\":null,}"u8.ToArray());
        command.Action.ShouldBe(IrisAction.Exit);
        Should.Throw<IrisException>(() => IpcProtocol.Decode(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' }));
        Should.Throw<IrisException>(() => IpcProtocol.Decode(new byte[] { 0xC3, 0x28 }));
        Should.Throw<IrisException>(() => IpcProtocol.Encode(new(IrisAction.TranslateText, "\ud800")));
    }
    [Fact]
    public async Task IncrementalBudgetAcceptsExactLimitAndCrLf_RejectsTruncationAndOversize()
    {
        byte[] valid = "{\"action\":\"Exit\"}"u8.ToArray();
        var exact = new byte[IpcProtocol.MaxFrameBytes + 2]; Array.Fill(exact, (byte)' '); valid.CopyTo(exact, 0);
        exact[^2] = (byte)'\r'; exact[^1] = (byte)'\n';
        var line = await IpcProtocol.ReadLineAsync(new Fragmented(exact), IpcProtocol.MaxFrameBytes, TestContext.Current.CancellationToken);
        line.Length.ShouldBe(IpcProtocol.MaxFrameBytes); IpcProtocol.Decode(line).Action.ShouldBe(IrisAction.Exit);
        await Should.ThrowAsync<IrisException>(() => IpcProtocol.ReadLineAsync(new MemoryStream(valid), IpcProtocol.MaxFrameBytes, CancellationToken.None));
        var oversized = new Fragmented(new byte[IpcProtocol.MaxFrameBytes + 500]);
        await Should.ThrowAsync<IrisException>(() => IpcProtocol.ReadLineAsync(oversized, IpcProtocol.MaxFrameBytes, CancellationToken.None));
        oversized.Position.ShouldBe(IpcProtocol.MaxFrameBytes + 2);
        Should.Throw<IrisException>(() => IpcProtocol.Encode(new(IrisAction.TranslateText, new string('x', IpcProtocol.MaxFrameBytes))));
    }
    private sealed class Fragmented(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
    }
}
