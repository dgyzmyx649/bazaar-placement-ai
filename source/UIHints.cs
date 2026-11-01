using System;using System.Collections;using System.Collections.Generic;using System.Linq;using System.Reflection;using HarmonyLib;
namespace BazaarPlacementAI
{
 public sealed class UIHints:IDisposable
 {
  private readonly Harmony harmony=new Harmony("local.ai.bazaar-placement.hints");
  private readonly IDictionary labels;private readonly Dictionary<object,object> originals=new Dictionary<object,object>();private bool? state;
  public static string WholeStashKey="G";
  public UIHints(Assembly assembly,string key)
  {
   WholeStashKey=key;
   var types=LookingInTypes.For(assembly);var localization=types.Localize;
   harmony.Patch(localization,postfix:new HarmonyMethod(typeof(UIHints),nameof(Text)));
   var coordinator=types.Coordinator;
   harmony.Patch(coordinator.GetProperty("OptimizationTimeBudgetMilliseconds").GetGetMethod(),prefix:new HarmonyMethod(typeof(UIHints),nameof(Budget)));
   harmony.Patch(coordinator.GetProperty("OptimizationCoveragePercentage").GetGetMethod(),prefix:new HarmonyMethod(typeof(UIHints),nameof(Coverage)));
   labels=types.Labels.GetFields(BindingFlags.Static|BindingFlags.NonPublic).Where(f=>f.FieldType.IsGenericType&&f.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)).Select(f=>(IDictionary)f.GetValue(null)).Single(d=>d.Contains("正在寻找更优排列"));
   foreach(string text in new[]{"正在寻找更优排列","正在计算当前场上最优排列","暂时无法生成推荐排列"})if(labels.Contains(text))originals[text]=labels[text];
  }
  public void Refresh()
  {
   bool ai=NativeRouter.LastSearchWasAI;if(state==ai)return;state=ai;
   foreach(var pair in originals)
   {
    var type=pair.Value.GetType();string text=pair.Key.ToString();
    labels[pair.Key]=ai&&text!="暂时无法生成推荐排列"?Activator.CreateInstance(type,new object[]{"AI 候选排序＋原核心细化…","AI ranking + native refinement…"}):pair.Value;
   }
  }
  public static void Text(ref string __result)
  {
   bool ai=NativeRouter.LastSearchWasAI;
   switch(__result)
   {
    case "计算当前场上最优排列":__result="上阵排布搜索；"+WholeStashKey+" 全背包；中键：背包左侧候选。无 PIN 自动选择快速原搜索或 AI 辅助搜索，有 PIN 用原方法。";break;
    case "Calculate the best arrangement for the current board":__result="Board search; "+WholeStashKey+": whole stash; middle-click: stash prefix. No PIN: automatic native/AI routing; PIN: original search.";break;
    case "正在计算当前场上最优排列":if(ai)__result="AI 候选排序＋原核心细化中；准备与复核另计";break;
    case "Calculating the best arrangement for the current board":if(ai)__result="AI ranking + native refinement; preparation/validation are additional";break;
    case "背包替换":if(ai)__result="AI 背包搜索";break;
    case "最佳排列":if(ai)__result="AI 排布搜索";break;
    case "stash replacement":if(ai)__result="AI stash search";break;
    case "Best arrangement":if(ai)__result="AI placement search";break;
    case "秒 · 覆盖 ":if(ai)__result="秒发现预算 · ";break;
    case "s · Coverage ":if(ai)__result="s discovery budget · ";break;
    case "搜索覆盖 ":if(ai)__result="采样验证 · ";break;
   }
  }
  public static bool Budget(ref int __result){if(!NativeRouter.AIActive)return true;__result=NativeRouter.DiscoveryBudget;return false;}
  public static bool Coverage(ref string __result){if(!NativeRouter.AIActive)return true;__result=NativeRouter.ProgressEvaluated+" 次评估 · 非穷举";return false;}
  public void Dispose(){foreach(var p in originals)labels[p.Key]=p.Value;harmony.UnpatchSelf();}
 }
}
