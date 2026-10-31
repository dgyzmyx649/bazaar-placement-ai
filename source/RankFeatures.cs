using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using LookingIN.Compute.Contracts;
namespace BazaarPlacementAI
{
 public sealed class RankProfiles
 {
  public const int Sem=64;
  public static readonly string[] Columns={"CooldownMax","DamageAmount","ShieldApplyAmount","HealAmount","BurnApplyAmount","PoisonApplyAmount","RegenApplyAmount","Multicast","AmmoMax","HasteAmount","ChargeAmount","CritChance","Lifesteal","FreezeAmount","SlowAmount","ChargeTargets","HasteTargets","FreezeTargets","SlowTargets","FlatCooldownReduction","PercentCooldownReduction","Custom_0","Custom_1","Custom_2","Custom_3","DamageGain","ShieldGain","HealGain","BurnGain","PoisonGain","CritChanceGain","Value","SellPrice","HealthMax"};
  public readonly BoardInput Board;
  public readonly float[][] Raw,Scaled,Semantics,Temporal;
  public readonly float[] Values;
  private readonly float[][] effectSem;
  private readonly int[] effectPositions;
  private readonly float[] skillSem=new float[Sem];
  private readonly float[] skillSum=new float[Columns.Length],skillMax=new float[Columns.Length];
  public RankProfiles(BoardInput b,JObject metadata)
  {
   Board=b;int n=b.Pieces.Length;Raw=new float[n][];Scaled=new float[n][];Semantics=new float[n][];Temporal=new float[n][];Values=new float[n];
   for(int i=0;i<n;i++)
   {
    var p=b.Pieces[i];var a=p.Prototype?.Attributes??p.Source?.Attributes??new Dictionary<string,int>();
    Raw[i]=Columns.Select(k=>(float)Get(a,k)).ToArray();Scaled[i]=Raw[i].Select(Log).ToArray();
    Semantics[i]=SemFor(metadata,p.TemplateId);Temporal[i]=TemporalFor(a,b.HorizonMilliseconds);
   }
   float avg=(float)Temporal.Select(t=>t[2]+t[3]+t[4]).DefaultIfEmpty(1).Average()/Math.Max(1,b.HorizonMilliseconds/1000f);
   for(int i=0;i<n;i++)
   {
    var t=Temporal[i];var r=Raw[i];
    Values[i]=Math.Max(.001f,t[2]+t[3]+t[4]+(float)b.DefenseWeight*(t[5]+t[6]+t[7])+avg*(r[10]/1000f*Math.Max(1,r[15])*.6f+r[9]/1000f*Math.Max(1,r[16])*.25f)+.01f);
   }
   foreach(var skill in b.Skills??new RuntimeLiveCardObservation[0])
   {
    var v=SemFor(metadata,skill.TemplateId);int tier=Array.IndexOf(new[]{"Bronze","Silver","Gold","Diamond","Legendary"},skill.Tier)+1;
    for(int j=0;j<Sem;j++)skillSem[j]+=v[j]*Math.Max(1,tier);
    var attrs=skill.Attributes;
    if(attrs==null||attrs.Count==0)attrs=metadata["templates"]?[skill.TemplateId.ToLowerInvariant()]?["tiers"]?[skill.Tier]?.ToObject<Dictionary<string,int>>()??new Dictionary<string,int>();
    for(int j=0;j<Columns.Length;j++){float value=Log(Get(attrs,Columns[j]));skillSum[j]+=value;skillMax[j]=Math.Max(skillMax[j],value);}
   }
   var effects=b.Snapshot.Player.Cards.Where(c=>c.Zone==RuntimeLiveCardZone.SocketEffect).ToArray();effectSem=effects.Select(c=>SemFor(metadata,c.TemplateId)).ToArray();effectPositions=effects.Select(c=>c.Position).ToArray();
  }
  public static int Get(IReadOnlyDictionary<string,int> a,string k){int v;return a!=null&&a.TryGetValue(k,out v)?v:0;}
  public static float Log(float v){return (float)(Math.Sign(v)*Math.Log(1+Math.Abs(v))/7);}
  public static int Cooldown(IReadOnlyDictionary<string,int> a)
  {int cd=Get(a,"CooldownMax");if(cd<=0)return 0;double value=(cd-Get(a,"FlatCooldownReduction"))*(1-Math.Min(100,Get(a,"PercentCooldownReduction"))/100d);return (int)Math.Max(Math.Min(cd,1000),value);}
  public static float[] TemporalFor(IReadOnlyDictionary<string,int> a,int horizon)
  {
   var t=new float[12];int cd=Cooldown(a),ammo=Get(a,"AmmoMax"),multi=Math.Max(1,Get(a,"Multicast"));
   Func<double,double> count=ms=>cd<=0?0:Math.Min(Math.Floor(ms/cd),ammo>0?ammo:double.MaxValue)*multi;
   double crit=1+Math.Max(0,Get(a,"CritChance"))/100d;
   for(int k=0;k<3;k++)t[k]=(float)(count(horizon*(k+1)/3d)*Get(a,"DamageAmount")*crit);
   double uses=count(horizon),seconds=horizon/1000d,tail=Math.Max(0,seconds-cd/1000d);
   t[3]=(float)(uses*Get(a,"BurnApplyAmount")*tail*.5);t[4]=(float)(uses*Get(a,"PoisonApplyAmount")*tail*.5);
   t[5]=(float)(uses*Get(a,"ShieldApplyAmount"));t[6]=(float)(uses*Get(a,"HealAmount"));t[7]=(float)(uses*Get(a,"RegenApplyAmount")*tail*.5);
   t[8]=(float)(uses*Get(a,"ChargeAmount")/1000d);t[9]=(float)(uses*Get(a,"HasteAmount")/1000d);t[10]=(float)(uses*Get(a,"FreezeAmount")/1000d);t[11]=(float)(uses*Get(a,"SlowAmount")/1000d);return t;
  }
  private static float[] SemFor(JObject meta,string id)
  {var v=meta["templates"]?[id.ToLowerInvariant()]?["sem"] as JArray;return v==null?new float[Sem]:v.ToObject<float[]>();}
  public float[] Features(int[] starts)
  {
   var b=Board;int n=b.Pieces.Length,attrs=Columns.Length;var f=new float[35+attrs*5+36+Sem*4];
   var cells=new int[10];for(int i=0;i<10;i++)cells[i]=-1;
   int field=0,used=0;for(int i=0;i<n;i++)if(starts[i]>=0){field++;used+=b.Pieces[i].Size;for(int s=starts[i];s<starts[i]+b.Pieces[i].Size;s++)cells[s]=i;}
   f[0]=b.Day/10f;f[1]=b.Level/10f;f[2]=b.Low/9f;f[3]=b.High/9f;f[4]=b.HorizonMilliseconds/10000f;f[5]=(float)b.DefenseWeight;f[6]=field/10f;f[7]=(n-field)/10f;f[8]=used/10f;f[9]=(b.High-b.Low+1-used)/10f;
   var pa=b.Snapshot.Player.Attributes;f[10]=Log(Get(pa,"HealthMax"));f[11]=Log(Get(pa,"Gold"));f[12]=Log(Get(pa,"Income"));f[13]=Log(Get(pa,"HealthRegen"));f[14]=(b.Skills?.Length??0)/16f;f[15]=effectPositions.Length/10f;
   int hero=Array.IndexOf(new[]{"Vanessa","Pygmalien","Dooley","Mak","Stelle","Jules","Karnok","Hero8"},b.Hero);if(hero>=0)f[16+hero]=1;
   for(int i=0;i<11;i++)f[24+i]=Log(Get(pa,"Custom_"+i));
   int a0=35,t0=a0+attrs*5,s0=t0+36;
   for(int i=0;i<attrs;i++){f[a0+attrs*3+i]=skillSum[i];f[a0+attrs*4+i]=skillMax[i];}
   for(int i=0;i<n;i++)
   {
    bool on=starts[i]>=0;int at=on?a0:a0+attrs*2;
    for(int k=0;k<attrs;k++){f[at+k]+=Scaled[i][k];if(on)f[a0+attrs+k]=Math.Max(f[a0+attrs+k],Scaled[i][k]);}
    if(!on)continue;
    for(int k=0;k<12;k++){float v=Temporal[i][k];f[t0+k]+=v;f[t0+12+k]=Math.Max(f[t0+12+k],v);f[t0+24+k]+=v*(starts[i]+.5f*b.Pieces[i].Size)/10f;}
    for(int k=0;k<Sem;k++){float v=Semantics[i][k];f[s0+k]+=v;f[s0+Sem+k]+=v*(starts[i]+.5f*b.Pieces[i].Size)/10f;f[s0+Sem*2+k]+=v*skillSem[k];}
    foreach(int e in Enumerable.Range(0,effectPositions.Length))if(effectPositions[e]>=starts[i]&&effectPositions[e]<starts[i]+b.Pieces[i].Size)
     for(int k=0;k<Sem;k++)f[s0+Sem*3+k]+=Semantics[i][k]*effectSem[e][k];
   }
   // Native base amounts can span several orders of magnitude; sum first, then scale.
   for(int k=t0;k<t0+36;k++)f[k]=Log(f[k]);
   return f;
  }
  public IEnumerable<int[]> Generate(int count,int seed,IEnumerable<int[]> initial=null)
  {
   var b=Board;var rng=new Random(seed);var seen=new HashSet<string>();
   foreach(var a in (initial??new int[0][]).Concat(new[]{b.Current}))if(Geometry.Valid(b,a)&&seen.Add(Geometry.Key(a)))yield return (int[])a.Clone();
   int n=b.Pieces.Length,width=b.High-b.Low+1,minimum=Math.Max(1,b.Pieces.Sum(p=>p.Size)-10);
   double avg=Values.DefaultIfEmpty(1).Average();
   for(int trial=0;trial<count*3&&seen.Count<count;trial++)
   {
    var dp=Enumerable.Repeat(double.NegativeInfinity,width+1).ToArray();var masks=new uint[width+1];dp[0]=0;
    for(int i=0;i<n;i++)
    {
     bool must=b.RequiredOnBoard!=null&&b.RequiredOnBoard.Contains(b.Pieces[i].InstanceId);
     bool allowed=b.AllowedOnBoard==null||b.AllowedOnBoard.Contains(b.Pieces[i].InstanceId);int size=b.Pieces[i].Size;
     double noise=-Math.Log(Math.Max(1e-9,-Math.Log(Math.Max(1e-9,rng.NextDouble()))));
     double weight=trial%6==0?Math.Max(.001,rng.NextDouble()):Values[i]+avg*noise*(trial%6==1?.1:trial%6==2?.5:trial%6==3?1:3);
     var next=must?Enumerable.Repeat(double.NegativeInfinity,width+1).ToArray():(double[])dp.Clone();var nm=must?new uint[width+1]:(uint[])masks.Clone();
     if(allowed)for(int c=0;c+size<=width;c++)if(!double.IsNegativeInfinity(dp[c])&&dp[c]+weight>next[c+size]){next[c+size]=dp[c]+weight;nm[c+size]=masks[c]|(1u<<i);}
     dp=next;masks=nm;
    }
    int chosen=-1;double best=double.NegativeInfinity;for(int c=minimum;c<=width;c++)if(dp[c]>best){chosen=c;best=dp[c];}if(chosen<0)continue;
    var order=Enumerable.Range(0,n).Where(i=>(masks[chosen]&(1u<<i))!=0).ToArray();for(int i=order.Length-1;i>0;i--){int j=rng.Next(i+1);int v=order[i];order[i]=order[j];order[j]=v;}
    var starts=Enumerable.Repeat(-1,n).ToArray();int pos=b.Low,free=width-chosen;
    foreach(int i in order){int gap=free>0&&trial%3==0?rng.Next(free+1):0;free-=gap;pos+=gap;starts[i]=pos;pos+=b.Pieces[i].Size;}
    if(Geometry.Valid(b,starts)&&seen.Add(Geometry.Key(starts)))yield return starts;
   }
  }
 }
}
