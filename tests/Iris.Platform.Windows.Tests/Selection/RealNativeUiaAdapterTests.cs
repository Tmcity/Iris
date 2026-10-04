using System;
using System.Runtime.InteropServices;
using Iris.Platform.Windows.Selection;
using NSubstitute;
using Shouldly;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Xunit;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class RealNativeUiaAdapterTests
{
    [Fact]
    public void RealUiaTextPattern_GetSelection_WhenArrayLengthExceeds32_ThrowsRangeCountExceeded_WithoutCallingGetElement()
    {
        var mockPattern = Substitute.For<IUIAutomationTextPattern>();
        var mockArray = Substitute.For<IUIAutomationTextRangeArray>();

        mockArray.Length.Returns(33);
        mockPattern.GetSelection().Returns(mockArray);

        using var pattern = new RealUiaTextPattern(mockPattern);

        var ex = Should.Throw<InvalidOperationException>(() => pattern.GetSelection());
        ex.Message.ShouldBe("RANGE_COUNT_EXCEEDED");

        mockArray.DidNotReceive().GetElement(Arg.Any<int>());
    }

    [Fact]
    public void RealUiaElement_GetTextPattern_FallsBackToClassicPattern_WhenTextPattern2Throws()
    {
        var mockElement = Substitute.For<IUIAutomationElement>();
        var mockPattern = Substitute.For<IUIAutomationTextPattern>();

        // TextPattern2 throws COMException (e.g. 0x80040204 UIA_E_NOTSUPPORTED)
        mockElement.GetCurrentPattern((UIA_PATTERN_ID)UIA_PATTERN_ID.UIA_TextPattern2Id)
            .Returns(_ => throw Marshal.GetExceptionForHR(unchecked((int)0x80040204))!);

        // Classic TextPattern succeeds
        mockElement.GetCurrentPattern((UIA_PATTERN_ID)UIA_PATTERN_ID.UIA_TextPatternId)
            .Returns(mockPattern);

        var mockUia = Substitute.For<IUIAutomation>();
        using var element = new RealUiaElement(mockElement, mockUia);

        var pattern = element.GetTextPattern();
        pattern.ShouldNotBeNull();
    }
}
