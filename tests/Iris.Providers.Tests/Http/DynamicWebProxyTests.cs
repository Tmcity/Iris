using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Iris.Providers.Tests.Http;

// The mutable IWebProxy was replaced. Keep its address-classification assertions against actual Core capture.
public sealed class DynamicWebProxyTests
{
    private sealed class Scope : IDisposable
    {
        internal readonly ISettingsService Settings = Substitute.For<ISettingsService>();
        private readonly ServiceProvider _services;
        internal INetworkProxySnapshotSource Source { get; }
        internal Scope(NetworkSettings settings)
        {
            Set(settings);
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Settings);
            services.AddSingleton(Substitute.For<ISecretStore>()); services.AddSingleton(Substitute.For<ITranslationProviderRegistry>());
            services.AddSingleton(Substitute.For<IOcrProviderRegistry>()); services.AddIrisCore();
            _services = services.BuildServiceProvider(); Source = _services.GetRequiredService<INetworkProxySnapshotSource>();
        }
        internal void Set(NetworkSettings network) => Settings.Current.Returns(new AppSettings { Network = network });
        public void Dispose() => _services.Dispose();
    }
    private static NetworkSettings Custom(bool bypass = true) => new() { ProxyMode = ProxyMode.Custom, CustomProxyUrl = "http://127.0.0.1:7890", BypassProxyOnLocal = bypass };

    [Fact]
    public async Task ModeNone_ReturnsDirectSnapshot()
    {
        using var scope = new Scope(new() { ProxyMode = ProxyMode.None });
        var value = await scope.Source.CaptureCurrentAsync(new("https://origin.example.test/"), TestContext.Current.CancellationToken);
        value.Mode.ShouldBe(ProxyMode.None); value.CustomProxyUri.ShouldBeNull(); value.RouteKind.ShouldBe(ProxyRouteKind.DirectByMode);
    }

    [Fact]
    public async Task ModeCustom_PublicHost_ReturnsProxyUri()
    {
        using var scope = new Scope(Custom());
        var value = await scope.Source.CaptureCurrentAsync(new("https://origin.example.test/"), TestContext.Current.CancellationToken);
        value.Mode.ShouldBe(ProxyMode.Custom); value.CustomProxyUri.ShouldBe(new Uri("http://127.0.0.1:7890/"));
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
    [InlineData("http://[::ffff:10.1.2.3]/")]
    [InlineData("http://[::ffff:192.168.1.1]/")]
    [InlineData("http://localhost./")]
    [InlineData("http://intranet./")]
    public async Task ModeCustom_BypassProxyOnLocalTrue_LocalAddressesAreBypassed(string input)
    {
        using var scope = new Scope(Custom());
        var value = await scope.Source.CaptureCurrentAsync(new(input), TestContext.Current.CancellationToken);
        value.Mode.ShouldBe(ProxyMode.None); value.CustomProxyUri.ShouldBeNull(); value.RouteKind.ShouldBe(ProxyRouteKind.DirectByLocalBypass);
    }

    [Theory]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("https://api.openai.com/")]
    [InlineData("http://[::ffff:8.8.8.8]/")]
    public async Task ModeCustom_BypassProxyOnLocalTrue_NonLocalAddressesGoThroughProxy(string input)
    {
        using var scope = new Scope(Custom());
        var value = await scope.Source.CaptureCurrentAsync(new(input), TestContext.Current.CancellationToken);
        value.Mode.ShouldBe(ProxyMode.Custom); value.CustomProxyUri.ShouldBe(new Uri("http://127.0.0.1:7890/"));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://192.168.1.5/")]
    public async Task ModeCustom_BypassProxyOnLocalFalse_LocalAddressesGoThroughProxy(string input)
    {
        using var scope = new Scope(Custom(false));
        var value = await scope.Source.CaptureCurrentAsync(new(input), TestContext.Current.CancellationToken);
        value.Mode.ShouldBe(ProxyMode.Custom); value.CustomProxyUri.ShouldBe(new Uri("http://127.0.0.1:7890/"));
    }

    [Fact]
    public async Task SettingsChangedBetweenCaptures_ImmediatelyTakesEffectWhileOldSnapshotRemainsStable()
    {
        using var scope = new Scope(new() { ProxyMode = ProxyMode.None }); var destination = new Uri("https://origin.example.test/");
        var first = await scope.Source.CaptureCurrentAsync(destination, TestContext.Current.CancellationToken);
        scope.Set(Custom()); var second = await scope.Source.CaptureCurrentAsync(destination, TestContext.Current.CancellationToken);
        first.Mode.ShouldBe(ProxyMode.None); first.CustomProxyUri.ShouldBeNull();
        second.Mode.ShouldBe(ProxyMode.Custom); second.GenerationId.ShouldNotBe(first.GenerationId);
    }

    [Fact]
    public async Task ModeCustom_InvalidProxyAddress_FailsClosedWithoutEcho()
    {
        using var scope = new Scope(Custom() with { CustomProxyUrl = "not-a-valid-proxy-uri-private" });
        for (int i = 0; i < 2; i++)
        {
            var error = await Should.ThrowAsync<IrisException>(() => scope.Source.CaptureCurrentAsync(new("https://origin.example.test/"), TestContext.Current.CancellationToken));
            error.Code.ShouldBe(ErrorCode.InvalidArgument); error.ToString().ShouldNotContain("not-a-valid-proxy-uri-private");
        }
    }

    [Fact]
    public async Task ModeSystem_LocalhostIsBypassed_WhenBypassProxyOnLocalIsTrue()
    {
        using var scope = new Scope(new() { ProxyMode = ProxyMode.System });
        var value = await scope.Source.CaptureCurrentAsync(new("http://localhost/"), TestContext.Current.CancellationToken);
        value.Mode.ShouldBe(ProxyMode.None); value.CustomProxyUri.ShouldBeNull();
    }

    [Fact]
    public void FrozenProxy_RejectsCredentialMutation()
    {
        var route = new NetworkRouteResolver().Resolve(new(Guid.NewGuid(), new("https://origin.example.test/"), ProxyMode.Custom,
            new("http://proxy.example.test/"), new("synthetic", "synthetic"), ProxyRouteKind.Proxy));
        Should.Throw<NotSupportedException>(() => route.CreateProxy()!.Credentials = System.Net.CredentialCache.DefaultCredentials);
    }
}
