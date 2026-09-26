using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using Iris.Core.Settings;
using Iris.Providers.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Http;

public sealed class DynamicWebProxyTests
{
    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly TestLogger<DynamicWebProxy> _logger = new();

    private void SetNetworkSettings(NetworkSettings network)
    {
        _settings.Current.Returns(new AppSettings
        {
            Network = network,
        });
    }

    [Fact]
    public void ModeNone_GetProxyReturnsNullAndIsBypassedReturnsTrue()
    {
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.None,
        });

        var proxy = new DynamicWebProxy(_settings, _logger);
        var targetUri = new Uri("https://api.openai.com/v1/chat/completions");

        proxy.IsBypassed(targetUri).ShouldBeTrue();
        proxy.GetProxy(targetUri).ShouldBeNull();
    }

    [Fact]
    public void ModeCustom_PublicHost_ReturnsProxyUri()
    {
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.Custom,
            CustomProxyUrl = "http://127.0.0.1:7890",
            BypassProxyOnLocal = true,
        });

        var proxy = new DynamicWebProxy(_settings, _logger);
        var targetUri = new Uri("https://api.openai.com/v1/chat/completions");

        proxy.IsBypassed(targetUri).ShouldBeFalse();
        proxy.GetProxy(targetUri).ShouldBe(new Uri("http://127.0.0.1:7890"));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.5:8080/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://172.20.0.1/")]
    [InlineData("http://192.168.1.5/")]
    [InlineData("http://169.254.1.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://intranet/")]
    [InlineData("http://printer.local/")]
    public void ModeCustom_BypassProxyOnLocalTrue_LocalAddressesAreBypassed(string uriString)
    {
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.Custom,
            CustomProxyUrl = "http://127.0.0.1:7890",
            BypassProxyOnLocal = true,
        });

        var proxy = new DynamicWebProxy(_settings, _logger);
        var uri = new Uri(uriString);

        proxy.IsBypassed(uri).ShouldBeTrue();
        proxy.GetProxy(uri).ShouldBeNull();
    }

    [Theory]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("https://api.openai.com/")]
    public void ModeCustom_BypassProxyOnLocalTrue_NonLocalAddressesGoThroughProxy(string uriString)
    {
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.Custom,
            CustomProxyUrl = "http://127.0.0.1:7890",
            BypassProxyOnLocal = true,
        });

        var proxy = new DynamicWebProxy(_settings, _logger);
        var uri = new Uri(uriString);

        proxy.IsBypassed(uri).ShouldBeFalse();
        proxy.GetProxy(uri).ShouldBe(new Uri("http://127.0.0.1:7890"));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://192.168.1.5/")]
    public void ModeCustom_BypassProxyOnLocalFalse_LocalAddressesGoThroughProxy(string uriString)
    {
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.Custom,
            CustomProxyUrl = "http://127.0.0.1:7890",
            BypassProxyOnLocal = false,
        });

        var proxy = new DynamicWebProxy(_settings, _logger);
        var uri = new Uri(uriString);

        proxy.IsBypassed(uri).ShouldBeFalse();
        proxy.GetProxy(uri).ShouldBe(new Uri("http://127.0.0.1:7890"));
    }

    [Fact]
    public void SettingsChangedBetweenCalls_ImmediatelyTakesEffect()
    {
        var proxy = new DynamicWebProxy(_settings, _logger);
        var targetUri = new Uri("https://api.openai.com/");

        // Call 1: None
        SetNetworkSettings(new NetworkSettings { ProxyMode = ProxyMode.None });
        proxy.IsBypassed(targetUri).ShouldBeTrue();
        proxy.GetProxy(targetUri).ShouldBeNull();

        // Call 2: Custom
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.Custom,
            CustomProxyUrl = "http://127.0.0.1:8888",
        });
        proxy.IsBypassed(targetUri).ShouldBeFalse();
        proxy.GetProxy(targetUri).ShouldBe(new Uri("http://127.0.0.1:8888"));
    }

    [Fact]
    public void ModeCustom_InvalidProxyAddress_ReturnsNullAndLogsWarningOnceWithoutAddress()
    {
        const string badUrl = "not-a-valid-proxy-uri-123456";
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.Custom,
            CustomProxyUrl = badUrl,
        });

        var proxy = new DynamicWebProxy(_settings, _logger);
        var targetUri = new Uri("https://api.openai.com/");

        var proxy1 = proxy.GetProxy(targetUri);
        var proxy2 = proxy.GetProxy(targetUri);

        proxy1.ShouldBeNull();
        proxy2.ShouldBeNull();

        _logger.Logs.Count.ShouldBe(1);
        _logger.Logs[0].Level.ShouldBe(LogLevel.Warning);
        _logger.Logs[0].Message.ShouldNotContain(badUrl);
    }

    [Fact]
    public void ModeSystem_LocalhostIsBypassed_WhenBypassProxyOnLocalIsTrue()
    {
        SetNetworkSettings(new NetworkSettings
        {
            ProxyMode = ProxyMode.System,
            BypassProxyOnLocal = true,
        });

        var proxy = new DynamicWebProxy(_settings, _logger);
        var uri = new Uri("http://localhost/");

        proxy.IsBypassed(uri).ShouldBeTrue();
        proxy.GetProxy(uri).ShouldBeNull();
    }

    [Fact]
    public void SettingCredentials_ThrowsNotSupportedException()
    {
        var proxy = new DynamicWebProxy(_settings, _logger);

        Should.Throw<NotSupportedException>(() => proxy.Credentials = CredentialCache.DefaultCredentials);
    }
}
