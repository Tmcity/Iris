using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Errors;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Errors;

public class ErrorMessagesTests
{
    [Fact]
    public void AllDefinedCodes_HaveNonEmptyMessage()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var msg = ErrorMessages.GetDefault(code);
            msg.ShouldNotBeNullOrWhiteSpace($"ErrorCode {code} should have non-empty message");
        }
    }

    [Fact]
    public void AllNonUnknownCodes_HaveDistinctMessage()
    {
        var unknownMsg = ErrorMessages.GetDefault(ErrorCode.Unknown);
        var seenMessages = new Dictionary<string, ErrorCode>();

        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            if (code == ErrorCode.Unknown)
            {
                continue;
            }

            var msg = ErrorMessages.GetDefault(code);
            msg.ShouldNotBe(unknownMsg, $"ErrorCode {code} should not equal Unknown message");

            seenMessages.ContainsKey(msg).ShouldBeFalse(
                $"Duplicate message '{msg}' found for ErrorCode {code}, previously used by {seenMessages.GetValueOrDefault(msg)}");
            seenMessages[msg] = code;
        }
    }

    [Fact]
    public void UndefinedCode_ReturnsUnknownMessage()
    {
        var unknownMsg = ErrorMessages.GetDefault(ErrorCode.Unknown);
        var undefinedMsg = ErrorMessages.GetDefault((ErrorCode)9999);
        undefinedMsg.ShouldBe(unknownMsg);
    }

    [Fact]
    public void SpecificCodes_MatchTableExactText()
    {
        ErrorMessages.GetDefault(ErrorCode.RateLimited).ShouldBe("请求过于频繁，请稍后重试");
        ErrorMessages.GetDefault(ErrorCode.StorageFailure).ShouldBe("读写本地数据失败");
    }
}
