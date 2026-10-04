using Iris.Core.Platform;
using Iris.Platform.Windows.Selection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class CopySafetyPolicyTests
{
    [Theory]
    [InlineData("Edit", true)]
    [InlineData("richedit", true)]
    [InlineData("RichEdit20A", true)]
    [InlineData("RichEdit20W", true)]
    [InlineData("richedit50w", true)]
    [InlineData("EditCustom", false)]
    [InlineData("Chrome_RenderWidgetHostHWND", false)]
    [InlineData("ConsoleWindowClass", false)]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", false)]
    public void Capture_RequiresExactPositiveClassPermission(string name, bool allowed)
    {
        var native = new FakeCopySafetyNative { ClassName = name };
        var policy = new CopySafetyPolicy(native);
        policy.Capture(new("app.exe", 5000, "private title", 0x1234)).HasValue.ShouldBe(allowed);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("menu")]
    [InlineData("moving")]
    [InlineData("invisible")]
    [InlineData("disabled")]
    [InlineData("notChild")]
    [InlineData("wrongPid")]
    [InlineData("noFocus")]
    [InlineData("ownPid")]
    [InlineData("terminalHost")]
    public void Capture_RejectsUnsafeOrUnconfirmedFocus(string fault)
    {
        var native = new FakeCopySafetyNative();
        switch (fault)
        {
            case "password": native.Style = 0x20; break;
            case "menu": native.Flags = 4; break;
            case "moving": native.Flags = 2; break;
            case "invisible": native.Visible = false; break;
            case "disabled": native.Enabled = false; break;
            case "notChild": native.Child = false; break;
            case "wrongPid": native.Pid = 6; break;
            case "noFocus": native.HasFocus = false; break;
            case "ownPid": native.OwnPid = 5000; break;
            case "terminalHost": native.SourceClassName = "ConsoleWindowClass"; break;
        }
        new CopySafetyPolicy(native).Capture(new("app.exe", 5000, null, 0x1234)).ShouldBeNull();
    }

    [Fact]
    public void Guard_RejectsFocusSwitchWithinSameSource()
    {
        var native = new FakeCopySafetyNative();
        var policy = new CopySafetyPolicy(native);
        var source = new ForegroundAppInfo("app.exe", 5000, null, 0x1234);
        var permit = policy.Capture(source)!.Value;
        native.Focus++;
        policy.IsValid(source, permit).ShouldBeFalse();
        native.Focus--;
        native.ClassName = "ConsoleWindowClass";
        policy.IsValid(source, permit).ShouldBeFalse();
    }
}
