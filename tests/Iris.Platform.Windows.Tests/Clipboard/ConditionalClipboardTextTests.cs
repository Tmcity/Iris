using System.Text;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Tests.Selection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class ConditionalClipboardTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReadText_RejectsOddSizeOrMalformedUtf16(bool odd) => SelectionTestScope.Run(async s =>
    {
        s.Native.Add(13, odd ? [1, 2, 3] : [0, 0xD8, 0, 0]);
        (await s.Clipboard.ReadTextIfVersionAsync(s.Native.Version)).Status.ShouldBe(ClipboardTextStatus.Invalid);
    });
    [Theory]
    [InlineData("short", 1_000_000, 0)]
    [InlineData("", 2, 1)]
    [InlineData("  ", 6, 1)]
    public Task ReadText_HandlesNulAndLargePadding(string text, int allocation, int expected) => SelectionTestScope.Run(async s =>
    {
        s.Native.Add(13, Encoding.Unicode.GetBytes(text + '\0'), (nuint)allocation);
        var result = await s.Clipboard.ReadTextIfVersionAsync(s.Native.Version);
        result.Status.ShouldBe((ClipboardTextStatus)expected);
        result.Text.ShouldBe(expected == 0 ? text : null);
        result.ToString().ShouldNotContain("short");
        s.Native.Count("Free").ShouldBe(0);
    });

    [Theory]
    [InlineData(65_536, true, 0)]
    [InlineData(65_537, true, 3)]
    [InlineData(65_537, false, 3)]
    [InlineData(5, false, 4)]
    public Task ReadText_EnforcesBoundedUtf16AndTermination(int chars, bool terminated, int expected) => SelectionTestScope.Run(async s =>
    {
        s.Native.Add(13, Encoding.Unicode.GetBytes(new string('x', chars) + (terminated ? "\0" : "")));
        var result = await s.Clipboard.ReadTextIfVersionAsync(s.Native.Version);
        result.Status.ShouldBe((ClipboardTextStatus)expected);
        if (expected == 0) result.Text!.Length.ShouldBe(chars);
        s.Native.Count("Free").ShouldBe(0);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReadText_VersionChangeNeverReturnsMixedText(bool afterRead) => SelectionTestScope.Run(async s =>
    {
        s.Seed();
        var expected = s.Native.Version;
        if (afterRead) s.Native.After = api => { if (api == "CopyFrom") s.Native.Version = new(1, 11, 12); };
        else s.Native.Version = new(1, 11, 12);
        var result = await s.Clipboard.ReadTextIfVersionAsync(expected);
        result.Status.ShouldBe(ClipboardTextStatus.Changed);
        result.Text.ShouldBeNull();
        s.Native.Count("Get").ShouldBe(afterRead ? 1 : 0);
    });
}
