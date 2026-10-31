using System;
using System.Collections.Generic;
using System.Linq;
using LookingIN.Compute.Contracts;

namespace BazaarPlacementAI
{
 public sealed class Piece
 {
  public string InstanceId,TemplateId,Tier,Enchantment,Name;
  public int Size,OriginalPosition;
  public RuntimeMindPalaceCardPrototype Prototype;
  public RuntimeLiveCardObservation Source;
  public float[] Run = new float[19];
 }
 public sealed class BoardInput
 {
  public string Hero; public int Day,Level,Low,High;
  public Piece[] Pieces; public RuntimeMindPalaceSocketAssignment[] Sockets;
  public RuntimeLiveCardObservation[] Skills;
  public RuntimeLiveSnapshotObservation Snapshot;
  public RuntimeCombatCatalogIdentity ExpectedCatalog;
  public int HorizonMilliseconds=10000; public double DefenseWeight;
  public int StashLow=0,StashHigh=9; public bool[] BoardUnlocked,StashUnlocked;
  public HashSet<string> RequiredOnBoard,AllowedOnBoard;
  public int ValidationRuns=16;
  public int[] Current {get{return Pieces.Select(p=>p.OriginalPosition).ToArray();}}
 }
 public sealed class RankedLayout
 {
  public int[] Starts; public double Score,Damage,Defense; public int Runs;
 }
 public sealed class SearchResult
 {
  public RankedLayout Best,Baseline; public RankedLayout[] Alternatives;
  public RankedLayout[] DiscoveryLeaders;
  public int Evaluated,ModelCandidates; public double InferenceMilliseconds,ElapsedMilliseconds;
  public int UnknownTemplates;
 }
 public static class Geometry
 {
  public static bool Valid(BoardInput b,int[] starts,bool requireStash=true)
  {
   if(starts==null||b.Pieces==null||starts.Length!=b.Pieces.Length||starts.Length>20)return false;
   var occupied=new HashSet<int>();
   for(int i=0;i<starts.Length;i++)
   {
    if(starts[i]<0&&b.RequiredOnBoard!=null&&b.RequiredOnBoard.Contains(b.Pieces[i].InstanceId))return false;
    if(starts[i]>=0&&b.AllowedOnBoard!=null&&!b.AllowedOnBoard.Contains(b.Pieces[i].InstanceId))return false;
    if(starts[i]==-1)continue;
    if(starts[i]<b.Low||b.Pieces[i].Size<1||b.Pieces[i].Size>3||starts[i]+b.Pieces[i].Size-1>b.High)return false;
    for(int s=starts[i];s<starts[i]+b.Pieces[i].Size;s++)
     if((b.BoardUnlocked!=null && !Unlocked(b.BoardUnlocked,s,b.Low))||!occupied.Add(s))return false;
   }
   return occupied.Count>0 && (!requireStash||PackStash(b,starts)!=null);
  }
  public static bool Unlocked(bool[] mask,int position,int low)
  {
   // The contracts use one boolean for every absolute socket in the corresponding range.
   int i=mask.Length==10?position:position-low;return i>=0&&i<mask.Length&&mask[i];
  }
  public static int[] PackStash(BoardInput b,int[] starts)
  {
   var result=Enumerable.Repeat(-1,starts.Length).ToArray();
   var free=new HashSet<int>(Enumerable.Range(b.StashLow,b.StashHigh-b.StashLow+1).Where(s=>b.StashUnlocked==null||Unlocked(b.StashUnlocked,s,b.StashLow)));
   var order=Enumerable.Range(0,starts.Length).Where(i=>starts[i]<0).OrderByDescending(i=>b.Pieces[i].Size).ToArray();
   if(order.Sum(i=>b.Pieces[i].Size)>free.Count)return null;
   var failed=new HashSet<long>();
   return Pack(0)?result:null;
   bool Pack(int n)
   {
    if(n==order.Length)return true;
    long key=((long)n<<32)|free.Aggregate(0L,(bits,s)=>bits|(1L<<(s-b.StashLow)));
    if(failed.Contains(key))return false;
    int k=order[n],size=b.Pieces[k].Size;
    for(int at=b.StashLow;at+size-1<=b.StashHigh;at++)
    {
     var cells=Enumerable.Range(at,size).ToArray();if(!cells.All(free.Contains))continue;
     foreach(int s in cells)free.Remove(s);result[k]=at;
     if(Pack(n+1))return true;
     foreach(int s in cells)free.Add(s);result[k]=-1;
    }
    failed.Add(key);return false;
   }
  }
  public static string Key(int[] starts){return string.Join(",",starts);}
  public static IEnumerable<int[]> Neighbors(BoardInput b,int[] source)
  {
   for(int i=0;i<source.Length;i++)
   {
    for(int at=b.Low;at+b.Pieces[i].Size-1<=b.High;at++)
    {if(source[i]==at)continue;var c=(int[])source.Clone();c[i]=at;if(Valid(b,c))yield return c;}
    if(source[i]<0)continue;
    var drop=(int[])source.Clone();drop[i]=-1;if(Valid(b,drop))yield return drop;
    for(int j=i+1;j<source.Length;j++)
    {var c=(int[])source.Clone();c[i]=source[j];c[j]=source[i];if(Valid(b,c))yield return c;}
    // Replacing an on-board item with a stash item works regardless of candidate index ordering.
    for(int j=0;j<source.Length;j++)if(source[j]<0)
    {var c=(int[])source.Clone();c[j]=source[i];c[i]=-1;if(Valid(b,c))yield return c;}
   }
  }
 }
}
