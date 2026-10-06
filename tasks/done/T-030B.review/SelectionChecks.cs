using Iris.Core.Errors;
using Iris.Core.Ocr;

static class SelectionChecks
{
    internal static async Task RunAsync()
    {
        foreach(var name in new[]{"unconfigured-main","unknown-id","explicit-backup","case-sensitive-id","empty-lines","whitespace","missing-both","unconfigured-backup","same-profile","same-instance","same-reference","no-fallback"})
        {
            await using var scope=new PipelineScope();var make=scope.Registry.Factory;string? id=null;
            ErrorCode? expectedError=null;bool expectedFallback=false;int mainCalls=1,backupCalls=0;
            if(name is "unconfigured-main" or "unknown-id" or "case-sensitive-id")
            {
                mainCalls=0;backupCalls=1;expectedFallback=true;
                if(name=="unconfigured-main"){scope.Main.Configured=false;scope.Main.ConstraintsError=new InvalidOperationException("Must not read unconfigured constraints.");}
                else id=name=="unknown-id"?"missing":"MAIN";
            }
            if(name=="explicit-backup"){id="backup";mainCalls=0;backupCalls=1;}
            if(name is "empty-lines" or "whitespace")
            {
                expectedError=ErrorCode.OcrNoTextFound;
                scope.Main.Body=(request,token)=>Task.FromResult(new OcrResult("main",name=="empty-lines"?Array.Empty<OcrLine>():new[]{new OcrLine(" \r\n\t ")},null,TimeSpan.Zero));
            }
            if(name=="missing-both")
            {
                expectedError=ErrorCode.NotConfigured;mainCalls=0;
                scope.Registry.Factory=()=>{var s=make();s.Entries=Array.Empty<OcrProviderRegistration>();return s;};
            }
            if(name is "unconfigured-backup" or "same-profile" or "same-instance" or "same-reference" or "no-fallback")
            {
                expectedError=name=="unconfigured-backup"?ErrorCode.NotConfigured:ErrorCode.Timeout;
                scope.Main.Body=(request,token)=>Task.FromException<OcrResult>(new IrisException(ErrorCode.Timeout,"controlled",providerId:"main"));
                if(name=="unconfigured-backup")scope.Backup.Configured=false;
                scope.Registry.Factory=()=>
                {
                    var s=make();var a=s.Entries[0];var b=s.Entries[1];
                    if(name=="same-profile")s.Captured=s.Captured with{Ocr=s.Captured.Ocr with{FallbackProviderId="main"}};
                    if(name=="same-instance")s.Entries=new[]{a,b with{InstanceId=a.InstanceId}};
                    if(name=="same-reference")s.Entries=new[]{a,b with{Provider=a.Provider}};
                    if(name=="no-fallback")s.Captured=s.Captured with{Ocr=s.Captured.Ocr with{FallbackProviderId=null}};
                    return s;
                };
            }
            OcrOutcome? outcome=null;Exception? error=null;try{outcome=await scope.Run(id:id).WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception ex){error=ex;}
            bool behavior=expectedError is{} code?error is IrisException iris && iris.Code==code:error==null && outcome!=null && outcome.UsedFallback==expectedFallback;
            PipelineScope.Check("SELECTION_"+name,behavior && scope.Main.Requests.Count==mainCalls && scope.Backup.Requests.Count==backupCalls && scope.Registry.Issued.Single().Releases==1);
        }
    }
}
