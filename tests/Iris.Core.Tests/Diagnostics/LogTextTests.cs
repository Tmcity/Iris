using System;
using Iris.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Diagnostics;

public class LogTextTests
{
    [Fact]
    public void Fingerprint_NullOrEmpty_ReturnsLenZero()
    {
        LogText.Fingerprint(null).ShouldBe("len=0");
        LogText.Fingerprint(string.Empty).ShouldBe("len=0");
    }

    [Fact]
    public void Fingerprint_KnownInput_MatchesExpectedHash()
    {
        LogText.Fingerprint("hello").ShouldBe("len=5 sha=2cf24dba");
    }

    [Fact]
    public void Fingerprint_DoesNotContainText()
    {
        const string secret = "secret-content-xyz";
        var fp = LogText.Fingerprint(secret);
        fp.ShouldNotContain(secret);
    }

    [Fact]
    public void Preview_ShortText_NoEllipsis()
    {
        var preview = LogText.Preview("hello");
        preview.ShouldBe("len=5 sha=2cf24dba head=\"hello\"");
        preview.ShouldNotContain("…");
    }

    [Fact]
    public void Preview_LongText_TruncatesAt32WithEllipsis()
    {
        var longText = new string('A', 50);
        var preview = LogText.Preview(longText);

        preview.ShouldContain($"head=\"{new string('A', 32)}…\"");
    }

    [Fact]
    public void Preview_EscapesNewlinesTabsAndQuotes()
    {
        var raw = "line1\r\nline2\t\"quoted\"";
        var preview = LogText.Preview(raw);

        // Should not have literal newline or tab control characters
        preview.ShouldNotContain("\r");
        preview.ShouldNotContain("\n");
        preview.ShouldNotContain("\t");

        // Should contain escaped representations
        preview.ShouldContain("\\r");
        preview.ShouldContain("\\n");
        preview.ShouldContain("\\t");
        preview.ShouldContain("\\\"quoted\\\"");
    }

    [Fact]
    public void Preview_DoesNotSplitSurrogatePair()
    {
        // 31 standard chars + 1 surrogate pair (2 chars: \uD83D\uDE00) + trailing chars
        var emoji = "😀"; // \uD83D\uDE00
        var text = new string('A', 31) + emoji + "extra";

        var preview = LogText.Preview(text);

        // Since char 31 is high surrogate, head should only take first 31 chars and append '…'
        preview.ShouldContain($"head=\"{new string('A', 31)}…\"");

        // Ensure there are no orphaned surrogate characters in the output
        for (var i = 0; i < preview.Length; i++)
        {
            if (char.IsSurrogate(preview[i]))
            {
                char.IsSurrogatePair(preview, i).ShouldBeTrue("Surrogate pair was split or incomplete");
                i++; // skip low surrogate
            }
        }
    }

    [Fact]
    public void Preview_EscapesBackslashAndSpecialControlCharacters()
    {
        // Backslash, ESC (\u001B), NUL (\0), and Line Separator (\u2028)
        var input = "a\\b\u001bc\0d\u2028e";
        var preview = LogText.Preview(input);

        preview.ShouldContain(@"head=""a\\b\u001Bc\u0000d\u2028e""");
    }
}
