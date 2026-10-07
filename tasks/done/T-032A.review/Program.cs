if(args.Length>0 && args[0]=="commit") {await CommitChecks.Run();return;}
if(args.Length>0 && args[0]=="draft") {await DraftChecks.Run();return;}
if(args.Length>0 && args[0]=="credentials") {await CredentialChecks.Run();return;}
if(args.Length>0 && args[0]=="resources") {await ResourceChecks.Run();return;}
await using(var scope=new ReviewScope())
{
    if(scope.Settings.Reads!=0 || scope.Secrets.Gets!=0 || scope.Secrets.Sets!=0 || !scope.Cloud.Created.IsEmpty || !scope.Local.Created.IsEmpty)
        throw new InvalidOperationException("Constructing the review scope performed external work.");
    Console.WriteLine("HARNESS_ACTUAL_COORDINATOR_REGISTRY_CONSTRUCTION=PASS");
}
Console.WriteLine("HARNESS_DISPOSAL=PASS; T-032A new behavior not tested yet.");
