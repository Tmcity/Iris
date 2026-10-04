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

    [Fact]
    public void RealUiaAutomationSession_Dispose_WhenCalledOnDifferentThread_DoesNotConsumeDisposalResponsibility_AndAllowsOwnerThreadToRetry()
    {
        var mockUia = Substitute.For<IUIAutomation>();
        bool releasedCom = false;
        bool coUninited = false;

        int ownerThreadId = Environment.CurrentManagedThreadId;
        var session = new RealUiaAutomationSession(
            mockUia,
            ownerThreadId: ownerThreadId,
            coInitSucceeded: true,
            coUninitAction: () => coUninited = true,
            releaseComAction: obj => releasedCom = true);

        // Call Dispose on wrong thread
        Exception? wrongThreadEx = null;
        var backgroundThread = new Thread(() =>
        {
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                wrongThreadEx = ex;
            }
        });
        backgroundThread.Start();
        backgroundThread.Join(TimeSpan.FromSeconds(5)).ShouldBeTrue();

        wrongThreadEx.ShouldNotBeNull();
        wrongThreadEx.ShouldBeOfType<InvalidOperationException>();
        releasedCom.ShouldBeFalse();
        coUninited.ShouldBeFalse();

        // Now call Dispose on the owner thread: must succeed and release resources!
        session.Dispose();
        releasedCom.ShouldBeTrue();
        coUninited.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)] // S_OK
    [InlineData(1)] // S_FALSE
    public void RealNativeUiaAdapter_CreateSession_WhenCoInitializeExSucceeds_EntersMtaAndTracksCoUninitialize(int hrValue)
    {
        var mockUia = Substitute.For<IUIAutomation>();
        bool coUninited = false;
        bool releasedCom = false;

        var adapter = new RealNativeUiaAdapter(
            coInitFunc: () => new HRESULT(hrValue),
            coUninitAction: () => coUninited = true,
            createUiaFunc: () => mockUia,
            releaseComAction: obj => releasedCom = true);

        var session = adapter.CreateSession(300, 300);
        session.ShouldNotBeNull();

        session.Dispose();
        coUninited.ShouldBeTrue();
        releasedCom.ShouldBeTrue();
    }

    [Fact]
    public void RealNativeUiaAdapter_CreateSession_WhenCoInitializeExFailsRpcEChangedMode_ThrowsImmediatelyWithoutCreatingUiaOrCallingCoUninitialize()
    {
        bool uiaFactoryCalled = false;
        bool coUninited = false;

        var adapter = new RealNativeUiaAdapter(
            coInitFunc: () => new HRESULT(unchecked((int)0x80010106)), // RPC_E_CHANGED_MODE
            coUninitAction: () => coUninited = true,
            createUiaFunc: () => { uiaFactoryCalled = true; return Substitute.For<IUIAutomation>(); },
            releaseComAction: obj => { });

        var ex = Should.Throw<COMException>(() => adapter.CreateSession(300, 300));
        ex.HResult.ShouldBe(unchecked((int)0x80010106));

        uiaFactoryCalled.ShouldBeFalse();
        coUninited.ShouldBeFalse();
    }

    [Fact]
    public void RealNativeUiaAdapter_CreateSession_WhenUiaCreationFails_RollsBackCoUninitialize()
    {
        bool coUninited = false;

        var adapter = new RealNativeUiaAdapter(
            coInitFunc: () => new HRESULT(0),
            coUninitAction: () => coUninited = true,
            createUiaFunc: () => throw new InvalidOperationException("UIA creation failed midway."),
            releaseComAction: obj => { });

        var ex = Should.Throw<Exception>(() => adapter.CreateSession(300, 300));
        ex.Message.ShouldContain("UIA creation failed midway.");

        coUninited.ShouldBeTrue();
    }

    [Fact]
    public void RealUiaTextPattern_GetSelection_WhenRangeCreationFailsMidway_DisposesAlreadyCreatedRanges_AndReleasesArray()
    {
        var mockPattern = Substitute.For<IUIAutomationTextPattern>();
        var mockArray = Substitute.For<IUIAutomationTextRangeArray>();
        var mockRange0 = Substitute.For<IUIAutomationTextRange>();
        var mockRange1 = Substitute.For<IUIAutomationTextRange>();

        mockArray.Length.Returns(3);
        mockArray.GetElement(0).Returns(mockRange0);
        mockArray.GetElement(1).Returns(mockRange1);
        mockArray.GetElement(2).Returns(_ => throw new InvalidOperationException("Range 2 creation failed."));
        mockPattern.GetSelection().Returns(mockArray);

        var releasedObjects = new List<object>();
        using var pattern = new RealUiaTextPattern(mockPattern, releaseComAction: obj => releasedObjects.Add(obj));

        var ex = Should.Throw<Exception>(() => pattern.GetSelection());
        ex.Message.ShouldContain("Range 2 creation failed.");

        // The already created ranges (range 0 and 1) must be disposed during rollback
        releasedObjects.ShouldContain(mockRange0);
        releasedObjects.ShouldContain(mockRange1);

        // The array must be released in finally
        releasedObjects.ShouldContain(mockArray);
    }
}
