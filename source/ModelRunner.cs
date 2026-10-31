using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Newtonsoft.Json.Linq;

namespace BazaarPlacementAI
{
 public sealed class ModelRunner:IDisposable
 {
  private readonly InferenceSession encoder,decoder;
  private readonly TreeRanker nativeRanker;
  private readonly JObject metadata;
  private readonly Dictionary<string,int> vocabulary;
  private readonly string[] heroes,enchantments;
  public double LastMilliseconds {get;private set;}
  private readonly HashSet<string> unknown=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  public int UnknownTemplates {get{return unknown.Count;}}
  public ModelRunner(string directory)
  {
   metadata=JObject.Parse(File.ReadAllText(Path.Combine(directory,"metadata.json")));
   if((string)metadata["schema"]=="placement-native-score-ranker-v4")
   {
    vocabulary=((JObject)metadata["templates"]).Properties().Select((p,i)=>new{Id=p.Name,Index=i+1}).ToDictionary(x=>x.Id,x=>x.Index,StringComparer.OrdinalIgnoreCase);
    nativeRanker=new TreeRanker(directory,metadata);heroes=new string[0];enchantments=new string[0];return;
   }
   NativeLoader.Load(Path.GetDirectoryName(directory));
   if((string)metadata["schema"]!="placement-20-slots-gap-v2")throw new InvalidDataException("Unsupported model schema");
   vocabulary=((JArray)metadata["vocabulary"]).Select((v,i)=>new{Id=(string)v,Index=i+1}).ToDictionary(x=>x.Id,x=>x.Index,StringComparer.OrdinalIgnoreCase);
   heroes=metadata["names"]["hero"].ToObject<string[]>();enchantments=metadata["names"]["ench"].ToObject<string[]>();
   using(var options=new SessionOptions())
   {
    options.IntraOpNumThreads=1;options.InterOpNumThreads=1;options.GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL;
    options.AddSessionConfigEntry("session.intra_op.allow_spinning","0");options.AddSessionConfigEntry("session.inter_op.allow_spinning","0");
    encoder=new InferenceSession(Path.Combine(directory,"encoder.onnx"),options);
    try{decoder=new InferenceSession(Path.Combine(directory,"decoder.onnx"),options);}catch{encoder.Dispose();throw;}
   }
  }
  public string Name(string id){return (string)metadata["templates"]?[id.ToLowerInvariant()]?["name"]??id;}
  public void ValidateCatalog(LookingIN.Compute.Contracts.RuntimeCombatCatalogIdentity expected)
  {
   if(nativeRanker==null||expected==null)return;var c=metadata["catalog"];
   if(c==null||!string.Equals((string)c["Value"],expected.Value,StringComparison.OrdinalIgnoreCase)||!string.Equals((string)c["SourceSha256"],expected.SourceSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("模型规则版本与当前计算核心不一致");
  }
  public Dictionary<string,int> Baseline(string id,string tier)
  {
   return metadata["templates"]?[id.ToLowerInvariant()]?["tiers"]?[tier]?.ToObject<Dictionary<string,int>>()??new Dictionary<string,int>();
  }
  public static float[] Growth(IReadOnlyDictionary<string,int> actual,IReadOnlyDictionary<string,int> baseline,string name)
  {
   var columns=new[]{"CooldownMax","DamageAmount","ShieldApplyAmount","HealAmount","BurnApplyAmount","PoisonApplyAmount","RegenApplyAmount","Multicast","AmmoMax","HasteAmount","ChargeAmount","CritChance","Lifesteal","FreezeAmount","SlowAmount"};
   var scales=new[]{10000f,100,100,100,20,20,20,3,6,3000,3000,100,100,3000,3000};var result=new float[19];
   for(int i=0;i<columns.Length;i++)result[i]=(Value(actual,columns[i])-Value(baseline,columns[i]))/scales[i];
   int counter=name=="Spider Fern"?3:(new[]{"Augmented Weaponry","Augmented Defenses","Langxian","Inexorable"}.Contains(name)?1:-1);
   if(counter>=0)result[15+counter]=(Value(actual,"Custom_"+counter)-Value(baseline,"Custom_"+counter))/20f;
   return result;
  }
  private static int Value(IReadOnlyDictionary<string,int> d,string k){int v;return d!=null&&d.TryGetValue(k,out v)?v:0;}
  private int Card(string id){int i;if(!vocabulary.TryGetValue(id,out i)){unknown.Add(id);return 0;}return i;}
  private static int Tier(string tier){int i=Array.IndexOf(new[]{"Bronze","Silver","Gold","Diamond","Legendary"},tier);if(i<0)throw new InvalidOperationException("未知品质："+tier);return i+1;}
  private static long Index(string[] names,string name){int i=Array.IndexOf(names,name??"");return i<0?0:i;}
  private static OrtValue Tensor<T>(T[] data,params long[] shape) where T:unmanaged{return OrtValue.CreateTensorValueFromMemory(data,shape);}
  private Dictionary<string,OrtValue> EncodeInputs(BoardInput b)
  {
   if(b.Pieces.Length>20)throw new InvalidOperationException("最多支持20件候选，不会截断物品");
   var pc=new long[20];var pt=new long[20];var pe=new long[20];var sizes=new long[20];var pr=new float[20*19];var pv=new bool[20];
   for(int k=0;k<b.Pieces.Length;k++){var p=b.Pieces[k];pc[k]=Card(p.TemplateId);pt[k]=Tier(p.Tier);pe[k]=Index(enchantments,p.Enchantment);sizes[k]=p.Size;pv[k]=true;Array.Copy(p.Run,0,pr,k*19,19);}
   var sc=new long[16];var st=new long[16];var sr=new float[16*19];var sv=new bool[16];
   if(b.Skills.Length>16)throw new InvalidOperationException("模型最多支持16个技能");
   for(int k=0;k<b.Skills.Length;k++){var c=b.Skills[k];sc[k]=Card(c.TemplateId);st[k]=Tier(c.Tier);sv[k]=true;var g=Growth(c.Attributes,Baseline(c.TemplateId,c.Tier),Name(c.TemplateId));Array.Copy(g,0,sr,k*19,19);}
   var ec=new long[32];var et=new long[32];var es=new long[32];var er=new float[32*19];var ev=new bool[32];
   var fx=b.Snapshot.Player.Cards.Where(c=>c.Zone==LookingIN.Compute.Contracts.RuntimeLiveCardZone.SocketEffect).ToArray();
   if(fx.Length>32)throw new InvalidOperationException("模型最多支持32个格位效果");
   for(int k=0;k<fx.Length;k++){ec[k]=Card(fx[k].TemplateId);et[k]=Tier(fx[k].Tier);es[k]=fx[k].Position+1;ev[k]=true;}
   return new Dictionary<string,OrtValue>{
    ["piece_card"]=Tensor(pc,1,20),["piece_tier"]=Tensor(pt,1,20),["piece_ench"]=Tensor(pe,1,20),["piece_cells"]=Tensor(sizes,1,20),["piece_run"]=Tensor(pr,1,20,19),
    ["skill_card"]=Tensor(sc,1,16),["skill_tier"]=Tensor(st,1,16),["skill_run"]=Tensor(sr,1,16,19),
    ["effect_card"]=Tensor(ec,1,32),["effect_tier"]=Tensor(et,1,32),["effect_socket"]=Tensor(es,1,32),["effect_run"]=Tensor(er,1,32,19),
    ["hero"]=Tensor(new[]{Index(heroes,b.Hero)},1),["numbers"]=Tensor(new[]{b.Day/10f,b.Low/9f,b.High/9f,b.Level/10f},1,4),
    ["piece_valid"]=Tensor(pv,1,20),["skill_valid"]=Tensor(sv,1,16),["effect_valid"]=Tensor(ev,1,32)};
  }
  private sealed class Beam {public long[] Order=Enumerable.Repeat(21L,11).ToArray();public double Score;public bool Done;}
  public int[][] Predict(BoardInput b,int width=8)
  {
   unknown.Clear();
   if(nativeRanker!=null){foreach(var p in b.Pieces)Card(p.TemplateId);var ranked=nativeRanker.Predict(b,Math.Max(24,width));LastMilliseconds=nativeRanker.LastMilliseconds;return ranked;}
   var clock=Stopwatch.StartNew();float[] memory;bool[] padding;
   var inputs=EncodeInputs(b);
   try{using(var run=new RunOptions())using(var output=encoder.Run(run,inputs,new[]{"memory","padding"})){memory=output[0].GetTensorDataAsSpan<float>().ToArray();padding=output[1].GetTensorDataAsSpan<bool>().ToArray();}}
   finally{foreach(var v in inputs.Values)v.Dispose();}
   int tokens=padding.Length,dimension=memory.Length/tokens;var beams=new List<Beam>{new Beam()};
   var fx=b.Snapshot.Player.Cards.Where(c=>c.Zone==LookingIN.Compute.Contracts.RuntimeLiveCardZone.SocketEffect).ToArray();
   for(int step=0;step<11;step++)
   {
    int count=beams.Count;var m=Repeat(memory,count);var pad=Repeat(padding,count);var cells=new long[count*20];var valid=new bool[count*20];var order=new long[count*11];var low=Enumerable.Repeat((long)b.Low,count).ToArray();var boardWidth=Enumerable.Repeat((long)(b.High-b.Low+1),count).ToArray();var effects=new float[count*10*32];
    for(int n=0;n<count;n++)
    {
     Array.Copy(beams[n].Order,0,order,n*11,11);
     for(int k=0;k<b.Pieces.Length;k++){cells[n*20+k]=b.Pieces[k].Size;valid[n*20+k]=true;}
     for(int k=0;k<fx.Length;k++)effects[n*320+fx[k].Position*32+k]=1;
    }
    var feed=new Dictionary<string,OrtValue>{["memory"]=Tensor(m,count,tokens,dimension),["padding"]=Tensor(pad,count,tokens),["piece_cells"]=Tensor(cells,count,20),["low"]=Tensor(low,count),["width"]=Tensor(boardWidth,count),["piece_valid"]=Tensor(valid,count,20),["socket_effect"]=Tensor(effects,count,10,32),["order"]=Tensor(order,count,11)};
    float[] logits;
    try{using(var run=new RunOptions())using(var output=decoder.Run(run,feed,new[]{"logits"})){logits=output[0].GetTensorDataAsSpan<float>().ToArray();}}
    finally{foreach(var v in feed.Values)v.Dispose();}
    var expanded=new List<Beam>();
    for(int n=0;n<count;n++)
    {
     if(beams[n].Done){expanded.Add(beams[n]);continue;}
     int at=(n*11+step)*22;
     EnforceInventory(b,beams[n].Order,step,logits,at);
     double max=Enumerable.Range(0,22).Max(a=>(double)logits[at+a]);if(max<=-1e8)continue;
     double denom=Enumerable.Range(0,22).Sum(a=>Math.Exp(logits[at+a]-max));
     for(int action=0;action<22;action++)if(logits[at+action]>-1e8)
     {var next=new Beam{Order=(long[])beams[n].Order.Clone(),Score=beams[n].Score+logits[at+action]-max-Math.Log(denom),Done=action==21};next.Order[step]=action;expanded.Add(next);}
    }
    beams=expanded.OrderByDescending(x=>x.Score).Take(width).ToList();if(beams.All(x=>x.Done))break;
   }
   LastMilliseconds=clock.Elapsed.TotalMilliseconds;
   return beams.Select(x=>Decode(b,x.Order)).Where(c=>Geometry.Valid(b,c)).GroupBy(Geometry.Key).Select(g=>g.First()).ToArray();
  }
  private static void EnforceInventory(BoardInput b,long[] order,int step,float[] logits,int offset)
  {
   int position=b.Low,selected=0;for(int t=0;t<step;t++){long a=order[t];if(a==20)position++;else if(a<20){selected+=b.Pieces[a].Size;position+=b.Pieces[a].Size;}}
   int stashCapacity=b.StashUnlocked==null?b.StashHigh-b.StashLow+1:b.StashUnlocked.Count(x=>x);
   int required=Math.Max(0,b.Pieces.Sum(p=>p.Size)-selected-stashCapacity);
   var chosen=new HashSet<long>(order.Take(step).Where(a=>a>=0&&a<b.Pieces.Length));
   int mandatory=b.RequiredOnBoard==null?0:b.Pieces.Where((p,i)=>!chosen.Contains(i)&&b.RequiredOnBoard.Contains(p.InstanceId)).Sum(p=>p.Size);
   required=Math.Max(required,mandatory);
   if(required>0)logits[offset+21]=-1e9f;
   if(b.RequiredOnBoard!=null&&b.Pieces.Where((p,i)=>b.RequiredOnBoard.Contains(p.InstanceId)).Any(p=>!order.Take(step).Any(a=>a<20&&b.Pieces[a].InstanceId==p.InstanceId)))logits[offset+21]=-1e9f;
   int free=Enumerable.Range(Math.Min(position,b.High+1),Math.Max(0,b.High-position+1)).Count(p=>b.BoardUnlocked==null||Geometry.Unlocked(b.BoardUnlocked,p,b.Low));
   for(int a=0;a<b.Pieces.Length;a++)
   {
    if(b.AllowedOnBoard!=null&&!b.AllowedOnBoard.Contains(b.Pieces[a].InstanceId))logits[offset+a]=-1e9f;
    int mandatoryAfter=mandatory-(b.RequiredOnBoard!=null&&b.RequiredOnBoard.Contains(b.Pieces[a].InstanceId)&&!chosen.Contains(a)?b.Pieces[a].Size:0);
    if(free-b.Pieces[a].Size<mandatoryAfter)logits[offset+a]=-1e9f;
   }
   for(int a=0;a<b.Pieces.Length;a++)for(int p=position;p<position+b.Pieces[a].Size;p++)
    if(p>b.High||b.BoardUnlocked!=null&&!Geometry.Unlocked(b.BoardUnlocked,p,b.Low)){logits[offset+a]=-1e9f;break;}
   int remaining=Enumerable.Range(Math.Min(position+1,b.High+1),Math.Max(0,b.High-position)).Count(p=>b.BoardUnlocked==null||Geometry.Unlocked(b.BoardUnlocked,p,b.Low));
   if(remaining<required)logits[offset+20]=-1e9f;
  }
  public static int[] Decode(BoardInput b,long[] order)
  {
   var starts=Enumerable.Repeat(-1,b.Pieces.Length).ToArray();int at=b.Low;
   foreach(long action in order){if(action==21)break;if(action==20){at++;continue;}if(action<0||action>=b.Pieces.Length||starts[action]>=0)throw new InvalidDataException("Invalid model action");starts[action]=at;at+=b.Pieces[action].Size;}
   return starts;
  }
  private static T[] Repeat<T>(T[] source,int n){var output=new T[source.Length*n];for(int i=0;i<n;i++)Array.Copy(source,0,output,i*source.Length,source.Length);return output;}
  public int[][] RankCandidates(BoardInput b,System.Collections.Generic.IEnumerable<int[]> layouts,int maximum)
  {return nativeRanker!=null?nativeRanker.Rank(b,layouts,maximum):layouts.Take(maximum).ToArray();}
  public void Dispose(){decoder?.Dispose();encoder?.Dispose();}
 }
 public static class NativeLoader
 {
  private static readonly object Gate=new object();private static IntPtr handle;
  [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr LoadLibraryEx(string path,IntPtr reserved,uint flags);
  public static void Load(string directory)
  {
   lock(Gate)
   {
    if(handle!=IntPtr.Zero)return;if(IntPtr.Size!=8)throw new PlatformNotSupportedException("This build requires Windows x64");
    string path=Path.Combine(directory,"onnxruntime.dll");if(!File.Exists(path))throw new FileNotFoundException("Missing CPU inference runtime",path);
    handle=LoadLibraryEx(path,IntPtr.Zero,0x00000100|0x00001000);
    if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Unable to load CPU inference runtime");
   }
  }
 }
}
