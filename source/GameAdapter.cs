using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using LookingIN.Compute.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarPlacementAI
{
 public sealed class GameState
 {
  public BoardInput Input;public string Fingerprint;
  public object NativeSnapshot,NativeTopology;
 }
 /// <summary>Read-only capture and explicit application through LookingIN's existing validated movement controller.
 /// No method patches, private protocol implementation, or direct game command injection.</summary>
 public sealed class GameAdapter
 {
  private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  private object runtime,coordinator,arranger;
  private Assembly lki;
  private readonly Dictionary<string,string> names;
  private static readonly JsonSerializerSettings JsonSettings=new JsonSerializerSettings{ConstructorHandling=ConstructorHandling.AllowNonPublicDefaultConstructor};
  public GameAdapter(string modelDirectory)
  {
   var meta=JObject.Parse(File.ReadAllText(Path.Combine(modelDirectory,"metadata.json")));
   names=((JObject)meta["templates"]).Properties().ToDictionary(p=>p.Name,p=>(string)p.Value["name"],StringComparer.OrdinalIgnoreCase);
  }
  public static object Read(object o,string property){return o==null?null:o.GetType().GetProperty(property,Flags)?.GetValue(o,null);}
  private void Bind()
  {
   if(coordinator!=null)return;
   BepInEx.PluginInfo info;
   if(!Chainloader.PluginInfos.TryGetValue("local.lookingin.bazaar.combat",out info))throw new InvalidOperationException("请先安装并启用 LookingIN 0.247.6");
   if(info.Metadata.Version.ToString()!="0.247.6")throw new InvalidOperationException("此测试版适配 LookingIN 0.247.6；当前版本需重新验证");
   lki=info.Instance.GetType().Assembly;
   var queue=new Queue<Tuple<object,int>>();queue.Enqueue(Tuple.Create((object)info.Instance,0));int visited=0;
   while(queue.Count>0&&visited++<64)
   {
    var next=queue.Dequeue();var value=next.Item1;
    var property=value.GetType().GetProperty("TimelineCoordinator",Flags);
    if(property!=null){runtime=value;coordinator=property.GetValue(value,null);break;}
    if(next.Item2>=2)continue;
    foreach(var field in value.GetType().GetFields(Flags).Where(f=>!f.IsStatic&&f.FieldType.Assembly==lki&&!f.FieldType.IsValueType))
    {var child=field.GetValue(value);if(child!=null)queue.Enqueue(Tuple.Create(child,next.Item2+1));}
   }
   if(coordinator==null)throw new InvalidOperationException("正在等待 LookingIN 初始化");
   var fieldInfo=runtime.GetType().GetFields(Flags).SingleOrDefault(f=>f.FieldType.FullName=="i.L");
   arranger=fieldInfo?.GetValue(runtime);
   if(arranger==null)throw new InvalidOperationException("当前 LookingIN 移动控制器不可用");
  }
  public string CurrentFingerprint()
  {
   Bind();var capture=Read(coordinator,"ObservedCapture");
   if(capture==null||Convert.ToBoolean(Read(capture,"IsCombat")??true))throw new InvalidOperationException("请在非战斗规划阶段使用");
   var snapshot=Read(capture,"Snapshot");if(snapshot==null)throw new InvalidOperationException("尚未取得实时棋盘");
   return Hash(JsonConvert.SerializeObject(snapshot));
  }
  public GameState Capture()
  {
   coordinator=null;runtime=null;arranger=null;
   Bind();
   if(Convert.ToBoolean(Read(coordinator,"IsOptimizationRunning")??false))throw new InvalidOperationException("LookingIN 正在搜索，请先等它结束");
   if(Convert.ToBoolean(Read(coordinator,"IsPvpCurveLocked")??false))throw new InvalidOperationException("战斗或回放期间不能搜索排布");
   var capture=Read(coordinator,"ObservedCapture");
   if(capture==null||Convert.ToBoolean(Read(capture,"IsCombat")??true))throw new InvalidOperationException("请在非战斗规划阶段使用");
   var source=Read(capture,"Snapshot");if(source==null)throw new InvalidOperationException("尚未取得实时棋盘");
   string json=JsonConvert.SerializeObject(source);
   var snapshot=JsonConvert.DeserializeObject<RuntimeLiveSnapshotObservation>(json);
   if(snapshot.HypotheticalCard!=null||snapshot.HypotheticalUpgradeTarget!=null||snapshot.HypotheticalEventPreview!=null||snapshot.HypotheticalEnchantmentTarget!=null)
    throw new InvalidOperationException("请先退出商店或技能假想预览，读取真实库存");
   var native=JsonConvert.DeserializeObject(json,source.GetType(),JsonSettings);
   var topologyMethod=lki.GetType("s.F",true).GetMethods(Flags).Single(m=>m.IsStatic&&m.ReturnType==typeof(bool)&&m.GetParameters().Length==3&&m.GetParameters()[0].ParameterType==source.GetType()&&m.GetParameters()[1].ParameterType.FullName=="C.C&");
   object[] topologyArgs={native,null,null};
   if(!(bool)topologyMethod.Invoke(null,topologyArgs))throw new InvalidOperationException("库存拓扑不可用："+topologyArgs[2]);
   var topology=JsonConvert.DeserializeObject<RuntimeInventoryTopology>(JsonConvert.SerializeObject(topologyArgs[1]));
   var items=snapshot.Player.Cards.Where(c=>c.CardType=="Item"&&(c.Zone==RuntimeLiveCardZone.Board||c.Zone==RuntimeLiveCardZone.Stash)).OrderBy(c=>c.Zone==RuntimeLiveCardZone.Board?0:1).ThenBy(c=>c.Position).ToArray();
   if(items.Length>20)throw new InvalidOperationException("候选超过20件，本模型不会截断库存");
   if(items.Length==0)throw new InvalidOperationException("场上还没有可搜索的物品");
   var declared=lki.GetType("F.e",true).GetProperty("Identity",Flags)?.GetValue(null,null);
   if(declared==null)throw new InvalidOperationException("无法确认 LookingIN 声明的规则数据身份");
   var expected=JsonConvert.DeserializeObject<RuntimeCombatCatalogIdentity>(JsonConvert.SerializeObject(declared));
   int level; snapshot.Player.Attributes.TryGetValue("Level",out level);
   var b=new BoardInput{Snapshot=snapshot,ExpectedCatalog=expected,Hero=snapshot.Player.Hero,Day=snapshot.Day,Level=level,Low=snapshot.BoardMinimumPosition,High=snapshot.BoardMaximumPosition,
    HorizonMilliseconds=Math.Max(1,Math.Min(60,Convert.ToInt32(Read(coordinator,"EvaluationSeconds")??10)))*1000,DefenseWeight=Convert.ToDouble(Read(coordinator,"DefenseWeight")??0d),
    Pieces=items.Select(c=>new Piece{Source=c,InstanceId=c.InstanceId,TemplateId=c.TemplateId,Tier=c.Tier,Enchantment=c.Enchantment,Size=c.BoardSize,OriginalPosition=c.Zone==RuntimeLiveCardZone.Board?c.Position:-1,Name=names.ContainsKey(c.TemplateId)?names[c.TemplateId]:c.TemplateId}).ToArray(),
    Skills=snapshot.Player.Cards.Where(c=>c.Zone==RuntimeLiveCardZone.Skill).ToArray(),Sockets=new RuntimeMindPalaceSocketAssignment[0],BoardUnlocked=topology.BoardUnlockedSockets.ToArray(),StashUnlocked=topology.StashUnlockedSockets.ToArray(),StashLow=topology.StashMinimumPosition,StashHigh=topology.StashMaximumPosition};
   if(!Geometry.Valid(b,b.Current))throw new InvalidOperationException("当前库存位置不完整，请等待采集刷新");
   return new GameState{Input=b,NativeSnapshot=native,NativeTopology=topologyArgs[1],Fingerprint=Hash(json)};
  }
  public bool IsMoving{get{try{Bind();return Convert.ToBoolean(Read(arranger,"IsRunning")??false);}catch{return false;}}}
  public string MovementStatus{get{return (string)Read(arranger,"Status")??string.Empty;}}
  public bool Apply(GameState state,RankedLayout layout,float now)
  {
   Bind();if(CurrentFingerprint()!=state.Fingerprint)throw new InvalidOperationException("库存已变化，请重新搜索");
   if(!Geometry.Valid(state.Input,layout.Starts))throw new InvalidOperationException("推荐布局无效");
   var dto=BuildRecommendation(state.Input,layout);
   var result=lki.GetType("C.f",true);var nativeResult=JsonConvert.DeserializeObject(JsonConvert.SerializeObject(new RuntimePlacementOptimizationEvaluation{CanOptimize=true,Recommendation=dto}),result,JsonSettings);
   var request=Activator.CreateInstance(lki.GetType("J.n",true),true);
   Set(request,"Mode",Enum.ToObject(lki.GetType("J.N",true),3));Set(request,"Snapshot",state.NativeSnapshot);Set(request,"InventoryTopology",state.NativeTopology);
   Set(request,"ReplaceableItemInstanceIds",state.Input.Pieces.Where(p=>p.OriginalPosition>=0).Select(p=>p.InstanceId).ToArray());
   Set(request,"StashCandidateInstanceIds",state.Input.Pieces.Where(p=>p.OriginalPosition<0).Select(p=>p.InstanceId).ToArray());
   var pins=Read(coordinator,"ActivePlacementPins");
   var apply=arranger.GetType().GetMethods(Flags).Single(m=>m.Name=="A"&&m.ReturnType==typeof(bool)&&m.GetParameters().Length==4&&m.GetParameters()[0].ParameterType.FullName=="J.n"&&m.GetParameters()[2].ParameterType==typeof(float));
   return (bool)apply.Invoke(arranger,new[]{request,nativeResult,(object)now,pins});
  }
  public static RuntimePlacementRecommendation BuildRecommendation(BoardInput b,RankedLayout layout)
  {
   if(!Geometry.Valid(b,layout.Starts))throw new InvalidOperationException("Invalid recommendation geometry");var stash=Geometry.PackStash(b,layout.Starts);
   return new RuntimePlacementRecommendation{Board=b.Pieces.Select((p,i)=>new{p,i}).Where(x=>layout.Starts[x.i]>=0).Select(x=>new RuntimePlacementBoardItem{InstanceId=x.p.InstanceId,TemplateId=x.p.Source.TemplateId,Tier=x.p.Source.Tier,OriginalBoardPosition=x.p.OriginalPosition,BoardPosition=layout.Starts[x.i],BoardSize=x.p.Size}).ToArray(),
    TargetInventory=b.Pieces.Select((p,i)=>new RuntimePlacementInventoryTarget{InstanceId=p.InstanceId,Container=layout.Starts[i]>=0?RuntimeInventoryContainer.Board:RuntimeInventoryContainer.Stash,Position=layout.Starts[i]>=0?layout.Starts[i]:stash[i],Size=p.Size}).ToArray(),AverageDamageScore=layout.Damage,AverageDefenseScore=layout.Defense,ScoreSampleCount=layout.Runs,
    ReplacementApplied=b.Pieces.Where((p,i)=>p.OriginalPosition<0&&layout.Starts[i]>=0).Any(),
    ReplacedItemInstanceIds=b.Pieces.Where((p,i)=>p.OriginalPosition>=0&&layout.Starts[i]<0).Select(p=>p.InstanceId).ToArray(),
    SelectedStashCandidateInstanceIds=b.Pieces.Where((p,i)=>p.OriginalPosition<0&&layout.Starts[i]>=0).Select(p=>p.InstanceId).ToArray(),
    ScoreMaximumMilliseconds=b.HorizonMilliseconds,DefenseWeight=b.DefenseWeight};
  }
  public object Coordinator {get{Bind();return coordinator;}}
  public bool IsTextInputFocused {get{Bind();return Convert.ToBoolean(Read(Read(runtime,"TimelineOverlay"),"IsDefenseWeightInputFocused")??false);}}
  public void ReportSearchMessage(string message)
  {
   Bind();var method=coordinator.GetType().GetMethod("b",Flags,null,new[]{typeof(string)},null);
   method?.Invoke(coordinator,new object[]{message});
  }
  public Assembly LookingInAssembly {get{Bind();return lki;}}
  public RuntimeCombatCatalogIdentity ExpectedCatalog
  {
   get
   {
    Bind();var declared=lki.GetType("F.e",true).GetProperty("Identity",Flags)?.GetValue(null,null);
    if(declared==null)throw new InvalidOperationException("无法确认 LookingIN 声明的规则数据身份");
    return JsonConvert.DeserializeObject<RuntimeCombatCatalogIdentity>(JsonConvert.SerializeObject(declared));
   }
  }
  public BoardInput FromNative(object nativeSnapshot,object nativeOptions)
  {
   Bind();var snapshot=JsonConvert.DeserializeObject<RuntimeLiveSnapshotObservation>(JsonConvert.SerializeObject(nativeSnapshot));
   var options=JsonConvert.DeserializeObject<RuntimePlacementOptimizationOptions>(JsonConvert.SerializeObject(nativeOptions));
   var topology=options.InventoryTopology??new RuntimeInventoryTopology();
   var cards=snapshot.Player.Cards.Where(c=>c.CardType=="Item"&&(c.Zone==RuntimeLiveCardZone.Board||c.Zone==RuntimeLiveCardZone.Stash)).ToArray();
   if(cards.Length>20)throw new InvalidOperationException("AI最多支持20件候选");
   var source=lki.GetType("F.e",true).GetProperty("Identity",Flags).GetValue(null,null);
   int level;snapshot.Player.Attributes.TryGetValue("Level",out level);
   var eligible=new HashSet<string>(options.StashCandidateInstanceIds??Array.Empty<string>(),StringComparer.Ordinal);
   var replaceable=new HashSet<string>(options.ReplaceableItemInstanceIds??Array.Empty<string>(),StringComparer.Ordinal);
   var b=new BoardInput{Snapshot=snapshot,ExpectedCatalog=JsonConvert.DeserializeObject<RuntimeCombatCatalogIdentity>(JsonConvert.SerializeObject(source)),Hero=snapshot.Player.Hero,Day=snapshot.Day,Level=level,Low=snapshot.BoardMinimumPosition,High=snapshot.BoardMaximumPosition,
    HorizonMilliseconds=options.ScoreMaximumMilliseconds,DefenseWeight=options.DefenseWeight,ValidationRuns=Math.Max(1,Math.Min(100,options.ComparisonSampleCount)),
    Pieces=cards.Select(c=>new Piece{Source=c,InstanceId=c.InstanceId,TemplateId=c.TemplateId,Tier=c.Tier,Enchantment=c.Enchantment,Size=c.BoardSize,OriginalPosition=c.Zone==RuntimeLiveCardZone.Board?c.Position:-1,Name=names.ContainsKey(c.TemplateId)?names[c.TemplateId]:c.TemplateId}).ToArray(),
    Skills=snapshot.Player.Cards.Where(c=>c.Zone==RuntimeLiveCardZone.Skill).ToArray(),Sockets=new RuntimeMindPalaceSocketAssignment[0],BoardUnlocked=topology.BoardUnlockedSockets.Count>0?topology.BoardUnlockedSockets.ToArray():null,StashUnlocked=topology.StashUnlockedSockets.Count>0?topology.StashUnlockedSockets.ToArray():null,StashLow=topology.StashMinimumPosition,StashHigh=topology.StashMaximumPosition,
    RequiredOnBoard=new HashSet<string>(cards.Where(c=>c.Zone==RuntimeLiveCardZone.Board&&!replaceable.Contains(c.InstanceId)).Select(c=>c.InstanceId)),
    AllowedOnBoard=new HashSet<string>(cards.Where(c=>c.Zone==RuntimeLiveCardZone.Board||eligible.Contains(c.InstanceId)).Select(c=>c.InstanceId))};
   if(!Geometry.Valid(b,b.Current))throw new InvalidOperationException("当前库存或候选约束不完整");return b;
  }
  public bool StartWholeStash()
  {
   if(IsMoving)throw new InvalidOperationException("正在执行物品移动，请稍候");
   var state=Capture();var capture=Read(coordinator,"ObservedCapture");
   if(Convert.ToBoolean(Read(coordinator,"IsInputSuspended")??false))throw new InvalidOperationException("LookingIN 当前暂停接收输入");
   var request=Activator.CreateInstance(lki.GetType("J.n",true),true);
   Set(request,"Mode",Enum.ToObject(lki.GetType("J.N",true),3));Set(request,"Snapshot",state.NativeSnapshot);Set(request,"InventoryTopology",state.NativeTopology);Set(request,"CardNames",Read(capture,"CardNames"));
   var pins=(System.Collections.IEnumerable)Read(coordinator,"ActivePlacementPins");var pinIds=new HashSet<string>(pins.Cast<object>().Select(p=>(string)Read(p,"InstanceId")));
   var board=state.Input.Pieces.Where(p=>p.OriginalPosition>=0&&!pinIds.Contains(p.InstanceId)).Select(p=>p.InstanceId).ToArray();
   Set(request,"ReplaceableItemInstanceIds",board);Set(request,"StashCandidateInstanceIds",state.Input.Pieces.Where(p=>p.OriginalPosition<0).Select(p=>p.InstanceId).ToArray());
   bool hasStash=state.Input.Pieces.Any(p=>p.OriginalPosition<0);
   if(!hasStash)Set(request,"Mode",Enum.ToObject(lki.GetType("J.N",true),0));
   var marksField=coordinator.GetType().GetFields(Flags).Single(f=>f.FieldType==typeof(HashSet<string>));var marks=(HashSet<string>)marksField.GetValue(coordinator);var previous=marks.ToArray();
   try{marks.Clear();if(hasStash)marks.UnionWith(board);var start=coordinator.GetType().GetMethods(Flags).Single(m=>m.Name=="A"&&m.ReturnType==typeof(bool)&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.FullName=="J.n");return (bool)start.Invoke(coordinator,new[]{request});}
   finally{marks.Clear();marks.UnionWith(previous);}
  }
  private static void Set(object instance,string name,object value){instance.GetType().GetProperty(name,Flags).SetValue(instance,value,null);}
  public static string Hash(string json){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json))).Replace("-","");}
 }
}
