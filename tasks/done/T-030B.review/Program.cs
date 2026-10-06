using Iris.Core.Ocr;

int checks=0,failures=0;
PipelineScope.Create=(registry,preprocessor,normalizer)=>new OcrService(registry,preprocessor,normalizer);
PipelineScope.CreateNormalizer=()=>new OcrTextNormalizer();
PipelineScope.Check=(name,pass)=>{checks++;if(!pass)failures++;Console.WriteLine($"CHECK {name}={(pass?"PASS":"FAIL")}");};
var groups=new List<(string Name,Func<Task> Run)>
{
    ("normalizer",()=>{BehaviorChecks.NormalizerVectors();return Task.CompletedTask;}),
    ("errors",BehaviorChecks.ErrorTableAsync),
    ("original-image",BehaviorChecks.OriginalImageAsync),
    ("snapshot-retry",LifecycleChecks.RetryOriginalSnapshotAsync),
    ("callback-failure",LifecycleChecks.ThrowingCancellationCallbackAsync),
    ("ownership",OwnershipChecks.RunAsync),
    ("actual-registry",RegistryIntegrationChecks.RunAsync),
    ("selection",SelectionChecks.RunAsync),
};
foreach(var stage in new[]{"prepare","recognize"})foreach(var stop in new[]{false,true})
    groups.Add(($"held-{stage}-{stop}",()=>LifecycleChecks.HeldStageAsync(stage,stop)));
foreach(var group in groups)
{
    try{await group.Run();}catch(Exception ex){failures++;Console.WriteLine($"GROUP_FAILURE {group.Name} {ex}");}
}
Console.WriteLine($"OCR_PIPELINE_REVIEW checks={checks} failures={failures} userContent=False");return failures==0?0:1;
