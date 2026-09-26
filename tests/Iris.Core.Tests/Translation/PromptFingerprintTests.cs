using System.Text.RegularExpressions;
using Iris.Core.Translation;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class PromptFingerprintTests
{
    [Theory]
    [InlineData("S", "U", "73c2db13edc9b680")]
    [InlineData("S", "V", "69fc421b6ec89313")]
    [InlineData("系统", "{{text}}", "389b1396edfc0e5e")]
    public void Compute_ExpectedInputs_ReturnsExpectedFingerprint(string system, string user, string expectedFingerprint)
    {
        var template = new PromptTemplate
        {
            Id = "test",
            Name = "Test",
            System = system,
            User = user,
        };

        var fingerprint = PromptFingerprint.Compute(template);
        fingerprint.ShouldBe(expectedFingerprint);
    }

    [Fact]
    public void Compute_ReturnsExactSixteenLowercaseHexCharacters()
    {
        var template = BuiltInPrompts.Default;
        var fingerprint = PromptFingerprint.Compute(template);

        fingerprint.Length.ShouldBe(16);
        Regex.IsMatch(fingerprint, "^[0-9a-f]{16}$").ShouldBeTrue();
    }
}
