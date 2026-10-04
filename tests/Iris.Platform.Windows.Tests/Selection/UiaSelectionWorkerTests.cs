using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Platform.Windows.Selection;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class UiaSelectionWorkerTests
{
    private readonly FakeNativeUiaAdapter _adapter = new();
    private readonly UiaRequestFrame _validRequest = new()
    {
        ProtocolVersion = 1,
        Kind = "Request",
        RequestId = "req-123",
        Hwnd = 0x1000,
        ProcessId = 4242
    };

    private static void RunOnMta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Thread.CurrentThread.GetApartmentState().ShouldBe(ApartmentState.MTA);
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw error;
        }
    }

    [Fact]
    public void Execute_OnMtaThread_SucceedsWithSingleRange()
    {
        RunOnMta(() =>
        {
            var range = new FakeUiaTextRange("Hello World");
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern { Ranges = [range] }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Success);
            reply.Text.ShouldBe("Hello World");
            reply.RequestId.ShouldBe("req-123");
            reply.ErrorCode.ShouldBeNull();
            range.IsDisposed.ShouldBeTrue();
            element.IsDisposed.ShouldBeTrue();
            _adapter.Session.IsDisposed.ShouldBeTrue();
        });
    }

    [Fact]
    public void Execute_OnMtaThread_MultipleRanges_JoinedByNewline()
    {
        RunOnMta(() =>
        {
            var r1 = new FakeUiaTextRange("Line 1");
            var r2 = new FakeUiaTextRange("Line 2");
            var r3 = new FakeUiaTextRange("Line 3");
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern { Ranges = [r1, r2, r3] }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Success);
            reply.Text.ShouldBe("Line 1\nLine 2\nLine 3");
            r1.IsDisposed.ShouldBeTrue();
            r2.IsDisposed.ShouldBeTrue();
            r3.IsDisposed.ShouldBeTrue();
        });
    }

    [Fact]
    public void Execute_WhenForegroundWindowChanged_ReturnsTargetChanged()
    {
        RunOnMta(() =>
        {
            _adapter.ForegroundWindow = 0x9999; // Different window
            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.TargetChanged);
            reply.Text.ShouldBeNull();
            reply.ErrorCode.ShouldBe("TARGET_CHANGED");
            _adapter.TotalCreateSessionCalls.ShouldBe(0);
        });
    }

    [Fact]
    public void Execute_WhenTargetIsIrisItself_ReturnsTargetChanged()
    {
        RunOnMta(() =>
        {
            _adapter.CurrentProcessId = 4242; // Same as request
            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.TargetChanged);
            reply.Text.ShouldBeNull();
            reply.ErrorCode.ShouldBe("TARGET_IS_IRIS");
        });
    }

    [Fact]
    public void Execute_WhenNoFocusedElement_ReturnsUnsupported()
    {
        RunOnMta(() =>
        {
            _adapter.Session.FocusedElement = null;
            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Unsupported);
            reply.Text.ShouldBeNull();
            reply.ErrorCode.ShouldBe("NO_FOCUSED_ELEMENT");
        });
    }

    [Fact]
    public void Execute_WhenElementFromDifferentProcess_ReturnsUnsupported()
    {
        RunOnMta(() =>
        {
            var element = new FakeUiaElement
            {
                ProcessId = 9999, // Mismatched PID
                NativeWindowHandle = 0x1000
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Unsupported);
            reply.Text.ShouldBeNull();
            reply.ErrorCode.ShouldBe("ELEMENT_SOURCE_MISMATCH");
        });
    }

    [Fact]
    public void Execute_WhenVirtualElementAncestorMatchesHwnd_Succeeds()
    {
        RunOnMta(() =>
        {
            var parent = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000 // Window matches expected
            };
            var range = new FakeUiaTextRange("Virtual Element Text");
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0, // Virtual element with no HWND
                Parent = parent,
                TextPattern = new FakeUiaTextPattern { Ranges = [range] }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Success);
            reply.Text.ShouldBe("Virtual Element Text");
        });
    }

    [Fact]
    public void Execute_WhenVirtualElementAncestorHasNoMatchingHwnd_ReturnsUnsupported()
    {
        RunOnMta(() =>
        {
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0, // Virtual element
                Parent = null // No parent with HWND
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Unsupported);
            reply.ErrorCode.ShouldBe("ELEMENT_SOURCE_MISMATCH");
        });
    }

    [Fact]
    public void Execute_WhenIsPasswordIsTrue_ReturnsSensitiveWithNullText()
    {
        RunOnMta(() =>
        {
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                IsPassword = true,
                TextPattern = new FakeUiaTextPattern { Ranges = [new FakeUiaTextRange("SecretPassword123")] }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Sensitive);
            reply.Text.ShouldBeNull();
            reply.ErrorCode.ShouldBe("PASSWORD_CONTROL");
        });
    }

    [Fact]
    public void Execute_WhenPatternNotSupported_ReturnsUnsupported()
    {
        RunOnMta(() =>
        {
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = null
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Unsupported);
            reply.Text.ShouldBeNull();
            reply.ErrorCode.ShouldBe("TEXT_PATTERN_UNSUPPORTED");
        });
    }

    [Fact]
    public void Execute_WhenPatternDoesNotSupportSelection_ReturnsUnsupported()
    {
        RunOnMta(() =>
        {
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern { SupportsSelection = false }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Unsupported);
            reply.ErrorCode.ShouldBe("TEXT_PATTERN_UNSUPPORTED");
        });
    }

    [Fact]
    public void Execute_WhenRangesEmpty_ReturnsEmpty()
    {
        RunOnMta(() =>
        {
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern { Ranges = [] }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Empty);
            reply.Text.ShouldBeNull();
        });
    }

    [Fact]
    public void Execute_WhenRangeIsWhitespaceOrDegenerateCaret_ReturnsEmpty()
    {
        RunOnMta(() =>
        {
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern
                {
                    Ranges = [new FakeUiaTextRange(""), new FakeUiaTextRange("   \t  ")]
                }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Empty);
            reply.Text.ShouldBeNull();
        });
    }

    [Fact]
    public void Execute_WhenRangesExceed32_ReturnsTooLong()
    {
        RunOnMta(() =>
        {
            var ranges = new System.Collections.Generic.List<FakeUiaTextRange>();
            for (int i = 0; i < 33; i++)
            {
                ranges.Add(new FakeUiaTextRange("a"));
            }

            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern { Ranges = ranges }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.TooLong);
            reply.ErrorCode.ShouldBe("RANGE_COUNT_EXCEEDED");
            reply.Text.ShouldBeNull();
        });
    }

    [Fact]
    public void Execute_WhenCumulativeTextExceeds65536_ReturnsTooLong()
    {
        RunOnMta(() =>
        {
            var largeText = new string('x', 65_537);
            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern { Ranges = [new FakeUiaTextRange(largeText)] }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.TooLong);
            reply.ErrorCode.ShouldBe("LENGTH_EXCEEDED");
            reply.Text.ShouldBeNull();
        });
    }

    [Fact]
    public void Execute_WhenForegroundWindowDriftsBeforeFinishing_DiscardsTextAndReturnsTargetChanged()
    {
        RunOnMta(() =>
        {
            var range = new FakeUiaTextRange();
            range.GetTextCallback = _ =>
            {
                // 模拟在读取文本过程中，前台窗口漂移
                _adapter.ForegroundWindow = 0x9999;
                return "Valid Text";
            };

            var element = new FakeUiaElement
            {
                ProcessId = 4242,
                NativeWindowHandle = 0x1000,
                TextPattern = new FakeUiaTextPattern { Ranges = [range] }
            };
            _adapter.Session.FocusedElement = element;

            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.TargetChanged);
            reply.ErrorCode.ShouldBe("TARGET_CHANGED");
            reply.Text.ShouldBeNull();
        });
    }

    [Fact]
    public void Execute_WhenComExceptionThrown_ReturnsFailedWithMappedErrorCode()
    {
        RunOnMta(() =>
        {
            _adapter.ThrowOnCreateSession = Marshal.GetExceptionForHR(unchecked((int)0x80004005));
            var reply = UiaSelectionWorker.Execute(_validRequest, _adapter);

            reply.Status.ShouldBe(UiaSelectionStatus.Failed);
            reply.ErrorCode.ShouldBe("COM_0x80004005");
            reply.Text.ShouldBeNull();
        });
    }
}
