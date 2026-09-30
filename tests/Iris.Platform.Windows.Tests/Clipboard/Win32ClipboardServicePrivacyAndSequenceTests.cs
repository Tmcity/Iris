using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class Win32ClipboardServicePrivacyAndSequenceTests
{
    private const string SecretMarker = "FAKE-SECRET-MARKER-987654321-XYZ";

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(12345678u)]
    [InlineData(uint.MaxValue)]
    public void GetSequenceNumber_ReturnsNativeSequenceDirectly_WithoutOpeningClipboardOrGate(uint expectedSeq)
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            SequenceNumber = expectedSeq
        };

        var service = new Win32ClipboardService(null, null, adapter);

        uint actual = service.GetSequenceNumber();
        actual.ShouldBe(expectedSeq);

        adapter.CallLog.ShouldContain("GetClipboardSequenceNumber");
        adapter.CallLog.ShouldNotContain("OpenClipboard");
    }

    [Fact]
    public async Task Privacy_InjectedFailure_DoesNotLeakTextContent_InExceptionsOrLogs()
    {
        var fakeLogger = new MemoryLogger<Win32ClipboardService>();
        var adapter = new FakeNativeClipboardAdapter
        {
            OnSetClipboardData = (_, _) => 0,
            LastError = 5 // Access denied
        };

        var service = new Win32ClipboardService(null, fakeLogger, adapter);

        var secretText = $"Sensitive Header: {SecretMarker} - password123";
        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync(secretText, TestContext.Current.CancellationToken));

        // Assert exception does not contain secret
        ex.Message.ShouldNotContain(SecretMarker);
        ex.Message.ShouldNotContain("password123");
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldNotContain(SecretMarker);
        ex.Detail.ShouldNotContain("password123");
        ex.ToString().ShouldNotContain(SecretMarker);

        // Assert log records do not contain secret
        foreach (var log in fakeLogger.Entries)
        {
            log.Message.ShouldNotContain(SecretMarker);
            log.Message.ShouldNotContain("password123");
        }
    }

    [Fact]
    public async Task Privacy_InvalidImageData_DoesNotLeakRawDataOrExceptionInLogs()
    {
        var fakeLogger = new MemoryLogger<Win32ClipboardService>();
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, fakeLogger, adapter);

        // Corrupt image with secret marker in header
        byte[] corruptBytes = [0xFF, 0xD8, 0xFF, .. System.Text.Encoding.UTF8.GetBytes(SecretMarker)];
        var imageData = new ImageData(corruptBytes, ImageFormat.Jpeg, 10, 10);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(imageData, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.ImageInvalid);
        ex.Message.ShouldNotContain(SecretMarker);
        if (ex.Detail != null)
        {
            ex.Detail.ShouldNotContain(SecretMarker);
        }
        ex.ToString().ShouldNotContain(SecretMarker);

        foreach (var log in fakeLogger.Entries)
        {
            log.Message.ShouldNotContain(SecretMarker);
        }
    }

    private sealed class MemoryLogger<T> : ILogger<T>
    {
        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            lock (Entries)
            {
                Entries.Add(new LogEntry(logLevel, msg, exception));
            }
        }
    }
}
