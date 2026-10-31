using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using LookingIN.Compute.Contracts;
using Newtonsoft.Json;
namespace BazaarPlacementAI
{
 // Replace one service instance; no vendor method or file is rewritten.
 public sealed class NativeRouter:IDisposable
 {
  public static volatile bool LastSearchWasAI,AIActive;public static int ProgressEvaluated,DiscoveryBudget=2500;
  public static int NextComparisonMode;
  private object original,coordinator,proxy;private FieldInfo field;private MethodInfo[] methods;
  private GameAdapter adapter;private int budget,maximum;private Action<string> log;
  private volatile Compute compute;private Func<RuntimeCombatCatalogIdentity,Compute> createCompute;private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
  private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
  private readonly Dictionary<object,Entry> results=new Dictionary<object,Entry>();
  private sealed class Entry{public BoardInput Input;public int[] Starts;}
  public static NativeRouter Attach(GameAdapter adapter,string directory,int budget,int maximum,Action<string> log)
  {
   var r=new NativeRouter{adapter=adapter,budget=budget,maximum=maximum,log=log,coordinator=adapter.Coordinator};
   r.createCompute=expected=>new Compute(Path.Combine(directory,"model"),Path.Combine(Paths.PluginPath,"LookingIN","LookingIN.Compute.Worker.exe"),Path.Combine(Paths.BepInExRootPath,"logs","BazaarPlacementAI"),expected,log);
   var service=adapter.LookingInAssembly.GetType("C.T",true);var progress=adapter.LookingInAssembly.GetType("C.u",true);
   r.field=r.coordinator.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Single(f=>f.FieldType==service);
   r.original=r.field.GetValue(r.coordinator);if(r.original==null||!progress.IsInstanceOfType(r.original))throw new InvalidOperationException("Unsupported LookingIN service");
   r.proxy=r.CreateProxy(service,progress);r.field.SetValue(r.coordinator,r.proxy);
   log("Attached AI to LookingIN's existing search/progress/results. PIN searches forwarded unchanged.");return r;
  }
  public bool IsWarm {get{return compute!=null&&!lifetime.IsCancellationRequested;}}
  // Initialization and all inference share the same gate and resident instance.
  // No Unity API or live-board capture is used on the background thread.
  public Task Warmup(RuntimeCombatCatalogIdentity expected)
  {
   return Task.Run(async()=>
   {
    try
    {
     await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
     try
     {
      if(compute!=null)return;
      var timer=Stopwatch.StartNew();log("Background warmup started; no placement search.");
      EnsureCompute(expected);
      log("Background warmup ready: "+timer.ElapsedMilliseconds+" ms; resident worker PID "+compute.WorkerProcessId+". Reused until game exit.");
     }
     finally{gate.Release();}
    }
    catch(OperationCanceledException){}
    catch(Exception e){log("Background warmup unavailable; first search will retry: "+e.GetBaseException().Message);}
   });
  }
  private void EnsureCompute(RuntimeCombatCatalogIdentity expected)
  {
   lifetime.Token.ThrowIfCancellationRequested();if(compute!=null)return;
   var ready=createCompute(expected);
   if(lifetime.IsCancellationRequested){ready.Dispose();lifetime.Token.ThrowIfCancellationRequested();}
   compute=ready;
  }
  public object Invoke(int id,object[] args)
  {
   var method=methods[id];var p=method.GetParameters();
   if(method.ReturnType.IsGenericType&&method.ReturnType.GetGenericTypeDefinition()==typeof(Task<>))
   {
    var output=method.ReturnType.GetGenericArguments()[0];
    if(output.FullName=="C.f"&&p.Length>=3&&p[0].ParameterType.FullName=="b.m"&&p[1].ParameterType.FullName=="C.d")
    {
     var o=To<RuntimePlacementOptimizationOptions>(args[1]);var s=To<RuntimeLiveSnapshotObservation>(args[0]);
     int comparison=Interlocked.Exchange(ref NextComparisonMode,0);
     if(comparison==1)
     {
      LastSearchWasAI=false;
      return Typed(output,RecordComparison("original",s,o,()=>AwaitNative((Task)Forward(method,args))));
     }
     if(Eligible(s,o)&&(comparison==2||SearchPolicy.UseModel(s,o)))try
     {
      var input=adapter.FromNative(args[0],args[1]);var callback=args.Length==4?args[2] as Delegate:null;
      LastSearchWasAI=true;DiscoveryBudget=budget;
      return Typed(output,comparison==2?RecordComparison("model",s,o,()=>Search(method,args,input,callback,(CancellationToken)args[args.Length-1])):Search(method,args,input,callback,(CancellationToken)args[args.Length-1]));
     }
     catch(Exception e){log("AI capture unavailable; original search: "+e.GetBaseException().Message);}
     LastSearchWasAI=false;
    }
    if(output.FullName=="b.S"&&p.Length==5&&p[0].ParameterType.FullName=="C.f")
    {
     Entry entry;lock(results)results.TryGetValue(args[0],out entry);
     if(entry!=null)return Typed(output,Curve(entry,output,(int)args[1],(CancellationToken)args[4]));
    }
   }
   return Forward(method,args);
  }
  private static async Task<object> AwaitNative(Task task){await task.ConfigureAwait(false);return task.GetType().GetProperty("Result").GetValue(task,null);}
  private async Task<object> RecordComparison(string method,RuntimeLiveSnapshotObservation snapshot,RuntimePlacementOptimizationOptions options,Func<Task<object>> run)
  {
   var input=JsonConvert.SerializeObject(new{observation=snapshot,options});string hash;
   using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input))).Replace("-","").ToLowerInvariant();
   var started=DateTime.UtcNow;var timer=Stopwatch.StartNew();
   log("Comparison start: "+method+"; input="+hash);
   var result=await run().ConfigureAwait(false);timer.Stop();
   try
   {
    string path=Path.Combine(Paths.BepInExRootPath,"logs","BazaarPlacementAI","comparison");Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path,started.ToString("yyyyMMdd-HHmmss-fff")+"-"+method+".json"),JsonConvert.SerializeObject(new{method,inputSha256=hash,startedUtc=started,wallMilliseconds=timer.Elapsed.TotalMilliseconds,input=JsonConvert.DeserializeObject(input),result},Formatting.Indented));
    var score=GameAdapter.Read(GameAdapter.Read(result,"Recommendation"),"WeightedScore");
    log("Comparison complete: "+method+"; "+timer.Elapsed.TotalMilliseconds.ToString("0")+" ms; score="+score+"; input="+hash);
   }
   catch(Exception e){log("Comparison audit unavailable: "+e.GetBaseException().Message);}
   return result;
  }
  public static bool Eligible(RuntimeLiveSnapshotObservation s,RuntimePlacementOptimizationOptions o)
  {
   return s?.Player!=null&&o!=null&&(o.PinnedItems==null||o.PinnedItems.Count==0)&&o.Objective==RuntimePlacementOptimizationObjective.MaximumWeightedScoreAtHorizon&&!o.HypotheticalPurchaseReplacement&&s.HypotheticalCard==null&&s.HypotheticalUpgradeTarget==null&&s.HypotheticalEventPreview==null&&s.HypotheticalEnchantmentTarget==null;
  }
  private async Task<object> Search(MethodInfo method,object[] args,BoardInput input,Delegate callback,CancellationToken token)
  {
   using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token))
   {
    await gate.WaitAsync(linked.Token).ConfigureAwait(false);
    AIActive=true;ProgressEvaluated=0;
    try{return await Task.Run(async()=>
    {
     try
     {
      EnsureCompute(input.ExpectedCatalog);
      int sequence=0,covered=0;
      var found=await compute.Search(input,budget,maximum,linked.Token,message=>
      {
       if(message.StartsWith("已评估")){int count;if(int.TryParse(message.Split(' ').ElementAtOrDefault(1),out count))covered=count;ProgressEvaluated=covered;}
       if(callback!=null)
       {
        var dto=new RuntimePlacementOptimizationProgress{Stage=message.Contains("复核")?RuntimePlacementOptimizationProgressStage.Validation:RuntimePlacementOptimizationProgressStage.Discovery,Sequence=++sequence,CoveredArrangementCount=covered,CoveredArrangementCount64=covered};
        try{callback.DynamicInvoke(Native(dto,callback.GetType().GetGenericArguments()[0]));}catch(Exception e){log("Progress: "+e.GetBaseException().Message);}
       }
      }).ConfigureAwait(false);
      var r=GameAdapter.BuildRecommendation(input,found.Best);
      r.WeightedScore=found.Best.Score;r.DamageScore=(long)found.Best.Damage;r.DefenseScore=(long)found.Best.Defense;
      r.WinnerIsIncumbent=Geometry.Key(found.Best.Starts)==Geometry.Key(input.Current);
      r.EvaluatedArrangementCount=found.Evaluated;r.CoveredArrangementCount=found.Evaluated;r.CoveredArrangementCount64=found.Evaluated;
      r.GeneratedCandidateCount=found.ModelCandidates;r.TotalTime=TimeSpan.FromMilliseconds(found.ElapsedMilliseconds);r.SearchTime=r.TotalTime;
      r.ValidationApplied=true;r.IndependentValidationSampleCount=found.Best.Runs;r.IndependentValidationCurrentSampleCount=found.Baseline.Runs;
      r.IndependentValidationWeightedScore=found.Best.Score;r.IndependentValidationCurrentWeightedScore=found.Baseline.Score;
      r.ValidationCandidateCount=found.Alternatives.Length;r.ValidationCompletedSampleCount=found.Best.Runs;
      var native=Native(new RuntimePlacementOptimizationEvaluation{CanOptimize=true,Recommendation=r,SearchDiagnostics=new RuntimePlacementSearchDiagnostics{ScoredArrangementCount=found.Evaluated}},method.ReturnType.GetGenericArguments()[0]);
      lock(results){if(results.Count>=8)results.Clear();results[native]=new Entry{Input=input,Starts=(int[])found.Best.Starts.Clone()};}
      log("AI search: "+found.ModelCandidates+" seeds, "+found.Evaluated+" evaluations, "+found.ElapsedMilliseconds.ToString("0")+" ms; current "+found.Baseline.Score.ToString("0.0")+", best "+found.Best.Score.ToString("0.0"));return native;
     }
     catch(OperationCanceledException){throw;}
     catch(Exception e)
     {
      log("AI failed; falling back to LookingIN: "+e.GetBaseException().Message);
      LastSearchWasAI=false;AIActive=false;
      var fallback=(Task)Forward(method,args);await fallback.ConfigureAwait(false);return fallback.GetType().GetProperty("Result").GetValue(fallback,null);
     }
    },linked.Token).ConfigureAwait(false);}
    finally{AIActive=false;gate.Release();}
   }
  }
  private async Task<object> Curve(Entry entry,Type type,int horizon,CancellationToken token)
  {
   using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token))
   {
    await gate.WaitAsync(linked.Token).ConfigureAwait(false);
    try{return Native(await compute.Timeline(entry.Input,entry.Starts,entry.Input.ValidationRuns,horizon,91771,linked.Token).ConfigureAwait(false),type);}
    finally{gate.Release();}
   }
  }
  private object Forward(MethodInfo method,object[] args)
  {
   try{return method.Invoke(original,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}
  }
  private static T To<T>(object value){return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));}
  private static object Native(object value,Type type){return JsonConvert.DeserializeObject(JsonConvert.SerializeObject(value),type,new JsonSerializerSettings{ConstructorHandling=ConstructorHandling.AllowNonPublicDefaultConstructor});}
  private static object Typed(Type t,Task<object> task){return typeof(NativeRouter).GetMethod(nameof(Cast),BindingFlags.NonPublic|BindingFlags.Static).MakeGenericMethod(t).Invoke(null,new object[]{task});}
  private static async Task<T> Cast<T>(Task<object> task){return (T)await task.ConfigureAwait(false);}
  public object CreateProxy(params Type[] interfaces)
  {
   methods=interfaces.Concat(interfaces.SelectMany(t=>t.GetInterfaces())).Distinct().SelectMany(t=>t.GetMethods()).Distinct().ToArray();
   var name=new AssemblyName("BazaarPlacementAI.SearchAdapter."+Guid.NewGuid().ToString("N"));
   var assembly=AppDomain.CurrentDomain.DefineDynamicAssembly(name,AssemblyBuilderAccess.Run);
   var builder=assembly.DefineDynamicModule(name.Name).DefineType("SearchService",TypeAttributes.Public|TypeAttributes.Sealed);
   foreach(var type in interfaces)builder.AddInterfaceImplementation(type);
   var owner=builder.DefineField("Owner",typeof(NativeRouter),FieldAttributes.Public);builder.DefineDefaultConstructor(MethodAttributes.Public);
   for(int i=0;i<methods.Length;i++)
   {
    var m=methods[i];var ps=m.GetParameters();if(ps.Any(p=>p.ParameterType.IsByRef))throw new NotSupportedException("Byref service method");
    var impl=builder.DefineMethod("Forward"+i,MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Final|MethodAttributes.NewSlot,m.ReturnType,ps.Select(p=>p.ParameterType).ToArray());
    var il=impl.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,owner);il.Emit(OpCodes.Ldc_I4,i);il.Emit(OpCodes.Ldc_I4,ps.Length);il.Emit(OpCodes.Newarr,typeof(object));
    for(int j=0;j<ps.Length;j++){il.Emit(OpCodes.Dup);il.Emit(OpCodes.Ldc_I4,j);il.Emit(OpCodes.Ldarg,j+1);if(ps[j].ParameterType.IsValueType)il.Emit(OpCodes.Box,ps[j].ParameterType);il.Emit(OpCodes.Stelem_Ref);}
    il.Emit(OpCodes.Callvirt,typeof(NativeRouter).GetMethod(nameof(Invoke)));
    if(m.ReturnType==typeof(void))il.Emit(OpCodes.Pop);else if(m.ReturnType.IsValueType)il.Emit(OpCodes.Unbox_Any,m.ReturnType);else il.Emit(OpCodes.Castclass,m.ReturnType);
    il.Emit(OpCodes.Ret);builder.DefineMethodOverride(impl,m);
   }
   var instance=Activator.CreateInstance(builder.CreateType());instance.GetType().GetField("Owner").SetValue(instance,this);return instance;
  }
  public void Dispose()
  {
   if(lifetime.IsCancellationRequested)return;
   if(field!=null&&ReferenceEquals(field.GetValue(coordinator),proxy))field.SetValue(coordinator,original);
   lifetime.Cancel();Task.Run(async()=>{await gate.WaitAsync().ConfigureAwait(false);try{compute?.Dispose();}finally{gate.Release();}});
  }
 }
}
