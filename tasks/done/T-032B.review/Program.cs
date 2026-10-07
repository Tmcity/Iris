if(args.Contains("--reachability"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Reachability(sta);
}
else if(args.Contains("--retry-save"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.RetrySaveBarrier(sta);
}
else if(args.Contains("--revisions"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Revisions(sta);
}
else if(args.Contains("--retry-barrier"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.RetryBarrier(sta);
}
else if(args.Contains("--identity"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Identity(sta);
}
else if(args.Contains("--profiles"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Profiles(sta);
}
else if(args.Contains("--render"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Render(sta);
}
else if(args.Contains("--receipts"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Receipts(sta);
}
else if(args.Contains("--controls"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Controls(sta);
}
else if(args.Contains("--workflow"))
{
    await using var sta=new ReviewSta();
    await WorkflowChecks.Run(sta);
}
else if(args.Contains("--infrastructure"))
{
    await using var sta=new ReviewSta();
    ReviewScope? scope=null;
    Exception? bodyError=null;
    try
    {
        scope=await sta.Run(()=>new ReviewScope(sta));
        await sta.Run(()=>
        {
            scope.Show();
            if(!scope.Session.IsOpen)throw new Exception("Owned window did not open");
            if(scope.Settings.Updates!=0 || scope.Secrets.Gets!=0 || scope.Secrets.Sets!=0 || scope.Secrets.Removes!=0)
                throw new Exception("Opening settings caused storage side effects");
            if(!scope.Cloud.Created.IsEmpty || !scope.Local.Created.IsEmpty)
                throw new Exception("Opening settings created a provider");
            return Task.CompletedTask;
        });
    }
    catch(Exception ex){bodyError=ex;}
    Exception? cleanupError=null;
    if(scope!=null)try{await scope.DisposeAsync();}catch(Exception ex){cleanupError=ex;}
    if(bodyError!=null && cleanupError!=null)throw new AggregateException(bodyError,cleanupError);
    if(bodyError!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bodyError).Throw();
    if(cleanupError!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupError).Throw();
    if(scope!.Session.IsOpen)throw new Exception("Owned settings window did not finish shutdown");
    Console.WriteLine("PASS infrastructure: real DI, owned settings window, no provider/storage side effects, full shutdown.");
    Console.WriteLine("Preparation only; OCR/screenshot UI requirements are not yet tested.");
}
else Console.WriteLine("Use --infrastructure for preparation smoke. No UI acceptance checks executed.");
