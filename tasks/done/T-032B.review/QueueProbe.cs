using Iris.App.Settings;
using System.Diagnostics;
var timer=Stopwatch.StartNew();
using(var q=new SettingsDraftOperationQueue())
{
 using var gate=new ManualResetEventSlim();
 var task=q.EnqueueAsync(async ct=>{gate.Wait(TimeSpan.FromMilliseconds(150),ct);await Task.Yield();});
 Console.WriteLine($"Original synchronous gate: Enqueue returned only after timeout; elapsedMs={timer.ElapsedMilliseconds}");
 await task;await q.DrainAndCancelAllAsync();
}
for(int i=0;i<20;i++)
{
 using var q=new SettingsDraftOperationQueue();
 var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 bool second=false;Task? first=null,waiting=null,drain=null;
 try
 {
  first=q.EnqueueAsync(async ct=>{entered.TrySetResult();await release.Task;});
  await entered.Task;
  waiting=q.EnqueueAsync(ct=>{second=true;return Task.CompletedTask;});
  drain=q.DrainAndCancelAllAsync();
  if(drain.IsCompleted || first.IsCompleted || second)throw new Exception("Drain crossed actual running work");
  release.TrySetResult();await first;await drain;
  try{await waiting;throw new Exception("Waiting operation was not canceled");}catch(OperationCanceledException){}
  if(second || q.HasActiveWork)throw new Exception("Drain retained/executed waiting work");
 }
 finally
 {
  release.TrySetResult();
  foreach(var task in new[]{first,waiting,drain})if(task!=null)try{await task;}catch(OperationCanceledException){}
  await q.DrainAndCancelAllAsync();
 }
}
Console.WriteLine("PASS actual queue deterministic drain: 20 iterations, blocked until release, waiting canceled, no active work");
