using System;using System.Linq;using System.Collections.Generic;using LookingIN.Compute.Contracts;
namespace BazaarPlacementAI
{
 public static class SearchPolicy
 {
  public static bool UseModel(RuntimeLiveSnapshotObservation snapshot,RuntimePlacementOptimizationOptions options)
  {
   if(snapshot?.Player?.Cards==null||options==null)return false;
   if(snapshot.Player.Cards.Any(c=>c.Zone==RuntimeLiveCardZone.SocketEffect))return true;
   var candidates=new HashSet<string>(options.StashCandidateInstanceIds??Array.Empty<string>(),StringComparer.Ordinal);
   int count=snapshot.Player.Cards.Count(c=>c.CardType=="Item"&&(c.Zone==RuntimeLiveCardZone.Board||c.Zone==RuntimeLiveCardZone.Stash&&candidates.Contains(c.InstanceId)));
   return count>10;
  }
 }
}
