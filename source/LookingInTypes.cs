using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
namespace BazaarPlacementAI
{
 /// <summary>Resolves LookingIN's obfuscated types by structure: non-obfuscated property names, signature shapes and type relationships.
 /// Known names from tested builds are only a fast first try and must pass the same rules. Only System.Reflection APIs and FullName
 /// comparisons are used, so the rules also run offline against a MetadataLoadContext.</summary>
 public sealed class LookingInTypes
 {
  private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  private static readonly Dictionary<Assembly,object> Cache=new Dictionary<Assembly,object>();
  public static bool UseKnownNames=true;
  public readonly Assembly Assembly;
  public readonly Type Coordinator,Capture,Snapshot,Service,Evaluation,Options,ProgressService,Progress,Curve,Topology,Request,Mode,IdentitySource,Arranger,Localization,Labels;
  public readonly MethodInfo SearchMethod,CurveMethod,TopologyBuilder,ArrangerApply,CoordinatorStart,Localize;
  private Type[] all;
  // Rules are deterministic for a loaded assembly, so a failure is cached too (no rescans every bind attempt).
  public static LookingInTypes For(Assembly assembly)
  {
   object v;lock(Cache)if(!Cache.TryGetValue(assembly,out v))try{Cache[assembly]=v=new LookingInTypes(assembly);}catch(Exception e){Cache[assembly]=v=e;}
   if(v is Exception error)throw new InvalidOperationException(error.Message.StartsWith("LookingIN ")?error.Message:"LookingIN type role resolution failed: "+error.GetBaseException().GetType().Name+": "+error.GetBaseException().Message);
   return (LookingInTypes)v;
  }
  public LookingInTypes(Assembly assembly)
  {
   Assembly=assembly;
   // 0.247.6 / 0.248.0 names (fast path only)
   Coordinator=Find("timeline coordinator",t=>t.IsClass&&Has(t,"ObservedCapture","IsOptimizationRunning","IsPvpCurveLocked","ActivePlacementPins","OptimizationTimeBudgetMilliseconds","OptimizationCoveragePercentage"),"h.y","h.x");
   Capture=Rel("observed capture",Prop(Coordinator,"ObservedCapture")?.PropertyType,t=>Has(t,"Snapshot","IsCombat","CardNames"));
   Snapshot=Rel("live snapshot",Prop(Capture,"Snapshot")?.PropertyType,t=>t.IsClass);
   Service=Find("search service",t=>t.IsInterface&&Has(t,"CatalogIdentity")&&SearchOf(t)!=null&&Coordinator.GetFields(Flags).Any(f=>!f.IsStatic&&f.FieldType==t),"C.T","C.S");
   SearchMethod=SearchOf(Service);
   Evaluation=Rel("optimization evaluation",SearchMethod.ReturnType.GetGenericArguments()[0],t=>Has(t,"CanOptimize","Recommendation"));
   Options=Rel("optimization options",SearchMethod.GetParameters()[1].ParameterType,t=>Has(t,"PinnedItems","InventoryTopology","ReplaceableItemInstanceIds","StashCandidateInstanceIds"));
   ProgressService=Find("progress search service",t=>t.IsInterface&&t.GetMethods().Length==1&&ProgressOf(t)!=null,"C.u","C.t");
   Progress=Rel("progress",ProgressOf(ProgressService).GetParameters()[2].ParameterType.GetGenericArguments()[0],t=>Has(t,"Stage","Sequence","CoveredArrangementCount"));
   CurveMethod=Method("timeline curve request",Service.GetMethods().Where(m=>Task1(m.ReturnType)&&m.GetParameters().Length==5&&m.GetParameters()[0].ParameterType==Evaluation));
   Curve=Rel("timeline curve",CurveMethod.ReturnType.GetGenericArguments()[0],t=>Has(t,"CanSimulate"));
   Topology=Rel("inventory topology",Prop(Options,"InventoryTopology")?.PropertyType,t=>t.IsClass);
   Request=Find("movement request",t=>t.IsClass&&!t.IsAbstract&&Has(t,"Mode","CardNames","ReplaceableItemInstanceIds","StashCandidateInstanceIds")&&Prop(t,"Snapshot")?.PropertyType==Snapshot&&Prop(t,"InventoryTopology")?.PropertyType==Topology,"J.n","J.m");
   Mode=Rel("movement mode",Prop(Request,"Mode")?.PropertyType,t=>t.IsEnum);
   IdentitySource=Find("catalog identity source",t=>t.IsAbstract&&t.IsSealed&&t.GetProperties(Flags).Any(p=>p.Name=="Identity"&&p.GetGetMethod(true)?.IsStatic==true&&p.PropertyType==Prop(Service,"CatalogIdentity").PropertyType),"F.e","F.d");
   TopologyBuilder=Unique(Find("inventory topology builder",t=>Unique(t.GetMethods(Flags|BindingFlags.DeclaredOnly).Where(IsTopologyBuilder))!=null,"s.F","s.D").GetMethods(Flags|BindingFlags.DeclaredOnly).Where(IsTopologyBuilder));
   Arranger=Find("movement controller",t=>t.IsClass&&Has(t,"IsRunning","Status")&&ApplyOf(t)!=null,"i.L","i.K");
   ArrangerApply=ApplyOf(Arranger);
   CoordinatorStart=Method("coordinator start",Coordinator.GetMethods(Flags).Where(m=>!m.IsStatic&&Is(m.ReturnType,"System.Boolean")&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==Request));
   Localization=Find("localization",t=>t.IsAbstract&&t.IsSealed&&LocalizeOf(t)!=null&&Statics(t).Any(m=>Is(m.ReturnType,"System.String")&&Params(m,"System.String","System.String")),"I.X","I.W");
   Localize=LocalizeOf(Localization);
   var language=Localize.GetParameters()[0].ParameterType;
   Labels=Find("search labels",t=>t.IsAbstract&&t.IsSealed&&t.GetFields(BindingFlags.Static|BindingFlags.NonPublic).Any(f=>IsLabelTable(f.FieldType))&&Statics(t).Any(m=>Is(m.ReturnType,"System.String")&&m.GetParameters().Length==2&&m.GetParameters()[0].ParameterType==language&&IsPair(m.GetParameters()[1].ParameterType)),"I.Q","I.P");
  }
  // ---- rules ----
  private MethodInfo SearchOf(Type t){return Unique(t.GetMethods().Where(m=>Task1(m.ReturnType)&&Matches(m,3)&&Has(m.ReturnType.GetGenericArguments()[0],"CanOptimize","Recommendation")));}
  private MethodInfo ProgressOf(Type t){return Unique(t.GetMethods().Where(m=>Task1(m.ReturnType)&&Matches(m,4)&&m.ReturnType.GetGenericArguments()[0]==Evaluation&&m.GetParameters()[1].ParameterType==Options&&Generic(m.GetParameters()[2].ParameterType,"System.Action`1")));}
  private bool Matches(MethodInfo m,int n){var p=m.GetParameters();return p.Length==n&&p[0].ParameterType==Snapshot&&Is(p[n-1].ParameterType,"System.Threading.CancellationToken");}
  private bool IsTopologyBuilder(MethodInfo m){var p=m.GetParameters();return m.IsStatic&&Is(m.ReturnType,"System.Boolean")&&p.Length==3&&p[0].ParameterType==Snapshot&&p[1].ParameterType.IsByRef&&p[1].ParameterType.GetElementType()==Topology&&Is(p[2].ParameterType,"System.String&");}
  private MethodInfo ApplyOf(Type t){return Unique(t.GetMethods(Flags).Where(m=>{var p=m.GetParameters();return !m.IsStatic&&Is(m.ReturnType,"System.Boolean")&&p.Length==4&&p[0].ParameterType==Request&&p[1].ParameterType==Evaluation&&Is(p[2].ParameterType,"System.Single");}));}
  private static MethodInfo LocalizeOf(Type t){return Unique(Statics(t).Where(m=>{var p=m.GetParameters();return Is(m.ReturnType,"System.String")&&p.Length==3&&Has(p[0].ParameterType,"IsChinese","IsEnglish")&&Is(p[1].ParameterType,"System.String")&&Is(p[2].ParameterType,"System.String");}));}
  private static bool IsPair(Type t){return t.IsGenericType&&t.GetGenericTypeDefinition().FullName=="System.ValueTuple`2"&&t.GetGenericArguments().All(a=>Is(a,"System.String"));}
  private static bool IsLabelTable(Type t){return t.IsGenericType&&t.GetGenericTypeDefinition().FullName=="System.Collections.Generic.Dictionary`2"&&Is(t.GetGenericArguments()[0],"System.String")&&IsPair(t.GetGenericArguments()[1]);}
  // ---- helpers ----
  private static IEnumerable<MethodInfo> Statics(Type t){return t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly);}
  private static bool Params(MethodInfo m,params string[] names){var p=m.GetParameters();return p.Length==names.Length&&p.Select((x,i)=>Is(x.ParameterType,names[i])).All(x=>x);}
  public static bool Is(Type t,string name){return t!=null&&t.FullName==name;}
  private static bool Generic(Type t,string definition){return t.IsGenericType&&t.GetGenericTypeDefinition().FullName==definition;}
  private static bool Task1(Type t){return Generic(t,"System.Threading.Tasks.Task`1");}
  public static PropertyInfo Prop(Type t,string name){return t?.GetProperties(Flags).FirstOrDefault(p=>p.Name==name);}
  private static bool Has(Type t,params string[] names){return t!=null&&names.All(n=>Prop(t,n)!=null);}
  private static T Unique<T>(IEnumerable<T> items)where T:class{var a=items.Take(2).ToArray();return a.Length==1?a[0]:null;}
  private Type[] AllTypes()
  {
   if(all==null)try{all=Assembly.GetTypes();}catch(ReflectionTypeLoadException e){all=e.Types.Where(t=>t!=null).ToArray();}
   return all;
  }
  private static bool Safe(Func<Type,bool> rule,Type t){try{return rule(t);}catch{return false;}}
  private Type Find(string role,Func<Type,bool> rule,params string[] known)
  {
   if(UseKnownNames)foreach(var name in known){var k=Assembly.GetType(name,false);if(k!=null&&Safe(rule,k))return k;}
   var found=AllTypes().Where(t=>Safe(rule,t)).Take(4).ToArray();
   if(found.Length==1)return found[0];
   throw new InvalidOperationException("LookingIN type role '"+role+"' "+(found.Length==0?"not found":"ambiguous ("+string.Join(", ",found.Select(t=>t.FullName))+")")+" in "+Assembly.GetName().Version);
  }
  private Type Rel(string role,Type t,Func<Type,bool> rule)
  {
   if(t!=null&&Safe(rule,t))return t;
   throw new InvalidOperationException("LookingIN type role '"+role+"' not found"+(t==null?"":" ("+t.FullName+" fails the shape check)")+" in "+Assembly.GetName().Version);
  }
  private MethodInfo Method(string role,IEnumerable<MethodInfo> candidates)
  {
   var found=candidates.Distinct().Take(2).ToArray();
   if(found.Length==1)return found[0];
   throw new InvalidOperationException("LookingIN member role '"+role+"' "+(found.Length==0?"not found":"ambiguous")+" in "+Assembly.GetName().Version);
  }
  public IEnumerable<KeyValuePair<string,string>> Describe()
  {
   foreach(var f in GetType().GetFields(BindingFlags.Public|BindingFlags.Instance))
   {var v=f.GetValue(this);if(v is Type t)yield return new KeyValuePair<string,string>(f.Name,t.FullName);else if(v is MethodInfo m)yield return new KeyValuePair<string,string>(f.Name,m.DeclaringType.FullName+"::"+m.Name+"("+string.Join(",",m.GetParameters().Select(p=>p.ParameterType.Name))+")");}
  }
 }
}
