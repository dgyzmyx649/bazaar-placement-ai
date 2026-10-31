using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LookingIN.Compute.Client;
using LookingIN.Compute.Contracts;
using Newtonsoft.Json;

namespace BazaarPlacementAI
{
 public sealed class Compute:IDisposable
 {
  private readonly ComputeWorkerService worker;
  private readonly ModelRunner model;
  public int WorkerProcessId {get{return worker.ProcessId;}}
  public Compute(string modelDirectory,string workerPath,string logs,RuntimeCombatCatalogIdentity expected,Action<string> log)
  {
   model=new ModelRunner(modelDirectory);
   try{model.ValidateCatalog(expected);}catch{model.Dispose();throw;}
   try{worker=new ComputeWorkerService(new ComputeClientOptions{WorkerPath=workerPath,WorkerCapacity=4,ClientVersion="BazaarPlacementAI/0.1.6",LogDirectory=logs,ExpectedCatalogIdentity=expected,StartupTimeoutMilliseconds=15000,RequiredCapabilities=new[]{"mind-palace"},Log=log});}
   catch{model.Dispose();throw;}
  }
  public async Task Prepare(BoardInput b,CancellationToken token)
  {
   var prepared=await worker.PrepareMindPalaceAsync(b.Snapshot,token).ConfigureAwait(false);
   var byId=prepared.Items.Where(c=>c.CardType=="Item").ToDictionary(c=>c.InstanceId,StringComparer.Ordinal);
   foreach(var piece in b.Pieces)
   {
    RuntimeMindPalaceCardPrototype p;if(!byId.TryGetValue(piece.InstanceId,out p))throw new InvalidOperationException("计算核心没有完整还原物品："+piece.Name);
    piece.Prototype=p;
    piece.TemplateId=p.TemplateId;piece.Tier=p.Tier;piece.Enchantment=p.Enchantment;
    if(string.IsNullOrWhiteSpace(piece.Name)||piece.Name==piece.Source.TemplateId)piece.Name=model.Name(p.TemplateId);
   }
   b.Sockets=prepared.SocketAssignments.ToArray();
   // Ask the public core for catalog baselines at the actual tier/enchantment; do not count enchantments as run gains.
   var bases=b.Pieces.Select(p=>Clone(p.Prototype)).ToArray();foreach(var p in bases)p.UsesCatalogBase=true;
   var baseline=await worker.MaterializeMindPalacePresentationAsync(b.Snapshot,bases,b.Sockets,true,false,token).ConfigureAwait(false);
   foreach(var piece in b.Pieces)
   {
    var original=baseline.Cards.FirstOrDefault(c=>c.InstanceId==piece.InstanceId);
    if(original==null)throw new InvalidOperationException("计算核心未返回完整基础属性");
    piece.Run=ModelRunner.Growth(piece.Prototype.Attributes,original.Attributes,piece.Name);
   }
  }
  private static RuntimeMindPalaceCardPrototype Clone(RuntimeMindPalaceCardPrototype p){return JsonConvert.DeserializeObject<RuntimeMindPalaceCardPrototype>(JsonConvert.SerializeObject(p));}
  public async Task<RankedLayout> Score(BoardInput b,int[] starts,int runs,uint seed,CancellationToken token)
  {
   var timeline=await Timeline(b,starts,runs,b.HorizonMilliseconds,seed,token).ConfigureAwait(false);
   var last=timeline.Samples.OrderBy(s=>s.TimeMilliseconds).Last();
   return new RankedLayout{Starts=(int[])starts.Clone(),Damage=last.CumulativeDamage,Defense=last.CumulativeDefense,Score=last.CumulativeDamage+b.DefenseWeight*last.CumulativeDefense,Runs=runs};
  }
  public async Task<RuntimeCombatTimelineEvaluation> Timeline(BoardInput b,int[] starts,int runs,int horizon,uint seed,CancellationToken token)
  {
   if(b.Pieces.Any(p=>p.Prototype==null))await Prepare(b,token).ConfigureAwait(false);
   if(!Geometry.Valid(b,starts))throw new InvalidOperationException("无效的棋盘或背包占位");
   var cards=b.Pieces.Select((p,i)=>{var c=Clone(p.Prototype);c.Zone=starts[i]<0?RuntimeLiveCardZone.Stash:RuntimeLiveCardZone.Board;c.Position=starts[i]<0?p.Prototype.Position:starts[i];return c;}).ToArray();
   // Preserve stash context as well as the cards selected for the board.
   var stash=Geometry.PackStash(b,starts);for(int i=0;i<cards.Length;i++)if(starts[i]<0)cards[i].Position=stash[i];
   var evaluation=await worker.SimulateMindPalaceAsync(b.Snapshot,cards,b.Sockets,horizon,runs,horizon,true,seed,token).ConfigureAwait(false);
   var timeline=evaluation.Timeline;
   if(!evaluation.CanExport||timeline==null||!timeline.CanSimulate||!timeline.CanDisplay||timeline.IsFatalCensored||timeline.IsCensored||timeline.Samples.Count==0)
    throw new InvalidOperationException("核心拒绝评估此排布："+evaluation.FailureReason);
   return timeline;
  }
  public async Task<SearchResult> Search(BoardInput b,int milliseconds,int maximumEvaluations,CancellationToken token,Action<string> progress)
  {
   var total=Stopwatch.StartNew();
   int nativeBudget=Math.Min(750,Math.Max(500,milliseconds/3)),nativeEvaluated=0;int[] global=null;
   progress("原核心补充全局候选…");
   var topology=new RuntimeInventoryTopology{BoardMinimumPosition=b.Low,BoardMaximumPosition=b.High,StashMinimumPosition=b.StashLow,StashMaximumPosition=b.StashHigh,BoardUnlockedSockets=Enumerable.Range(0,10).Select(s=>s>=b.Low&&s<=b.High&&(b.BoardUnlocked==null||Geometry.Unlocked(b.BoardUnlocked,s,b.Low))).ToArray(),StashUnlockedSockets=Enumerable.Range(0,10).Select(s=>s>=b.StashLow&&s<=b.StashHigh&&(b.StashUnlocked==null||Geometry.Unlocked(b.StashUnlocked,s,b.StashLow))).ToArray()};
   var globalResult=await worker.OptimizePlacementAsync(b.Snapshot,new RuntimePlacementOptimizationOptions{TimeBudgetMilliseconds=nativeBudget,Parallelism=4,ScoreMaximumMilliseconds=b.HorizonMilliseconds,DefenseWeight=b.DefenseWeight,ComparisonSampleCount=b.ValidationRuns<=50?50:100,ValidationWatchdogMilliseconds=1500,BoardMinimumPosition=b.Low,BoardMaximumPosition=b.High,InventoryTopology=topology,ReplaceableItemInstanceIds=b.Pieces.Where(p=>p.OriginalPosition>=0&&(b.RequiredOnBoard==null||!b.RequiredOnBoard.Contains(p.InstanceId))).Select(p=>p.InstanceId).ToArray(),StashCandidateInstanceIds=b.Pieces.Where(p=>p.OriginalPosition<0&&(b.AllowedOnBoard==null||b.AllowedOnBoard.Contains(p.InstanceId))).Select(p=>p.InstanceId).ToArray(),Seed=81931},token).ConfigureAwait(false);
   if(globalResult.CanOptimize&&globalResult.Recommendation!=null)
   {
    var starts=Enumerable.Repeat(-1,b.Pieces.Length).ToArray();bool known=true;
    foreach(var p in globalResult.Recommendation.Board){int i=Array.FindIndex(b.Pieces,c=>c.InstanceId==p.InstanceId);if(i<0){known=false;break;}starts[i]=p.BoardPosition;}
    if(known&&Geometry.Valid(b,starts)){global=starts;nativeEvaluated=globalResult.Recommendation.EvaluatedArrangementCount;}
   }
   var nativeRecommendation=globalResult.Recommendation;
   if(global!=null&&nativeRecommendation.ArrangementDomainCount64>0&&nativeRecommendation.CoveredArrangementCount64>=nativeRecommendation.ArrangementDomainCount64&&nativeRecommendation.UnprocessedCandidateCount==0&&nativeRecommendation.ValidationApplied&&!nativeRecommendation.ValidationTimedOut&&nativeRecommendation.IndependentValidationCurrentSampleCount>0)
   {
    var best=new RankedLayout{Starts=global,Score=nativeRecommendation.IndependentValidationWeightedScore,Damage=nativeRecommendation.AverageDamageScore,Defense=nativeRecommendation.AverageDefenseScore,Runs=nativeRecommendation.IndependentValidationSampleCount};
    var original=new RankedLayout{Starts=b.Current,Score=nativeRecommendation.IndependentValidationCurrentWeightedScore,Runs=nativeRecommendation.IndependentValidationCurrentSampleCount};
    if(best.Score<original.Score)best=original;
    var small=new[]{best,original};progress("原核心已完成排布搜索与复核");
    return new SearchResult{Best=best,Baseline=original,Alternatives=small,DiscoveryLeaders=small,Evaluated=nativeEvaluated,ModelCandidates=0,InferenceMilliseconds=0,ElapsedMilliseconds=total.Elapsed.TotalMilliseconds};
   }
   progress("正在还原物品属性…");await Prepare(b,token).ConfigureAwait(false);
   progress("AI 正在生成候选…");var candidates=model.Predict(b);var clock=Stopwatch.StartNew();
   var scores=new Dictionary<string,RankedLayout>();var queue=new Queue<int[]>();
   queue.Enqueue(b.Current);if(global!=null)queue.Enqueue(global);foreach(var c in candidates)queue.Enqueue(c);
   var seen=new HashSet<string>();int generation=0;
   while(queue.Count>0 && scores.Count<maximumEvaluations)
   {
    token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds>=Math.Max(100,milliseconds-nativeBudget)&&scores.Count>0)break;
    var batch=new List<int[]>();while(queue.Count>0&&batch.Count<4&&scores.Count+batch.Count<maximumEvaluations)
    {var c=queue.Dequeue();if(Geometry.Valid(b,c)&&seen.Add(Geometry.Key(c)))batch.Add(c);}
    var evaluated=await Task.WhenAll(batch.Select(async c=>
    {
     try{return await Score(b,c,8,7319,token).ConfigureAwait(false);}
     catch(InvalidOperationException e) when(e.Message.StartsWith("核心拒绝评估此排布：")&&Geometry.Key(c)!=Geometry.Key(b.Current)){return null;}
    })).ConfigureAwait(false);
    foreach(var score in evaluated)if(score!=null)scores[Geometry.Key(score.Starts)]=score;
    progress("已评估 "+(scores.Count+nativeEvaluated)+" 个排布");
    if(queue.Count==0)
    {
     var leaders=scores.Values.OrderByDescending(s=>s.Score).Take(3).ToArray();
     var neighbors=leaders.SelectMany(leader=>CandidateMoves.Around(b,leader.Starts)).Where(c=>!seen.Contains(Geometry.Key(c))).GroupBy(Geometry.Key).Select(g=>g.First()).Take(3500).ToArray();
     var ranked=model.RankCandidates(b,neighbors,24);
     int stride=Math.Max(1,neighbors.Length/12),offset=generation%stride;
     var explore=neighbors.Where((c,i)=>i>=offset&&(i-offset)%stride==0).Take(12);
     foreach(var c in ranked.Concat(explore).GroupBy(Geometry.Key).Select(g=>g.First()))queue.Enqueue(c);
     generation++;
    }
   }
   if(!scores.ContainsKey(Geometry.Key(b.Current)))throw new InvalidOperationException("未能完成当前排布的基准评估");
   progress("正在用独立样本复核推荐…");
   var finalists=scores.Values.OrderByDescending(s=>s.Score).Take(24).Select(s=>s.Starts).Concat(global==null?new[]{b.Current}:new[]{b.Current,global}).GroupBy(Geometry.Key).Select(g=>g.First()).ToArray();
   var checkedScores=(await Task.WhenAll(finalists.Select(async c=>
   {try{return await Score(b,c,b.ValidationRuns,91771,token).ConfigureAwait(false);}catch(InvalidOperationException e) when(e.Message.StartsWith("核心拒绝评估此排布：")&&Geometry.Key(c)!=Geometry.Key(b.Current)){return null;}})).ConfigureAwait(false)).Where(s=>s!=null).ToList();
   var baseline=checkedScores.Single(s=>Geometry.Key(s.Starts)==Geometry.Key(b.Current));
   return new SearchResult{Best=checkedScores.OrderByDescending(s=>s.Score).First(),Baseline=baseline,Alternatives=checkedScores.OrderByDescending(s=>s.Score).ToArray(),DiscoveryLeaders=scores.Values.OrderByDescending(s=>s.Score).Take(24).ToArray(),Evaluated=scores.Count+nativeEvaluated,ModelCandidates=candidates.Length,UnknownTemplates=model.UnknownTemplates,InferenceMilliseconds=model.LastMilliseconds,ElapsedMilliseconds=total.Elapsed.TotalMilliseconds};
  }
  public void Dispose(){worker.Dispose();model.Dispose();}
 }
}


