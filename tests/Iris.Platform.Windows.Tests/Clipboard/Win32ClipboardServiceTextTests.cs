using System;
using System.Text;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class Win32ClipboardServiceTextTests
{
    [Theory]
    [InlineData("Hello, World!")]
    [InlineData("Iris 桌面翻译工具测试")]
    [InlineData("🎨✨🚀 Multi-byte Emoji: 👩‍💻👨‍👩‍👧‍👦")]
    [InlineData("Line1\r\nLine2\nLine3\rLine4")]
    [InlineData("")]
    public async Task SetTextAsync_And_GetTextAsync_RoundtripsSuccessfully(string expectedText)
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        await service.SetTextAsync(expectedText, TestContext.Current.CancellationToken);

        // Verify SetClipboardData wrote CF_UNICODETEXT
        adapter.ClipboardFormats.ContainsKey(DibV5Helper.CF_UNICODETEXT).ShouldBeTrue();

        string? readText = await service.GetTextAsync(TestContext.Current.CancellationToken);
        readText.ShouldBe(expectedText);
    }

    [Fact]
    public async Task GetTextAsync_WhenFormatNotAvailable_ReturnsNull()
    {
        var adapter = new FakeNativeClipboardAdapter();
        adapter.ClipboardFormats.Clear(); // No formats
        var service = new Win32ClipboardService(null, null, adapter);

        var result = await service.GetTextAsync(TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetTextAsync_WhenHandleIsZero_ReturnsNull()
    {
        var adapter = new FakeNativeClipboardAdapter();
        // Format is reported available, but GetClipboardData returns 0
        adapter.OnIsClipboardFormatAvailable = _ => true;
        adapter.OnGetClipboardData = _ => 0;
        var service = new Win32ClipboardService(null, null, adapter);

        var result = await service.GetTextAsync(TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SetTextAsync_WhenTextIsNull_ThrowsInvalidArgument_BeforeTouchingAdapter()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync(null!, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
        adapter.AllocatedHGlobals.ShouldBeEmpty();
        adapter.CallLog.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("Prefix\0Suffix")]
    [InlineData("\0Leading")]
    [InlineData("Trailing\0")]
    public async Task SetTextAsync_WhenTextContainsEmbeddedNull_ThrowsInvalidArgument_BeforeTouchingAdapter(string invalidText)
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync(invalidText, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
        adapter.AllocatedHGlobals.ShouldBeEmpty();
        adapter.CallLog.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetTextAsync_WhenTextExceedsBudget_ThrowsInputTooLong_BeforeTouchingAdapter()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        string longText = new('A', 4_194_305);
        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync(longText, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.InputTooLong);
        adapter.AllocatedHGlobals.ShouldBeEmpty();
        adapter.CallLog.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTextAsync_WhenBlockSizeLessThanTwo_ThrowsInvalidResponse()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var hMem = adapter.GlobalAlloc(0, 1); // 1 byte size
        adapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = hMem;

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.GetTextAsync(TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task GetTextAsync_WhenBlockSizeIsOdd_ThrowsInvalidResponse()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var hMem = adapter.GlobalAlloc(0, 7); // 7 bytes size (odd)
        adapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = hMem;

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.GetTextAsync(TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task GetTextAsync_WhenBlockSizeExceedsMaximumBytes_ThrowsInputTooLong()
    {
        var adapter = new FakeNativeClipboardAdapter();
        // 8,388,610 + 2 = 8,388,612 bytes
        var hMem = (nint)2001;
        adapter.MemoryBlocks[hMem] = [];
        adapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = hMem;

        adapter.OnGlobalLock = _ => 12345;
        // Mock size returning > 8,388,610 bytes
        adapter.CallLog.Clear();
        var service = new Win32ClipboardService(null, null, adapter);

        // Custom adapter to return size > 8,388,610
        var customAdapter = new FakeNativeClipboardAdapter();
        customAdapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = (nint)3001;
        customAdapter.MemoryBlocks[(nint)3001] = [0, 0];

        // Simulate huge GlobalSize
        var hugeSizeAdapter = new HugeSizeAdapter(customAdapter, (nuint)8_388_612);
        var hugeService = new Win32ClipboardService(null, null, hugeSizeAdapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            hugeService.GetTextAsync(TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.InputTooLong);
    }

    [Fact]
    public async Task GetTextAsync_WhenMissingNullTerminatorWithinBounds_ThrowsInvalidResponse()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var hMem = adapter.GlobalAlloc(0, 6);
        // Fill 6 bytes with non-zero UTF-16 code units ('ABC')
        byte[] bytes = Encoding.Unicode.GetBytes("ABC");
        bytes.CopyTo(adapter.MemoryBlocks[hMem], 0);
        adapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = hMem;

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.GetTextAsync(TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task GetTextAsync_WhenNullTerminatorAtOffsetZero_ReturnsEmptyString()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var hMem = adapter.GlobalAlloc(0, 4);
        adapter.MemoryBlocks[hMem][0] = 0;
        adapter.MemoryBlocks[hMem][1] = 0;
        adapter.MemoryBlocks[hMem][2] = 0xFF; // trailing garbage
        adapter.MemoryBlocks[hMem][3] = 0xFF;
        adapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = hMem;

        var service = new Win32ClipboardService(null, null, adapter);

        string? result = await service.GetTextAsync(TestContext.Current.CancellationToken);
        result.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetTextAsync_WhenGarbageAfterNullTerminator_OnlyReadsUpToNullTerminator()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var hMem = adapter.GlobalAlloc(0, 16);
        byte[] validPart = Encoding.Unicode.GetBytes("Hello\0");
        validPart.CopyTo(adapter.MemoryBlocks[hMem], 0);
        // Put garbage after \0
        for (int i = validPart.Length; i < 16; i++)
        {
            adapter.MemoryBlocks[hMem][i] = 0xAA;
        }
        adapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = hMem;

        var service = new Win32ClipboardService(null, null, adapter);

        string? result = await service.GetTextAsync(TestContext.Current.CancellationToken);
        result.ShouldBe("Hello");
    }

    private sealed class HugeSizeAdapter(INativeClipboardAdapter inner, nuint reportedSize) : INativeClipboardAdapter
    {
        public nint CreateOwnerWindow() => inner.CreateOwnerWindow();
        public void DestroyWindow(nint hwnd) => inner.DestroyWindow(hwnd);
        public bool OpenClipboard(nint hwnd) => inner.OpenClipboard(hwnd);
        public bool CloseClipboard() => inner.CloseClipboard();
        public bool EmptyClipboard() => inner.EmptyClipboard();
        public nint SetClipboardData(uint format, nint hMem) => inner.SetClipboardData(format, hMem);
        public nint GetClipboardData(uint format) => inner.GetClipboardData(format);
        public bool IsClipboardFormatAvailable(uint format) => inner.IsClipboardFormatAvailable(format);
        public uint GetClipboardSequenceNumber() => inner.GetClipboardSequenceNumber();
        public nint GlobalAlloc(uint uFlags, nuint dwBytes) => inner.GlobalAlloc(uFlags, dwBytes);
        public nint GlobalFree(nint hMem) => inner.GlobalFree(hMem);
        public nint GlobalLock(nint hMem) => inner.GlobalLock(hMem);
        public bool GlobalUnlock(nint hMem) => inner.GlobalUnlock(hMem);
        public nuint GlobalSize(nint hMem) => reportedSize;
        public int GetLastError() => inner.GetLastError();
    }
}
