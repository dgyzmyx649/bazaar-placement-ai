using System;using System.Linq;using System.Collections.Generic;
namespace BazaarPlacementAI
{
 public static class CandidateMoves
 {
  public static IEnumerable<int[]> Around(BoardInput b,int[] source)
  {
   foreach(var a in Geometry.Neighbors(b,source))yield return a;
   var on=Enumerable.Range(0,source.Length).Where(i=>source[i]>=0&&(b.RequiredOnBoard==null||!b.RequiredOnBoard.Contains(b.Pieces[i].InstanceId))).ToArray();
   var off=Enumerable.Range(0,source.Length).Where(i=>source[i]<0&&(b.AllowedOnBoard==null||b.AllowedOnBoard.Contains(b.Pieces[i].InstanceId))).ToArray();
   foreach(int drop in on)
   {
    for(int j=0;j<off.Length;j++)for(int k=j+1;k<off.Length;k++)
    {
     foreach(var a in Exchange(b,source,new[]{drop},new[]{off[j],off[k]}))yield return a;
     if(b.Pieces[drop].Size<3)continue;
     for(int l=k+1;l<off.Length;l++)foreach(var a in Exchange(b,source,new[]{drop},new[]{off[j],off[k],off[l]}))yield return a;
    }
   }
   for(int i=0;i<on.Length;i++)for(int j=i+1;j<on.Length;j++)
   {
    int capacity=b.Pieces[on[i]].Size+b.Pieces[on[j]].Size;if(capacity>3)continue;
    foreach(int add in off)foreach(var a in Exchange(b,source,new[]{on[i],on[j]},new[]{add}))yield return a;
   }
  }
  private static IEnumerable<int[]> Exchange(BoardInput b,int[] source,int[] dropped,int[] added)
  {
   int outCells=dropped.Sum(i=>b.Pieces[i].Size),inCells=added.Sum(i=>b.Pieces[i].Size);
   if(inCells!=outCells)yield break;
   var layout=(int[])source.Clone();foreach(int i in dropped)layout[i]=-1;
   var free=new HashSet<int>(Enumerable.Range(b.Low,b.High-b.Low+1).Where(s=>b.BoardUnlocked==null||Geometry.Unlocked(b.BoardUnlocked,s,b.Low)));
   for(int i=0;i<layout.Length;i++)if(layout[i]>=0)for(int s=layout[i];s<layout[i]+b.Pieces[i].Size;s++)free.Remove(s);
   foreach(var result in Place(0))yield return result;
   IEnumerable<int[]> Place(int index)
   {
    if(index==added.Length){if(Geometry.Valid(b,layout))yield return (int[])layout.Clone();yield break;}
    int piece=added[index],size=b.Pieces[piece].Size;
    for(int pos=b.Low;pos+size-1<=b.High;pos++)
    {
     var slots=Enumerable.Range(pos,size).ToArray();if(!slots.All(free.Contains))continue;
     foreach(int s in slots)free.Remove(s);layout[piece]=pos;
     foreach(var a in Place(index+1))yield return a;
     foreach(int s in slots)free.Add(s);layout[piece]=-1;
    }
   }
  }
 }
}
