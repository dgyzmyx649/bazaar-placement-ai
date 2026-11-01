using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
namespace BazaarPlacementAI
{
 [BepInPlugin("local.ai.bazaar-placement","Bazaar Placement AI","0.1.7")]
 [BepInDependency("local.lookingin.bazaar.combat",BepInDependency.DependencyFlags.HardDependency)]
 public sealed class Plugin:BaseUnityPlugin
 {
  private GameAdapter adapter;private NativeRouter router;private float nextBind;private string directory,lastError;
  private ConfigEntry<int> budget,evaluations;private ConfigEntry<KeyCode> shortcut;
  private ConfigEntry<bool> backgroundWarmup;private bool warmupScheduled;private float nextWarmup;
  private bool guiShortcutPending;private int guiComparisonMode;private float lastShortcut=-100;
  private UIHints hints;private bool roleWarned;
  private void Awake()
  {
   directory=Path.GetDirectoryName(Info.Location);
   budget=Config.Bind("Search","Milliseconds",3500,new ConfigDescription("Combined model/native discovery budget; initialization and validation take additional time.",new AcceptableValueRange<int>(500,15000)));
   evaluations=Config.Bind("Search","MaximumEvaluations",512,new ConfigDescription("Maximum model-guided layouts; native global search uses a separate time slice.",new AcceptableValueRange<int>(8,512)));
   shortcut=Config.Bind("Search","WholeStashKey",KeyCode.G,"Search all stash items in LookingIN's existing panel.");
   backgroundWarmup=Config.Bind("Search","BackgroundWarmup",true,"Load the model and initialize its resident compute worker in the background at game startup. The worker is reused until game exit; no search runs while idle.");
   adapter=new GameAdapter(Path.Combine(directory,"model"));
   Logger.LogInfo("Placement AI 0.1.7: background warmup and resident compute; G whole stash; Alt+G original comparison; Ctrl+G model comparison. Input backend: "+UnityInput.Current.GetType().Name);
  }
  private void Update()
  {
   if(router==null&&Time.unscaledTime>=nextBind)
   {
    nextBind=Time.unscaledTime+3;
    try{router=NativeRouter.Attach(adapter,directory,budget.Value,evaluations.Value,s=>Logger.LogInfo(s));lastError=null;try{hints=new UIHints(adapter.LookingInAssembly,shortcut.Value.ToString());Logger.LogInfo("Updated LookingIN search hints and AI progress labels.");}catch(Exception e){Logger.LogWarning("Search hints unavailable: "+e.GetBaseException().Message);}}
    catch(Exception e){var message=e.GetBaseException().Message;bool role=message.StartsWith("LookingIN type role")||message.StartsWith("LookingIN member role");if(role&&!roleWarned){roleWarned=true;Logger.LogWarning("Cannot attach to this LookingIN version: "+message+". LookingIN's original search stays in use; please report this line.");}else if(!role&&message!=lastError)Logger.LogDebug("Waiting for LookingIN: "+message);lastError=message;}
   }
   if(router!=null&&backgroundWarmup.Value&&!warmupScheduled&&Time.unscaledTime>=nextWarmup)
   {
    nextWarmup=Time.unscaledTime+5;
    try{var expected=adapter.ExpectedCatalog;warmupScheduled=true;router.Warmup(expected);}
    catch(Exception e){Logger.LogDebug("Waiting to schedule warmup: "+e.GetBaseException().Message);}
   }
   hints?.Refresh();
   bool pressed=guiShortcutPending;int comparison=guiComparisonMode;guiShortcutPending=false;guiComparisonMode=0;
   if(UnityInput.Current.GetKeyDown(shortcut.Value)){pressed=true;comparison=UnityInput.Current.GetKey(KeyCode.LeftAlt)||UnityInput.Current.GetKey(KeyCode.RightAlt)?1:UnityInput.Current.GetKey(KeyCode.LeftControl)||UnityInput.Current.GetKey(KeyCode.RightControl)?2:comparison;}
   if(!pressed||Time.unscaledTime-lastShortcut<.2f)return;
   lastShortcut=Time.unscaledTime;
   Logger.LogInfo("Whole-stash hotkey received: "+shortcut.Value);
   if(router==null){Logger.LogInfo("G search blocked: LookingIN service not ready.");return;}
   if(Typing()){Logger.LogInfo("G search blocked: a text input is focused.");return;}
   try{NativeRouter.NextComparisonMode=comparison;if(adapter.StartWholeStash())Logger.LogInfo("G search queued: all stash candidates; comparison="+comparison);else{NativeRouter.NextComparisonMode=0;Logger.LogInfo("G search declined by LookingIN's current-state checks.");}}
   catch(Exception e)
   {
    NativeRouter.NextComparisonMode=0;
    string message="全背包搜索未开始："+e.GetBaseException().Message;Logger.LogInfo(message);
    try{adapter.ReportSearchMessage(message);}catch(Exception report){Logger.LogDebug(report.Message);}
   }
  }
  // IMGUI input fallback only: this draws no panel and leaves the event available to other UI.
  private void OnGUI()
  {
   var e=Event.current;
   if(e!=null&&e.type==EventType.KeyDown&&e.keyCode==shortcut.Value){guiShortcutPending=true;guiComparisonMode=e.alt?1:e.control?2:0;}
  }
  private bool Typing()
  {
   // keyboardControl also identifies buttons/sliders and may remain nonzero across frames.
   // LookingIN exposes the actual focus state of its IMGUI defense-weight text field.
   if(adapter.IsTextInputFocused)return true;
   var type=Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
   var selected=GameAdapter.Read(type?.GetProperty("current")?.GetValue(null,null),"currentSelectedGameObject") as GameObject;
   if(selected!=null)foreach(var c in selected.GetComponents<Component>())if(c!=null&&Convert.ToBoolean(GameAdapter.Read(c,"isFocused")??false))return true;
   return false;
  }
  private void OnDestroy(){hints?.Dispose();router?.Dispose();}
 }
}
