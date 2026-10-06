string directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..",".."));
int checks=0,skips=0;
var failures=new List<Exception>();
async Task Check(string name,Func<Task> action)
{
    checks++;
    try{await action();Console.WriteLine($"CHECK {name}=PASS");}
    catch(ReviewEnvironmentSkip ex){skips++;Console.WriteLine($"CHECK {name}=ENVIRONMENT_SKIP {ex.Message}");}
    catch(Exception ex){failures.Add(ex);Console.WriteLine($"CHECK {name}=FAIL {ex}");}
}
try
{
    await BehaviorChecks.Run(directory,Check);
    await LifecycleChecks.Run(directory,Check);
    await ResultChecks.Run(directory,Check);
    await SdkChecks.Run(directory,Check);
}
catch(Exception ex){failures.Add(ex);Console.WriteLine($"PROBE_INFRASTRUCTURE_FAILURE {ex}");}
Console.WriteLine($"WINDOWS_OCR_REVIEW checks={checks} failures={failures.Count} environmentSkips={skips} userContent=False");
Environment.ExitCode=failures.Count==0?0:2;
