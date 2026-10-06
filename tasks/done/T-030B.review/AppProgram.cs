int checks=0,failures=0;
PipelineScope.Check=(name,pass)=>{checks++;if(!pass)failures++;Console.WriteLine($"CHECK {name}={(pass?"PASS":"FAIL")}");};
foreach(var failed in new[]{false,true})try{await AppChecks.RunAsync(failed);}catch(Exception ex){failures++;Console.WriteLine($"APP_GROUP_FAILURE {failed} {ex}");}
Console.WriteLine($"OCR_PIPELINE_APP_REVIEW checks={checks} failures={failures} userContent=False");return failures==0?0:1;
