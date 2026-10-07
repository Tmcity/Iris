using System.Reflection;
var stream=new GateBody();var lease=new Lease();var inner=new StreamContent(stream);
var type=typeof(CaptureHandler).GetNestedType("LeaseContent",BindingFlags.NonPublic)!;
using var wrapper=(HttpContent)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[inner,lease],null)!;
Task<Stream>? read=null;
try
{
 read=wrapper.ReadAsStreamAsync();
 var entered=stream.ReadReachedGate.Task;
 await Task.WhenAny(read,entered).WaitAsync(TimeSpan.FromSeconds(5));
 bool buffering=entered.IsCompleted && !read.IsCompleted;
 Console.WriteLine($"Prototype response wrapper: ReadAsStreamCompletedBeforeEof={read.IsCompleted}; UnderlyingReadReachedEofGate={entered.IsCompleted}; BuffersUntilEof={buffering}");
 if(!buffering)throw new Exception("Expected buffering behavior did not reproduce");
}
finally
{
 stream.Release.TrySetResult();
 if(read!=null){using var returned=await read;}
 wrapper.Dispose();
}
Console.WriteLine($"CLEANUP leaseReturned={lease.Returned}");
var comparisonBody=new GateBody();var comparisonLease=new Lease();
using(var comparison=new ForwardingComparison(new StreamContent(comparisonBody),comparisonLease))
{
 Stream? borrowed=null;
 try
 {
  var task=comparison.ReadAsStreamAsync(CancellationToken.None);
  if(!task.IsCompletedSuccessfully || comparisonBody.ReadReachedGate.Task.IsCompleted)throw new Exception("Forwarding unexpectedly read the body before returning stream");
  borrowed=await task;var first=new byte[1];int count=await borrowed.ReadAsync(first);
  if(count!=1 || first[0]!=65 || comparisonBody.ReadReachedGate.Task.IsCompleted || comparisonLease.Returned)throw new Exception("First byte / lease ownership incorrect");
  Console.WriteLine("PASS comparison: stream and first byte available before EOF; response still owns lease");
 }
 finally{comparisonBody.Release.TrySetResult();borrowed?.Dispose();}
}
if(!comparisonLease.Returned)throw new Exception("Comparison response did not return lease");
Console.WriteLine("PASS comparison: response disposal returns lease; no product/prototype source changed");
sealed class Lease:IDisposable{internal bool Returned;public void Dispose()=>Returned=true;}
sealed class GateBody:Stream
{
 private bool sent;
 internal TaskCompletionSource ReadReachedGate=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public override bool CanRead=>true;public override bool CanWrite=>false;public override bool CanSeek=>false;
 public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
 public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
 {
  if(!sent){sent=true;buffer.Span[0]=65;return 1;}
  ReadReachedGate.TrySetResult();await Release.Task.WaitAsync(token);return 0;
 }
 public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
 public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
 public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
 public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();
}
internal static class Check{internal static void That(bool v,string m){if(!v)throw new Exception(m);}internal static void Pass(string m)=>Console.WriteLine("PASS "+m);}

// Independent comparison only: response content retains lease until response disposal.
sealed class ForwardingComparison(HttpContent inner,IDisposable lease):HttpContent
{
 protected override Task<Stream> CreateContentReadStreamAsync()=>inner.ReadAsStreamAsync();
 protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)=>inner.ReadAsStreamAsync(ct);
 protected override Stream CreateContentReadStream(CancellationToken ct)=>inner.ReadAsStream(ct);
 protected override Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext? context)=>inner.CopyToAsync(stream);
 protected override Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext? context,CancellationToken ct)=>inner.CopyToAsync(stream,ct);
 protected override bool TryComputeLength(out long length){length=0;return false;}
 protected override void Dispose(bool disposing){if(disposing){inner.Dispose();lease.Dispose();}base.Dispose(disposing);}
}
