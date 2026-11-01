using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LookingIN.Compute.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class PlacementTests
{
 static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
 public static int Main(string[] args)
 {
  try{Run(args);return 0;}catch(Exception e){while(e.InnerException!=null)e=e.InnerException;Console.WriteLine("FAIL "+e.GetType().FullName+": "+e.Message);return 1;}
 }
 static void Run(string[] args)
 {
  string managed=@"E:\steam\steamapps\common\The Bazaar\TheBazaar_Data\Managed";
  foreach(var name in new[]{"System.Memory.dll","System.Buffers.dll","System.Runtime.CompilerServices.Unsafe.dll"})if(File.Exists(Path.Combine(managed,name)))Assembly.LoadFrom(Path.Combine(managed,name));
  var b=new BazaarPlacementAI.BoardInput{Low=0,High=9,Pieces=Enumerable.Range(0,20).Select(i=>new BazaarPlacementAI.Piece{Size=1,InstanceId="i"+i}).ToArray()};
  var current=Enumerable.Range(0,20).Select(i=>i<10?i:-1).ToArray();
  Check(BazaarPlacementAI.Geometry.Valid(b,current),"20 owned one-cell items fit board and stash");
  b.RequiredOnBoard=new System.Collections.Generic.HashSet<string>{"i0"};
  var swapped=(int[])current.Clone();swapped[0]=-1;swapped[10]=0;
  Check(!BazaarPlacementAI.Geometry.Valid(b,swapped),"ordinary search cannot discard a required current-board item");
  b.RequiredOnBoard=null;b.AllowedOnBoard=new System.Collections.Generic.HashSet<string>(Enumerable.Range(0,10).Select(i=>"i"+i));
  Check(!BazaarPlacementAI.Geometry.Valid(b,swapped),"hover search cannot include an unrequested stash item");b.AllowedOnBoard=null;
  var wrong=(int[])current.Clone();wrong[9]=-1;Check(!BazaarPlacementAI.Geometry.Valid(b,wrong),"rejects recommendations that overflow the stash");
  wrong=(int[])current.Clone();wrong[1]=0;Check(!BazaarPlacementAI.Geometry.Valid(b,wrong),"rejects overlaps");
  var exchange=new BazaarPlacementAI.BoardInput{Low=0,High=1,StashLow=0,StashHigh=1,Pieces=new[]{new BazaarPlacementAI.Piece{Size=2,InstanceId="large"},new BazaarPlacementAI.Piece{Size=1,InstanceId="small-a"},new BazaarPlacementAI.Piece{Size=1,InstanceId="small-b"}}};
  var full=new[]{0,-1,-1};
  Check(!BazaarPlacementAI.Geometry.Valid(exchange,new[]{-1,0,-1})&&BazaarPlacementAI.CandidateMoves.Around(exchange,full).Any(a=>a[0]<0&&a[1]>=0&&a[2]>=0),"full stash permits atomic two-small-for-one-medium exchange");
  exchange.RequiredOnBoard=new System.Collections.Generic.HashSet<string>{"large"};
  Check(BazaarPlacementAI.CandidateMoves.Around(exchange,full).All(a=>a[0]>=0),"atomic exchanges preserve required fielded items");
  Check(BazaarPlacementAI.RankProfiles.Cooldown(new System.Collections.Generic.Dictionary<string,int>{{"CooldownMax",2000},{"FlatCooldownReduction",1500}})==1000,"rank features use the core effective cooldown floor");
  b.Low=2;b.High=7;b.Pieces=b.Pieces.Take(8).ToArray();b.BoardUnlocked=Enumerable.Range(0,10).Select(i=>i>=2&&i<=7).ToArray();
  var partial=new[]{2,4,6,-1,-1,-1,-1,-1};Check(BazaarPlacementAI.Geometry.Valid(b,partial),"leading/interior gaps respect absolute unlocked sockets");
  b.BoardUnlocked=Enumerable.Repeat(true,6).ToArray();Check(BazaarPlacementAI.Geometry.Valid(b,partial),"also accepts relative-range topology masks");
  b.BoardUnlocked[2]=false;Check(!BazaarPlacementAI.Geometry.Valid(b,partial),"rejects locked socket placement");
  Check(BazaarPlacementAI.ModelRunner.Decode(new BazaarPlacementAI.BoardInput{Low=2,Pieces=new[]{new BazaarPlacementAI.Piece{Size=2},new BazaarPlacementAI.Piece{Size=1}}},new long[]{20,0,20,1,21}).SequenceEqual(new[]{3,6}),"skip actions preserve exact absolute starts");
  string model=Path.GetFullPath(args[0]);var snapshot=JObject.Parse(File.ReadAllText(args[1]))["input"]["observation"].ToObject<RuntimeLiveSnapshotObservation>();
  using(var classifier=new BazaarPlacementAI.ModelRunner(model))
  {
   bool rejected=false;try{classifier.ValidateCatalog(new RuntimeCombatCatalogIdentity{Value="wrong-catalog",SourceSha256="wrong-catalog"});}catch(InvalidDataException){rejected=true;}
   Check(rejected,"rank model rejects a mismatched core catalog before launching search");
  }
  b=FromSnapshot(snapshot);
  b.ValidationRuns=64;
  WarmupLifecycle(model);
  var boardOnly=new RuntimePlacementOptimizationOptions{StashCandidateInstanceIds=Array.Empty<string>()};
  Check(!BazaarPlacementAI.SearchPolicy.UseModel(snapshot,boardOnly),"small unslotted searches retain the original fast path");
  boardOnly.StashCandidateInstanceIds=snapshot.Player.Cards.Where(c=>c.Zone==RuntimeLiveCardZone.Stash).Select(c=>c.InstanceId).ToArray();
  Check(BazaarPlacementAI.SearchPolicy.UseModel(snapshot,boardOnly),"large whole-stash searches use the native-score model");
  using(var engine=new BazaarPlacementAI.Compute(model,@"E:\steam\steamapps\common\The Bazaar\BepInEx\plugins\LookingIN\LookingIN.Compute.Worker.exe",Path.GetFullPath("work/placement-plugin/tests-logs"),null,Console.WriteLine))
  {
   var original=JsonConvert.SerializeObject(snapshot);engine.Prepare(b,CancellationToken.None).GetAwaiter().GetResult();
   var score=engine.Score(b,b.Current,2,721,CancellationToken.None).GetAwaiter().GetResult();Check(score.Score>=0,"public core evaluates unchanged inventory");
   var result=engine.Search(b,1200,24,CancellationToken.None,s=>{}).GetAwaiter().GetResult();
   Check(result.ModelCandidates>0,"native Mono + ONNX produces legal AI candidates");
   Check(result.Best.Score>=result.Baseline.Score,"independent rescore retains original layout when stronger");
   Check(BazaarPlacementAI.Geometry.Valid(b,result.Best.Starts),"final recommendation respects board and stash capacity");
   var dto=BazaarPlacementAI.GameAdapter.BuildRecommendation(b,result.Best);
   Check(dto.TargetInventory.Count==b.Pieces.Length && dto.TargetInventory.Select(x=>x.InstanceId).Distinct().Count()==b.Pieces.Length,"movement target preserves every inventory instance exactly once");
   Check(original==JsonConvert.SerializeObject(snapshot),"search does not mutate captured input");
   Console.WriteLine("RESULT "+JsonConvert.SerializeObject(new{result.ModelCandidates,result.Evaluated,result.InferenceMilliseconds,result.ElapsedMilliseconds,Baseline=result.Baseline.Score,Best=result.Best.Score}));
   Integration(engine,model,snapshot);
  }
 }
 static void WarmupLifecycle(string model)
 {
  var router=new BazaarPlacementAI.NativeRouter();BazaarPlacementAI.Compute resident=null;int started=0,thread=0;
  int caller=Thread.CurrentThread.ManagedThreadId;Set(router,"log",(Action<string>)Console.WriteLine);
  Set(router,"createCompute",(Func<RuntimeCombatCatalogIdentity,BazaarPlacementAI.Compute>)(expected=>
  {
   Interlocked.Increment(ref started);thread=Thread.CurrentThread.ManagedThreadId;
   return resident=new BazaarPlacementAI.Compute(model,@"E:\steam\steamapps\common\The Bazaar\BepInEx\plugins\LookingIN\LookingIN.Compute.Worker.exe",Path.GetFullPath("work/placement-plugin/warmup-tests-logs"),expected,Console.WriteLine);
  }));
  Task.WhenAll(router.Warmup(null),router.Warmup(null)).GetAwaiter().GetResult();
  Check(router.IsWarm&&resident!=null&&thread!=caller&&started==1,"real SDK model/worker startup completes in background with one resident process");
  int pid=resident.WorkerProcessId;router.Dispose();
  bool closed=SpinWait.SpinUntil(()=>{try{using(var p=Process.GetProcessById(pid))return p.HasExited;}catch(ArgumentException){return true;}},5000);
  Check(closed&&!router.IsWarm,"plugin disposal releases the actual resident SDK worker");
  router.Warmup(null).GetAwaiter().GetResult();Check(started==1,"disposal prevents a late background initialization from restarting the worker");
 }
 static void Set(object o,string name,object value){o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(o,value);}
 static object Native(object o,Type t){return JsonConvert.DeserializeObject(JsonConvert.SerializeObject(o),t);}
 static object Await(object o){var task=(Task)o;task.GetAwaiter().GetResult();return task.GetType().GetProperty("Result").GetValue(task,null);}
 static void Integration(BazaarPlacementAI.Compute engine,string model,RuntimeLiveSnapshotObservation snapshot)
 {
  var asm=Assembly.LoadFrom(@"E:\steam\steamapps\common\The Bazaar\BepInEx\plugins\LookingIN\LookingIN.dll");
  var types=BazaarPlacementAI.LookingInTypes.For(asm);var service=types.Service;var progress=types.ProgressService;
  var original=Fake(service,progress);var coordinator=FormatterServices.GetUninitializedObject(types.Coordinator);
  var field=coordinator.GetType().GetFields(BindingFlags.NonPublic|BindingFlags.Instance).Single(f=>f.FieldType==service);field.SetValue(coordinator,original);
  var adapter=new BazaarPlacementAI.GameAdapter(model);Set(adapter,"coordinator",coordinator);Set(adapter,"lki",asm);
  var router=BazaarPlacementAI.NativeRouter.Attach(adapter,Path.GetDirectoryName(model),500,12,Console.WriteLine);
  int initializations=0,initializerThread=0,callerThread=Thread.CurrentThread.ManagedThreadId,workerPid=engine.WorkerProcessId;
  Set(router,"createCompute",(Func<RuntimeCombatCatalogIdentity,BazaarPlacementAI.Compute>)(expected=>{Interlocked.Increment(ref initializations);initializerThread=Thread.CurrentThread.ManagedThreadId;return engine;}));
  Task.WhenAll(router.Warmup(null),router.Warmup(null)).GetAwaiter().GetResult();
  Check(router.IsWarm&&initializations==1&&initializerThread!=callerThread,"background warmup initializes off the caller thread exactly once for concurrent requests");
  Check(!BazaarPlacementAI.NativeRouter.AIActive&&!BazaarPlacementAI.NativeRouter.LastSearchWasAI,"background warmup does not start a search or change existing search labels");
  Check(!ReferenceEquals(field.GetValue(coordinator),original),"real native readonly service field accepts interface proxy on game Mono");
  using(var hints=new BazaarPlacementAI.UIHints(asm,"G"))
  {
   BazaarPlacementAI.NativeRouter.LastSearchWasAI=true;BazaarPlacementAI.NativeRouter.AIActive=true;BazaarPlacementAI.NativeRouter.DiscoveryBudget=2500;BazaarPlacementAI.NativeRouter.ProgressEvaluated=3;hints.Refresh();
   Check(Convert.ToInt32(BazaarPlacementAI.GameAdapter.Read(coordinator,"OptimizationTimeBudgetMilliseconds"))==2500,"existing progress panel shows AI discovery budget");
   Check(((string)BazaarPlacementAI.GameAdapter.Read(coordinator,"OptimizationCoveragePercentage")).Contains("非穷举"),"AI progress never reports misleading exhaustive coverage");
   var localize=types.Localization.GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.ReturnType==typeof(string)&&m.GetParameters().Length==2&&m.GetParameters().All(x=>x.ParameterType==typeof(string)));
   string hint=(string)localize.Invoke(null,new object[]{"计算当前场上最优排列","Calculate the best arrangement for the current board"});
   Check(hint.Contains("G")&&(hint.Contains("全背包")||hint.Contains("whole stash")),"existing search tooltip explains G and no-PIN AI behavior");
   BazaarPlacementAI.NativeRouter.AIActive=false;
  }
  var nativeSnapshot=Native(snapshot,types.Snapshot);
  var options=new RuntimePlacementOptimizationOptions{ScoreMaximumMilliseconds=8000,ComparisonSampleCount=4,InventoryTopology=new RuntimeInventoryTopology{BoardMinimumPosition=snapshot.BoardMinimumPosition,BoardMaximumPosition=snapshot.BoardMaximumPosition,StashMinimumPosition=0,StashMaximumPosition=9,BoardUnlockedSockets=Enumerable.Repeat(true,10).ToArray(),StashUnlockedSockets=Enumerable.Repeat(true,10).ToArray()},ReplaceableItemInstanceIds=snapshot.Player.Cards.Where(c=>c.Zone==RuntimeLiveCardZone.Board).Select(c=>c.InstanceId).ToArray(),StashCandidateInstanceIds=snapshot.Player.Cards.Where(c=>c.Zone==RuntimeLiveCardZone.Stash).Select(c=>c.InstanceId).ToArray()};
  var search=types.SearchMethod;
  options.PinnedItems=new[]{new RuntimePlacementPin{InstanceId="test",BoardPosition=0}};
  int before=Calls;
  Check(Await(search.Invoke(field.GetValue(coordinator),new[]{nativeSnapshot,Native(options,types.Options),(object)CancellationToken.None}))==null&&Calls==before+1,"PIN requests forward to original service without AI");
  options.PinnedItems=Array.Empty<RuntimePlacementPin>();before=Calls;
  BazaarPlacementAI.NativeRouter.NextComparisonMode=1;
  Check(Await(search.Invoke(field.GetValue(coordinator),new[]{nativeSnapshot,Native(options,types.Options),(object)CancellationToken.None}))==null&&Calls==before+1&&!BazaarPlacementAI.NativeRouter.LastSearchWasAI&&BazaarPlacementAI.NativeRouter.NextComparisonMode==0,"original comparison forwards the same no-PIN request and consumes the override once");
  options.PinnedItems=new[]{new RuntimePlacementPin{InstanceId="test",BoardPosition=0}};before=Calls;
  BazaarPlacementAI.NativeRouter.NextComparisonMode=2;
  Check(Await(search.Invoke(field.GetValue(coordinator),new[]{nativeSnapshot,Native(options,types.Options),(object)CancellationToken.None}))==null&&Calls==before+1&&!BazaarPlacementAI.NativeRouter.LastSearchWasAI,"model comparison still respects PIN eligibility");
  options.PinnedItems=Array.Empty<RuntimePlacementPin>();before=Calls;
  var progressMethod=progress.GetMethods().Single();
  var callbackType=progressMethod.GetParameters()[2].ParameterType;
  var callback=Delegate.CreateDelegate(callbackType,typeof(PlacementTests).GetMethod("OnProgress").MakeGenericMethod(types.Progress));
  var evaluation=Await(progressMethod.Invoke(field.GetValue(coordinator),new[]{nativeSnapshot,Native(options,types.Options),callback,(object)CancellationToken.None}));
  Check(evaluation!=null&&Calls==before,"no-PIN request returns native typed AI recommendation without fallback");
  Check(Convert.ToBoolean(BazaarPlacementAI.GameAdapter.Read(evaluation,"CanOptimize")),"native result enables existing recommendation UI");
  Check(ProgressCalls>0,"AI reports progress through LookingIN's native progress callback");
  router.Warmup(null).GetAwaiter().GetResult();
  Check(initializations==1&&engine.WorkerProcessId==workerPid,"search and repeated warmup reuse the same resident model and worker PID");
  using(var cancel=new CancellationTokenSource())
  {
   cancel.Cancel();bool canceled=false;
   try{Await(search.Invoke(field.GetValue(coordinator),new[]{nativeSnapshot,Native(options,types.Options),(object)cancel.Token}));}catch(OperationCanceledException){canceled=true;}
   Check(canceled&&Calls==before,"native cancellation propagates without starting fallback search");
  }
  var curve=types.CurveMethod;
  var timeline=Await(curve.Invoke(field.GetValue(coordinator),new[]{evaluation,(object)8000,(object)100,(object)1u,(object)CancellationToken.None}));
  Check(timeline!=null&&Convert.ToBoolean(BazaarPlacementAI.GameAdapter.Read(timeline,"CanSimulate"))&&Calls==before,"existing blue-curve request resolves AI result through public compute core");
  Set(router,"compute",null);router.Dispose();Check(ReferenceEquals(field.GetValue(coordinator),original),"detach restores original search service");
  router.Warmup(null).GetAwaiter().GetResult();
  Check(!router.IsWarm&&initializations==1,"warmup cannot recreate a worker after plugin disposal");
 }
 public static int Calls;
 public static int ProgressCalls;
 public static void OnProgress<T>(T value){ProgressCalls++;}
 public static object Forward(Type t)
 {
  Calls++;if(t==typeof(void))return null;
  if(t.IsGenericType&&t.GetGenericTypeDefinition()==typeof(Task<>))return typeof(Task).GetMethod("FromResult").MakeGenericMethod(t.GetGenericArguments()[0]).Invoke(null,new object[]{null});
  return t.IsValueType?Activator.CreateInstance(t):null;
 }
 static object Fake(params Type[] interfaces)
 {
  var asm=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("FakeCompute"),AssemblyBuilderAccess.Run);
  var builder=asm.DefineDynamicModule("FakeCompute").DefineType("FakeComputeService",TypeAttributes.Public);builder.DefineDefaultConstructor(MethodAttributes.Public);
  foreach(var t in interfaces)builder.AddInterfaceImplementation(t);
  int i=0;foreach(var m in interfaces.Concat(interfaces.SelectMany(t=>t.GetInterfaces())).Distinct().SelectMany(t=>t.GetMethods()).Distinct())
  {
   var impl=builder.DefineMethod("Forward"+(i++),MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot|MethodAttributes.Final,m.ReturnType,m.GetParameters().Select(p=>p.ParameterType).ToArray());
   var il=impl.GetILGenerator();il.Emit(OpCodes.Ldtoken,m.ReturnType);il.Emit(OpCodes.Call,typeof(Type).GetMethod("GetTypeFromHandle"));il.Emit(OpCodes.Call,typeof(PlacementTests).GetMethod("Forward"));
   if(m.ReturnType==typeof(void))il.Emit(OpCodes.Pop);else if(m.ReturnType.IsValueType)il.Emit(OpCodes.Unbox_Any,m.ReturnType);else il.Emit(OpCodes.Castclass,m.ReturnType);
   il.Emit(OpCodes.Ret);builder.DefineMethodOverride(impl,m);
  }
  return Activator.CreateInstance(builder.CreateType());
 }
 public static BazaarPlacementAI.BoardInput FromSnapshot(RuntimeLiveSnapshotObservation snapshot)
 {
  return new BazaarPlacementAI.BoardInput{Snapshot=snapshot,Hero=snapshot.Player.Hero,Day=snapshot.Day,Level=snapshot.Player.Attributes["Level"],Low=snapshot.BoardMinimumPosition,High=snapshot.BoardMaximumPosition,HorizonMilliseconds=8000,DefenseWeight=0,
   Pieces=snapshot.Player.Cards.Where(c=>c.CardType=="Item"&&(c.Zone==RuntimeLiveCardZone.Board||c.Zone==RuntimeLiveCardZone.Stash)).Select(c=>new BazaarPlacementAI.Piece{Source=c,InstanceId=c.InstanceId,TemplateId=c.TemplateId,Tier=c.Tier,Enchantment=c.Enchantment,Name=c.TemplateId,OriginalPosition=c.Zone==RuntimeLiveCardZone.Board?c.Position:-1,Size=c.BoardSize}).ToArray(),
   Skills=snapshot.Player.Cards.Where(c=>c.Zone==RuntimeLiveCardZone.Skill).ToArray(),Sockets=new RuntimeMindPalaceSocketAssignment[0]};
 }
}
