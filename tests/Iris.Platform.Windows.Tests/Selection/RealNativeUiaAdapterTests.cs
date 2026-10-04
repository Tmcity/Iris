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

    [Fact]
    public void RealUiaElement_GetTextPattern_PropagatesCOMException_WhenTextPattern2ThrowsDisconnected()
    {
        var mockElement = Substitute.For<IUIAutomationElement>();
        var mockUia = Substitute.For<IUIAutomation>();

        // Disconnection COM error (0x800706BA) must NOT be swallowed or downgraded
        mockElement.GetCurrentPattern((UIA_PATTERN_ID)UIA_PATTERN_ID.UIA_TextPattern2Id)
            .Returns(_ => throw Marshal.GetExceptionForHR(unchecked((int)0x800706BA))!);

        using var element = new RealUiaElement(mockElement, mockUia);

        var ex = Should.Throw<COMException>(() => element.GetTextPattern());
        ex.HResult.ShouldBe(unchecked((int)0x800706BA));
    }

    [Fact]
    public void RealUiaAutomationSession_GetFocusedElement_PropagatesCOMException_WhenUnderlyingUiaThrows()
    {
        var mockUia = Substitute.For<IUIAutomation>();
        mockUia.GetFocusedElement().Returns(_ => throw Marshal.GetExceptionForHR(unchecked((int)0x80004005))!); // E_FAIL

        using var session = new RealUiaAutomationSession(mockUia);

        var ex = Should.Throw<COMException>(() => session.GetFocusedElement());
        ex.HResult.ShouldBe(unchecked((int)0x80004005));
    }

    [Fact]
    public void RealUiaAutomationSession_Dispose_ThrowsInvalidOperationException_WhenCalledOnDifferentThread()
    {
        var mockUia = Substitute.For<IUIAutomation>();
        var session = new RealUiaAutomationSession(mockUia, ownerThreadId: 99999);

        var ex = Should.Throw<InvalidOperationException>(() => session.Dispose());
        ex.Message.ShouldContain("must be disposed on the same thread");
    }

    [Fact]
    public void RealNativeUiaAdapter_CreateSession_ThrowsInvalidOperationException_WhenCalledOnStaThread()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                RealNativeUiaAdapter.Instance.CreateSession(300, 300);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5)).ShouldBeTrue();

        threadEx.ShouldNotBeNull();
        threadEx.ShouldBeOfType<InvalidOperationException>();
        threadEx.Message.ShouldContain("must be executed on an MTA thread");
    }
}
