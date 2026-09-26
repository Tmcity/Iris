using System;
using Iris.App.Tray;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Tray;

public sealed class TrayCreationRetryPolicyTests
{
    [Fact]
    public void ErrorLogThreshold_IsThirty()
    {
        TrayCreationRetryPolicy.ErrorLogThreshold.ShouldBe(30);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(30, 2)]
    public void GetDelay_UpToThreshold_ReturnsTwoSeconds(int failedAttempts, int expectedSeconds)
    {
        var delay = TrayCreationRetryPolicy.GetDelay(failedAttempts);

        delay.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(31, 30)]
    [InlineData(100, 30)]
    public void GetDelay_AfterThreshold_ReturnsThirtySeconds(int failedAttempts, int expectedSeconds)
    {
        var delay = TrayCreationRetryPolicy.GetDelay(failedAttempts);

        delay.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void GetDelay_WithNonPositiveFailedAttempts_ThrowsArgumentOutOfRangeException(int invalidAttempts)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => TrayCreationRetryPolicy.GetDelay(invalidAttempts));
    }

}
