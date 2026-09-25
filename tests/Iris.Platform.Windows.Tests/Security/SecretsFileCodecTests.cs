using System;
using System.Collections.Generic;
using System.Text;
using Iris.Platform.Windows.Security;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Security;

public sealed class SecretsFileCodecTests
{
    [Fact]
    public void SerializeAndParse_Roundtrip_MatchesAndKeysSortedOrdinal_NoBom_TrailingNewline()
    {
        var entries = new Dictionary<string, byte[]>
        {
            ["provider/z-key"] = [1, 2, 3, 4],
            ["provider/a-key"] = [5, 6, 7, 8],
            ["provider/m-key"] = [9, 10, 11, 12],
        };

        var bytes = SecretsFileCodec.Serialize(entries);

        // Check no BOM
        bytes.Length.ShouldBeGreaterThan(3);
        var hasBom = bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasBom.ShouldBeFalse();

        // Check trailing newline
        bytes[^1].ShouldBe((byte)'\n');

        // Check format and version fields in JSON text
        var jsonText = Encoding.UTF8.GetString(bytes);
        jsonText.ShouldContain("\"format\": \"iris-secrets\"");
        jsonText.ShouldContain("\"version\": 1");

        // Check keys ordered ordinally
        var aIndex = jsonText.IndexOf("provider/a-key", StringComparison.Ordinal);
        var mIndex = jsonText.IndexOf("provider/m-key", StringComparison.Ordinal);
        var zIndex = jsonText.IndexOf("provider/z-key", StringComparison.Ordinal);
        aIndex.ShouldBeLessThan(mIndex);
        mIndex.ShouldBeLessThan(zIndex);

        // Parse roundtrip
        var result = SecretsFileCodec.Parse(bytes);
        result.IsCorrupted.ShouldBeFalse();
        result.DroppedKeys.Count.ShouldBe(0);
        result.Entries.Count.ShouldBe(3);
        result.Entries["provider/a-key"].ShouldBe([5, 6, 7, 8]);
        result.Entries["provider/m-key"].ShouldBe([9, 10, 11, 12]);
        result.Entries["provider/z-key"].ShouldBe([1, 2, 3, 4]);
    }

    [Theory]
    [InlineData("{ invalid json syntax")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{"format":"iris-secrets","version":1,"entries":{},"format":"iris-secrets"}""")]
    [InlineData("""{"format":"iris-secrets","version":1,"entries":{"k1":"AQID","k1":"AQID"}}""")]
    [InlineData("""{"format":"wrong-format","version":1,"entries":{}}""")]
    [InlineData("""{"version":1,"entries":{}}""")]
    [InlineData("""{"format":"iris-secrets","version":2,"entries":{}}""")]
    [InlineData("""{"format":"iris-secrets","version":"1","entries":{}}""")]
    [InlineData("""{"format":"iris-secrets","version":1}""")]
    [InlineData("""{"format":"iris-secrets","version":1,"entries":[]}""")]
    public void Parse_CorruptedScenarios_ReturnsIsCorruptedTrue(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = SecretsFileCodec.Parse(bytes);
        result.IsCorrupted.ShouldBeTrue();
    }

    [Fact]
    public void Parse_EmptyFile_ReturnsIsCorruptedTrue()
    {
        var result = SecretsFileCodec.Parse(ReadOnlySpan<byte>.Empty);
        result.IsCorrupted.ShouldBeTrue();
    }

    [Fact]
    public void Parse_WhitespaceFile_ReturnsIsCorruptedTrue()
    {
        var bytes = Encoding.UTF8.GetBytes("   \r\n\t  ");
        var result = SecretsFileCodec.Parse(bytes);
        result.IsCorrupted.ShouldBeTrue();
    }

    [Fact]
    public void Parse_IndividualEntryIssues_DropsBadEntriesAndRetainsValid()
    {
        const string json = """
        {
          "format": "iris-secrets",
          "version": 1,
          "entries": {
            "valid-1": "AQIDBA==",
            "bad-number": 12345,
            "bad-base64": "not!a!valid!base64!",
            "": "AQID",
            "   ": "AQID",
            "valid-2": "BQYHCA=="
          }
        }
        """;

        var bytes = Encoding.UTF8.GetBytes(json);
        var result = SecretsFileCodec.Parse(bytes);

        result.IsCorrupted.ShouldBeFalse();
        result.Entries.Count.ShouldBe(2);
        result.Entries["valid-1"].ShouldBe(new byte[] { 1, 2, 3, 4 });
        result.Entries["valid-2"].ShouldBe(new byte[] { 5, 6, 7, 8 });

        result.DroppedKeys.ShouldContain("bad-number");
        result.DroppedKeys.ShouldContain("bad-base64");
        result.DroppedKeys.ShouldContain("(空键名)");
    }
}
