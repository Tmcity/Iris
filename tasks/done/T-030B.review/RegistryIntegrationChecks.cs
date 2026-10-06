using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

static class RegistryIntegrationChecks
{
    internal static async Task RunAsync()
    {
        static OcrProviderProfile Profile(string id,string name)=>new(){Id=id,Kind=OcrProviderKind.WindowsLocal,DisplayName=name,WindowsOptions=new()};
        var main=Profile("main","main");var backup=Profile("backup","old-backup");
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{main,backup}),ActiveProviderId="main",FallbackProviderId="backup",LanguageHint="ja"}});
        var factory=new Factory();var registry=new OcrProviderRegistry(settings,new ReviewSecrets(),new[]{factory},NullLogger<OcrProviderRegistry>.Instance);
        var images=new PipelinePreprocessor();var service=new OcrService(registry,images,new OcrTextNormalizer());
        Task<OcrOutcome>? first=null;
        try
        {
            first=service.RecognizeAsync(new(new byte[]{1},Iris.Core.Imaging.ImageFormat.Png,40,40));
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(new[]{main,backup with{DisplayName="new-backup"}}),ActiveProviderId="backup",LanguageHint="ko"}});
            await registry.RefreshAsync();factory.Release.TrySetResult();var old=await first;
            PipelineScope.Check("ACTUAL_REGISTRY_FALLBACK_USES_ORIGINAL_GENERATION",old.UsedFallback && old.Text=="old-backup:ja");
            var next=await service.RecognizeAsync(new(new byte[]{2},Iris.Core.Imaging.ImageFormat.Png,40,40));
            PipelineScope.Check("NEXT_OPERATION_USES_NEW_REGISTRY_GENERATION",!next.UsedFallback && next.Text=="new-backup:ko");
        }
        finally
        {
            factory.Release.TrySetResult();if(first!=null)try{await first;}catch(Exception){}
            var errors=new List<Exception>();
            try{await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception ex){errors.Add(ex);}
            try{await registry.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception ex){errors.Add(ex);}
            if(errors.Count!=0)throw new AggregateException("Registry integration cleanup failed.",errors);
        }
    }
    sealed class Factory:IOcrProviderFactory
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OcrProviderKind Kind=>OcrProviderKind.WindowsLocal;
        public IOcrProvider Create(OcrProviderProfile profile,ProviderCredentials credentials)=>new PipelineProvider(profile.Id)
        {
            Body=async(request,token)=>
            {
                if(profile.Id=="main"){Entered.TrySetResult();await Release.Task;throw new IrisException(ErrorCode.Timeout,"controlled");}
                return new OcrResult(profile.Id,new[]{new OcrLine(profile.DisplayName+":"+request.LanguageHint)},null,TimeSpan.Zero);
            }
        };
    }
}
