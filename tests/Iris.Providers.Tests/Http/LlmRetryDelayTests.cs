using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Iris.Providers.Http;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Http;

public sealed class LlmRetryDelayTests
{
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-09-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Compute_RetryAfterDeltaSeconds_UnderCap_ReturnsExactDelta()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));

        var delay = LlmRetryDelay.Compute(response, _timeProvider);

        delay.TotalSeconds.ShouldBe(2.0);
    }

    [Fact]
    public void Compute_RetryAfterDeltaSeconds_OverCap_ReturnsFiveSeconds()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));

        var delay = LlmRetryDelay.Compute(response, _timeProvider);

        delay.TotalSeconds.ShouldBe(5.0);
    }

    [Fact]
    public void Compute_RetryAfterDate_InThreeSeconds_ReturnsThreeSeconds()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var targetDate = _timeProvider.GetUtcNow().AddSeconds(3);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(targetDate);

        var delay = LlmRetryDelay.Compute(response, _timeProvider);

        delay.TotalSeconds.ShouldBe(3.0);
    }

    [Fact]
    public void Compute_RetryAfterDate_InThePast_ReturnsZero()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var targetDate = _timeProvider.GetUtcNow().AddSeconds(-10);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(targetDate);

        var delay = LlmRetryDelay.Compute(response, _timeProvider);

        delay.TotalSeconds.ShouldBe(0.0);
    }

    [Fact]
    public void Compute_NoRetryAfterHeader_ReturnsJitteredBetweenZeroPointEightAndOnePointTwo()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        for (var i = 0; i < 100; i++)
        {
            var delay = LlmRetryDelay.Compute(response, _timeProvider);
            delay.TotalSeconds.ShouldBeGreaterThanOrEqualTo(0.8);
            delay.TotalSeconds.ShouldBeLessThanOrEqualTo(1.2);
        }
    }

    [Fact]
    public void Compute_NullResponse_ReturnsJitteredBetweenZeroPointEightAndOnePointTwo()
    {
        for (var i = 0; i < 100; i++)
        {
            var delay = LlmRetryDelay.Compute(null, _timeProvider);
            delay.TotalSeconds.ShouldBeGreaterThanOrEqualTo(0.8);
            delay.TotalSeconds.ShouldBeLessThanOrEqualTo(1.2);
        }
    }
}
