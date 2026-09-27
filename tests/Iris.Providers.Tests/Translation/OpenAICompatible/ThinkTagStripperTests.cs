using System;
using System.Diagnostics;
using System.Text;
using Iris.Providers.Translation.OpenAICompatible;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Translation.OpenAICompatible;

public sealed class ThinkTagStripperTests
{
    [Fact]
    public void Process_WithoutThinkTag_ReturnsTextOrWhitespaceVerbatim()
    {
        var stripper1 = new ThinkTagStripper();
        var result1 = stripper1.Process("Hello") + stripper1.Flush();
        result1.ShouldBe("Hello");

        var stripper2 = new ThinkTagStripper();
        var result2 = stripper2.Process("  Hi") + stripper2.Flush();
        result2.ShouldBe("  Hi");
    }

    [Fact]
    public void Process_CompleteTagInSingleDelta_StripsTagAndLeadingWhitespace()
    {
        var stripper = new ThinkTagStripper();
        var result = stripper.Process("<think>abc</think>\n\n你好") + stripper.Flush();
        result.ShouldBe("你好");
    }

    [Fact]
    public void Process_TagArbitrarilySplit_ProducesExpectedResult()
    {
        const string text = "<think>推理</think>  译文";

        // Every split into two parts
        for (var i = 0; i <= text.Length; i++)
        {
            var part1 = text[..i];
            var part2 = text[i..];

            var stripper = new ThinkTagStripper();
            var out1 = stripper.Process(part1);
            var out2 = stripper.Process(part2);
            var flush = stripper.Flush();

            var combined = out1 + out2 + flush;
            combined.ShouldBe("译文", $"Failed when split at index {i}: '{part1}' | '{part2}'");
        }

        // Each character individually
        {
            var stripper = new ThinkTagStripper();
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                sb.Append(stripper.Process(ch.ToString()));
            }
            sb.Append(stripper.Flush());
            sb.ToString().ShouldBe("译文");
        }
    }

    [Fact]
    public void Process_LeadingWhitespaceBeforeThinkTag_StripsTagAndReturnsRemainder()
    {
        var stripper = new ThinkTagStripper();
        var result = stripper.Process("\n<think>x</think>y") + stripper.Flush();
        result.ShouldBe("y");
    }

    [Fact]
    public void Process_ThinkTagInMiddleOfText_PreservesTagVerbatim()
    {
        var stripper = new ThinkTagStripper();
        var result = stripper.Process("A <think>B</think>") + stripper.Flush();
        result.ShouldBe("A <think>B</think>");
    }

    [Fact]
    public void Process_LooksLikeThinkTagButIsNot_OutputsVerbatim()
    {
        var stripper1 = new ThinkTagStripper();
        var result1 = stripper1.Process("<thinking>") + stripper1.Flush();
        result1.ShouldBe("<thinking>");

        var stripper2 = new ThinkTagStripper();
        var out1 = stripper2.Process("<th");
        var out2 = stripper2.Process("e end");
        var flush2 = stripper2.Flush();
        (out1 + out2 + flush2).ShouldBe("<the end");
    }

    [Fact]
    public void Flush_InDetectingOrInThink_HandlesIncompleteTagsCorrectly()
    {
        var stripper1 = new ThinkTagStripper();
        stripper1.Process("<thi");
        stripper1.Flush().ShouldBe("<thi");

        var stripper2 = new ThinkTagStripper();
        stripper2.Process("<think>未完");
        stripper2.Flush().ShouldBe(string.Empty);
    }

    [Fact]
    public void Process_VeryLongThinkingContent_DoesNotAccumulateAndExecutesQuickly()
    {
        var stripper = new ThinkTagStripper();
        stripper.Process("<think>");

        var chunk = new string('x', 100);
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < 10000; i++) // 1,000,000 chars total
        {
            var outChunk = stripper.Process(chunk);
            outChunk.ShouldBe(string.Empty);
        }

        var finalChunk = stripper.Process("</think>\nDone");
        var flush = stripper.Flush();
        sw.Stop();

        (finalChunk + flush).ShouldBe("Done");
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Process_WhitespaceAfterCloseTagAcrossDeltas_StripsAllWhitespace()
    {
        var stripper = new ThinkTagStripper();
        var o1 = stripper.Process("<think>x</think>");
        var o2 = stripper.Process("  ");
        var o3 = stripper.Process("\n");
        var o4 = stripper.Process("译文");
        var flush = stripper.Flush();

        var combined = o1 + o2 + o3 + o4 + flush;
        combined.ShouldBe("译文");
    }
}
