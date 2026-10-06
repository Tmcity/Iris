using System.Text.Json;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;

static class BehaviorChecks
{
    internal static async Task ErrorTableAsync()
    {
        var allowed=new HashSet<ErrorCode>{ErrorCode.NotConfigured,ErrorCode.NetworkUnavailable,ErrorCode.Timeout,ErrorCode.ProviderUnavailable,ErrorCode.QuotaExceeded};
        foreach(var phase in new[]{"constraints","prepare","recognize"})
        foreach(var code in Enum.GetValues<ErrorCode>())
        {
            await using var scope=new PipelineScope();var error=new IrisException(code,"controlled",providerId:"main");
            if(phase=="constraints")scope.Main.ConstraintsError=error;
            else if(phase=="prepare")scope.Preprocessor.Body=(image,limits,token)=>ReferenceEquals(limits,scope.Main.Limits)?Task.FromException<ImageData>(error):Task.FromResult(image);
            else scope.Main.Body=(request,token)=>Task.FromException<OcrResult>(error);
            OcrOutcome? outcome=null;Exception? actual=null;try{outcome=await scope.Run().WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception ex){actual=ex;}
            bool expected=allowed.Contains(code);
            PipelineScope.Check($"ERROR_TABLE_{phase}_{code}",expected ? actual==null && outcome is{UsedFallback:true,Text:"backup"} && scope.Backup.Requests.Count==1 : actual is IrisException iris && iris.Code==code && scope.Backup.Requests.IsEmpty);
            if(scope.Registry.Issued.Count!=1 || scope.Registry.Issued.Single().Releases!=1)throw new InvalidOperationException("One operation must release exactly one snapshot.");
        }
    }
    internal static async Task OriginalImageAsync()
    {
        await using var scope=new PipelineScope();scope.Main.Limits=scope.Main.Limits with{MaxLongSide=500};scope.Backup.Limits=scope.Backup.Limits with{MaxLongSide=900};
        scope.Main.Body=(request,token)=>Task.FromException<OcrResult>(new IrisException(ErrorCode.Timeout,"controlled"));
        var result=await scope.Run();var calls=scope.Preprocessor.Calls.ToArray();
        PipelineScope.Check("BOTH_PROVIDERS_PREPROCESS_ORIGINAL_IMAGE",calls.Length==2 && calls.All(c=>ReferenceEquals(c.Image,scope.Original)) && calls[0].Constraints.MaxLongSide==500 && calls[1].Constraints.MaxLongSide==900);
        PipelineScope.Check("RAW_AND_LANGUAGE_PRESERVED",result.Raw.ProviderId=="backup" && result.Raw.Elapsed==TimeSpan.FromMilliseconds(17) && scope.Main.Requests.Single().LanguageHint=="ja" && scope.Backup.Requests.Single().LanguageHint=="ja");
    }
    internal static void NormalizerVectors()
    {
        var normalizer=PipelineScope.CreateNormalizer();
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"normalizer-vectors.json")));
        foreach(var row in doc.RootElement.EnumerateArray())
        {
            var lines=row.GetProperty("lines").EnumerateArray().Select(x=>
            {
                var b=x.GetProperty("bounds");PixelRect? bounds=null;
                if(b.ValueKind!=JsonValueKind.Null){var a=b.EnumerateArray().Select(v=>v.GetInt32()).ToArray();bounds=new(a[0],a[1],a[2],a[3]);}
                return new OcrLine(x.GetProperty("text").GetString()!,bounds);
            }).ToArray();
            var result=normalizer.Normalize(lines,Enum.Parse<OcrTextLayout>(row.GetProperty("layout").GetString()!));
            PipelineScope.Check("NORMALIZER_"+row.GetProperty("name").GetString(),result==row.GetProperty("expected").GetString());
        }
    }
}
