using System;
using System.Collections.Generic;

namespace Iris.Platform.Windows.Selection;

internal interface INativeUiaAdapter
{
    nint GetForegroundWindow();
    uint GetWindowProcessId(nint hwnd);
    bool IsWindowValid(nint hwnd);
    bool IsWindowOrAncestor(nint childHwnd, nint expectedAncestorHwnd);
    uint GetCurrentProcessId();

    IUiaAutomationSession CreateSession(int connectionTimeoutMs, int transactionTimeoutMs);
}

internal interface IUiaAutomationSession : IDisposable
{
    IUiaElement? GetFocusedElement();
}

internal interface IUiaElement : IDisposable
{
    uint ProcessId { get; }
    nint NativeWindowHandle { get; }
    bool IsPassword { get; }

    IUiaElement? GetParent(int maxDepth);
    IUiaTextPattern? GetTextPattern();
}

internal interface IUiaTextPattern : IDisposable
{
    bool SupportsSelection { get; }
    IReadOnlyList<IUiaTextRange> GetSelection();
}

internal interface IUiaTextRange : IDisposable
{
    string? GetText(int maxLength);
}
