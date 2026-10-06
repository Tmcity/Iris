using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Iris.Core.DependencyInjection;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Providers.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

static class BaiduRegistryChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    internal static async Task RunAsync()
    {
        var profile=new OcrProviderProfile{Id="actual-baidu",Kind=OcrProviderKind.BaiduCloud,DisplayName="baidu",BaiduOptions=new()};
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{profile}),ActiveProviderId=profile.Id,FallbackProviderId=null}});
        var secrets=new ReviewSecrets();secrets.Set(SecretKeys.ApiKey(profile.Id),"FAKE_API");secrets.Set(SecretKeys.SecretKey(profile.Id),"FAKE_SECRET");
        using var transport=new Transport();var services=new ServiceCollection();
        services.AddSingleton<ISettingsService>(settings);services.AddSingleton<ISecretStore>(secrets);services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));
        services.AddIrisCore();services.AddIrisProviders();services.AddSingleton<IHttpClientFactory>(transport);
        await using var container=services.BuildServiceProvider();var registry=container.GetRequiredService<IOcrProviderRegistry>();
        IOcrProviderSnapshot? lease=null;
        try
        {
            lease=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            var provider=lease.Providers.Single().Provider;
            RegistryChecks.Check("BAIDU_FACTORY_RESOLVES_REAL_PROVIDER",provider.GetType().FullName=="Iris.Providers.Ocr.Baidu.BaiduOcrProvider");
            var result=await provider.TestAsync(CancellationToken.None).WaitAsync(Limit);
            RegistryChecks.Check("REAL_BAIDU_CLEANUP_FAILURE_RETAINS_RESPONSE_STREAM",!result.Success && transport.Requests==1 && transport.Body!=null && !transport.Body.Released);
            await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(Array.Empty<OcrProviderProfile>())}});
            await registry.RefreshAsync().WaitAsync(Limit);
            Exception? failure=null;try{await lease.DisposeAsync();}catch(Exception ex){failure=ex;}
            RegistryChecks.Check("REGISTRY_REPORTS_REAL_BAIDU_RETIREMENT_FAILURE",failure!=null && !transport.Body!.Released);
            transport.Body!.AllowRelease=true;await registry.RetryCleanupAsync().WaitAsync(Limit);await lease.DisposeAsync();lease=null;
            RegistryChecks.Check("REGISTRY_RETRY_RELEASES_ORIGINAL_BAIDU_STREAM",transport.Requests==1 && transport.Body.Released && transport.Body.ReleaseAttempts>=2);
        }
        finally
        {
            if(transport.Body!=null)transport.Body.AllowRelease=true;
            var failures=new List<Exception>();
            if(lease!=null)try{await lease.DisposeAsync();}catch(Exception ex){failures.Add(ex);}
            try{await registry.RetryCleanupAsync().WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
            try{await registry.StopAsync().WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
            if(failures.Count!=0)throw new AggregateException("Actual Baidu cleanup failed.",failures);
        }
    }
    sealed class Transport : HttpMessageHandler,IHttpClientFactory
    {
        internal int Requests;
        internal FaultStream? Body;
        public HttpClient CreateClient(string name)=>new(this,false){Timeout=Timeout.InfiniteTimeSpan};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Interlocked.Increment(ref Requests);
            Body=new FaultStream(Encoding.UTF8.GetBytes("{\"access_token\":\"FAKE_TOKEN\",\"expires_in\":3600}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(Body)});
        }
        protected override void Dispose(bool disposing){if(disposing && Body!=null){Body.AllowRelease=true;Body.Dispose();}base.Dispose(disposing);}
    }
    sealed class FaultStream(byte[] bytes):MemoryStream(bytes,false)
    {
        internal bool AllowRelease,Released;
        internal int ReleaseAttempts;
        protected override void Dispose(bool disposing)
        {
            if(disposing){Interlocked.Increment(ref ReleaseAttempts);if(!AllowRelease)throw new IOException("controlled response stream release");Released=true;}
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
}
