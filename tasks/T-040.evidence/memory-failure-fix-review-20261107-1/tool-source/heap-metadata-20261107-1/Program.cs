using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Owned T-040 Sandbox only.");
if(args.Length!=1||!int.TryParse(args[0],NumberStyles.None,CultureInfo.InvariantCulture,out int pid))throw new ArgumentException("Expected owned Iris PID.");
using var process=Process.GetProcessById(pid);
string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Iris","current","Iris.App.exe");
if(!string.Equals(process.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected target.");
string root=@"C:\IrisT040Output";
if(!Directory.Exists(root))throw new InvalidOperationException("Missing owned output mapping.");
using var target=DataTarget.AttachToProcess(pid,suspend:false);
string dac=@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.14\mscordaccore.dll";
using var runtime=target.ClrVersions.Single().CreateRuntime(dac);
var histogram=new Dictionary<string,(long Count,ulong Bytes)>(StringComparer.Ordinal);
long invalid=0;
var dependencyPropertyFields=new List<object>();
var dependencyPropertyNames=new List<object>();
foreach(var obj in runtime.Heap.EnumerateObjects())
{
    if(!obj.IsValid||obj.Type is null){invalid++;continue;}
    string name=obj.Type.Name??"<unknown>";
    if(name=="System.Windows.DependencyProperty"&&dependencyPropertyFields.Count==0)
        dependencyPropertyFields.AddRange(obj.Type.Fields.Select(f=>new{Name=f.Name,Type=f.Type?.Name,Element=f.ElementType.ToString()}));
    if(name=="System.Windows.DependencyProperty"&&(obj.ReadField<int>("_packedData")&0xffff)==337)
        dependencyPropertyNames.Add(new{Index=337,CodePropertyName=obj.ReadStringField("_name")});
    var item=histogram.GetValueOrDefault(name);
    histogram[name]=(item.Count+1,item.Bytes+obj.Size);
}
var renderFields=new List<object>();
var reached=new HashSet<ulong>();
var parents=new Dictionary<ulong,ulong>();
var targetObjects=new List<(string Type,ulong Address)>();
var queue=new Queue<ClrObject>();
foreach(var rootObject in runtime.Heap.EnumerateRoots())
    if(rootObject.Object.IsValid&&reached.Add(rootObject.Object.Address)){queue.Enqueue(rootObject.Object);parents[rootObject.Object.Address]=0;}
var reachableHistogram=new Dictionary<string,(long Count,ulong Bytes)>(StringComparer.Ordinal);
long rootGraphErrors=0;
while(queue.TryDequeue(out var obj))
{
    try
    {
        if(obj.Type is null)continue;
        string name=obj.Type.Name??"<unknown>";
        if(name=="Iris.App.Settings.SettingsWindow"||name=="Iris.App.History.HistoryWindow")targetObjects.Add((name,obj.Address));
        var item=reachableHistogram.GetValueOrDefault(name);
        reachableHistogram[name]=(item.Count+1,item.Bytes+obj.Size);
        foreach(var child in obj.EnumerateReferences(carefully:true))
            if(child.IsValid&&reached.Add(child.Address)){queue.Enqueue(child);parents[child.Address]=obj.Address;}
    }
    catch(InvalidOperationException){rootGraphErrors++;}
}
var rootPaths=new List<object>();
var retainedPropertyEntries=new List<object>();
foreach(var targetObject in targetObjects)
{
    var referencePath=new List<object>();ulong address=targetObject.Address;
    for(int depth=0;depth<100&&address!=0;depth++)
    {
        var obj=runtime.Heap.GetObject(address);
        referencePath.Add(new{Address=address,Type=obj.Type?.Name});
        if(!parents.TryGetValue(address,out address))break;
    }
    rootPaths.Add(new{TargetType=targetObject.Type,TargetAddress=targetObject.Address,ReverseRootPath=referencePath});
    if(parents.TryGetValue(targetObject.Address,out ulong arrayAddress))
    {
        var array=runtime.Heap.GetObject(arrayAddress);
        if(array.IsArray&&array.Type?.Name=="System.Windows.EffectiveValueEntry[]")
        {
            var entries=array.AsArray();
            for(int i=0;i<entries.Length;i++)
            {
                var value=entries.GetStructValue(i);
                try
                {
                    var stored=value.ReadObjectField("_value");
                    if(stored.Address==targetObject.Address)
                        retainedPropertyEntries.Add(new{TargetType=targetObject.Type,Index=i,PropertyIndex=value.ReadField<ushort>("_propertyIndex"),Fields=value.Type?.Fields.Select(f=>f.Name).ToArray()});
                }
                catch(InvalidOperationException){ }
            }
        }
    }
}
foreach(var module in runtime.EnumerateModules())
{
    var type=module.GetTypeByName("System.Windows.Media.RenderCapability");
    if(type is null)continue;
    foreach(var field in type.StaticFields)
    {
        if(field.Name?.Contains("tier",StringComparison.OrdinalIgnoreCase)!=true)continue;
        foreach(var domain in runtime.AppDomains)
        {
            try{renderFields.Add(new{Name=field.Name,Value=field.Read<int>(domain)});}
            catch(Exception error){renderFields.Add(new{Name=field.Name,Failure=error.GetType().FullName});}
        }
    }
}
var data=new{Utc=DateTimeOffset.UtcNow,Pid=pid,ReadOnlyMetadata=true,TargetSuspended=false,GcRequested=false,
    MemoryContentsOrStringValuesExported=false,HeapCanWalk=runtime.Heap.CanWalkHeap,InvalidObjects=invalid,
    Objects=histogram.Values.Sum(x=>x.Count),ManagedObjectBytes=histogram.Values.Aggregate(0UL,(total,item)=>total+item.Bytes),
    Histogram=histogram.OrderByDescending(x=>x.Value.Bytes).Select(x=>new{Type=x.Key,x.Value.Count,x.Value.Bytes}).ToArray(),RenderCapabilityFields=renderFields,
    RootGraphErrors=rootGraphErrors,ReachableObjects=reached.Count,ReachableObjectBytes=reachableHistogram.Values.Aggregate(0UL,(total,item)=>total+item.Bytes),
    ReachableHistogram=reachableHistogram.OrderByDescending(x=>x.Value.Bytes).Select(x=>new{Type=x.Key,x.Value.Count,x.Value.Bytes}).ToArray(),ClosedWindowRootPaths=rootPaths,RetainedPropertyEntries=retainedPropertyEntries,DependencyPropertyFields=dependencyPropertyFields,CodePropertyNames=dependencyPropertyNames};
string path=Path.Combine(root,"heap-metadata-"+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ",CultureInfo.InvariantCulture)+".json");
await File.WriteAllTextAsync(path,JsonSerializer.Serialize(data)).ConfigureAwait(false);
