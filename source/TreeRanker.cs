using System;using System.IO;using System.Linq;using System.Collections.Generic;using Newtonsoft.Json.Linq;
namespace BazaarPlacementAI
{
 public sealed class TreeRanker
 {
  private sealed class Node{public int Feature;public double Threshold,Value;public Node Left,Right;public bool Leaf;}
  private readonly Node[] trees;private readonly JObject metadata;
  public double LastMilliseconds{get;private set;}
  public TreeRanker(string directory,JObject meta)
  {
   metadata=meta;var model=JObject.Parse(File.ReadAllText(Path.Combine(directory,"ranker.json")));
   if((string)model["schema"]!="placement-native-score-ranker-v4"||(int)model["feature_count"]!=497)throw new InvalidDataException("Unsupported native-score ranker");
   trees=((JArray)model["trees"]).Select(Read).ToArray();if(trees.Length==0)throw new InvalidDataException("Empty ranker");
  }
  private static Node Read(JToken t)
  {
   if(t["leaf_value"]!=null)return new Node{Leaf=true,Value=(double)t["leaf_value"]};
   if((string)t["decision_type"]!="<=")throw new InvalidDataException("Unsupported ranker split");
   return new Node{Feature=(int)t["split_feature"],Threshold=(double)t["threshold"],Left=Read(t["left_child"]),Right=Read(t["right_child"])};
  }
  public double Score(float[] features)
  {
   if(features.Length!=497)throw new InvalidDataException("Rank feature count mismatch");double score=0;
   foreach(var root in trees){var node=root;while(!node.Leaf)node=features[node.Feature]<=node.Threshold?node.Left:node.Right;score+=node.Value;}return score;
  }
  public int[][] Rank(BoardInput b,IEnumerable<int[]> layouts,int maximum)
  {
   var p=new RankProfiles(b,metadata);return layouts.Where(a=>Geometry.Valid(b,a)).GroupBy(Geometry.Key).Select(g=>g.First()).Select(a=>new{Starts=a,Value=Score(p.Features(a))}).OrderByDescending(a=>a.Value).Take(maximum).Select(a=>a.Starts).ToArray();
  }
  public int[][] Predict(BoardInput b,int maximum=24,int poolSize=2048)
  {
   var clock=System.Diagnostics.Stopwatch.StartNew();var p=new RankProfiles(b,metadata);
   var ranked=p.Generate(poolSize,731903).Select(a=>new{Starts=a,Value=Score(p.Features(a))}).OrderByDescending(a=>a.Value).ToArray();
   Func<int[],string> field=a=>string.Join(",",Enumerable.Range(0,a.Length).Where(i=>a[i]>=0));
   int distinct=ranked.Select(a=>field(a.Starts)).Distinct().Count(),cap=distinct>=maximum/2?3:maximum;
   var seen=new Dictionary<string,int>();var chosen=new List<int[]>();
   foreach(var a in ranked){string key=field(a.Starts);int n;seen.TryGetValue(key,out n);if(n>=cap)continue;seen[key]=n+1;chosen.Add(a.Starts);if(chosen.Count==maximum)break;}
   if(chosen.Count<maximum)foreach(var a in ranked){if(!chosen.Any(c=>Geometry.Key(c)==Geometry.Key(a.Starts)))chosen.Add(a.Starts);if(chosen.Count==maximum)break;}
   LastMilliseconds=clock.Elapsed.TotalMilliseconds;return chosen.ToArray();
  }
 }
}
