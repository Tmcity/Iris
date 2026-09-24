using System;
using System.Linq;
using Iris.Core.Errors;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Contracts;

public class ErrorCodeTests
{
    [Fact]
    public void Values_AreUnique()
    {
        var values = Enum.GetValues<ErrorCode>();
        var distinctValues = values.Distinct().ToArray();
        values.Length.ShouldBe(distinctValues.Length);
    }

    [Fact]
    public void NonZeroValues_AreInDefinedRanges()
    {
        var values = Enum.GetValues<ErrorCode>();
        foreach (var val in values)
        {
            var intVal = (int)val;
            if (intVal == 0)
            {
                val.ShouldBe(ErrorCode.Unknown);
                continue;
            }

            intVal.ShouldBeGreaterThanOrEqualTo(1001);
            intVal.ShouldBeLessThanOrEqualTo(5999);
        }
    }

    [Fact]
    public void IrisException_ToErrorInfo_MapsAllFields()
    {
        var inner = new InvalidOperationException("Inner error");
        var ex = new IrisException(
            code: ErrorCode.RateLimited,
            message: "Too many requests",
            innerException: inner,
            httpStatus: 429,
            retryable: true,
            providerId: "test-provider",
            detail: "custom detail");

        var info = ex.ToErrorInfo();
        info.Code.ShouldBe(ErrorCode.RateLimited);
        info.Message.ShouldBe("Too many requests");
        info.Detail.ShouldBe("custom detail");
        info.HttpStatus.ShouldBe(429);
        info.Retryable.ShouldBeTrue();
        info.ProviderId.ShouldBe("test-provider");
    }
}
