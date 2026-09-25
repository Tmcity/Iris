using System;
using Iris.App.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Hosting;

public sealed class NotificationThrottleTests
{
    [Fact]
    public void TryAcquire_FirstCall_ReturnsTrue()
    {
        var fakeTime = new FakeTimeProvider();
        var throttle = new NotificationThrottle(fakeTime);

        var acquired = throttle.TryAcquire();

        acquired.ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_WithinTenSeconds_ReturnsFalse()
    {
        var fakeTime = new FakeTimeProvider();
        var throttle = new NotificationThrottle(fakeTime);

        throttle.TryAcquire().ShouldBeTrue();

        fakeTime.Advance(TimeSpan.FromSeconds(5));
        throttle.TryAcquire().ShouldBeFalse();

        fakeTime.Advance(TimeSpan.FromSeconds(4.9));
        throttle.TryAcquire().ShouldBeFalse();
    }

    [Fact]
    public void TryAcquire_AfterTenSeconds_ReturnsTrue()
    {
        var fakeTime = new FakeTimeProvider();
        var throttle = new NotificationThrottle(fakeTime);

        throttle.TryAcquire().ShouldBeTrue();

        fakeTime.Advance(TimeSpan.FromSeconds(9.999));
        throttle.TryAcquire().ShouldBeFalse();

        fakeTime.Advance(TimeSpan.FromMilliseconds(1));
        throttle.TryAcquire().ShouldBeTrue();
    }
}
