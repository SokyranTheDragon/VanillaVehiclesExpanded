﻿using HarmonyLib;
using RimWorld;
using System.Linq;
using Verse;

namespace VanillaVehiclesExpanded
{
    [HarmonyPatch(typeof(Frame), "Destroy")]
    public static class Frame_Destroy_Patch
    {
        public static void Prefix(Frame __instance, DestroyMode mode, out (Map map, IntVec3 pos, Rot4 rotation) __state)
        {
            __state = (__instance.Map, __instance.Position, __instance.Rotation);
            if (GameComponent_VehicleUseTracker.Instance.frameWrecks.ContainsKey(__instance))
            {
                foreach (var thingCost in __instance.BuildDef.CostList)
                {
                    var stackCount = (int)(thingCost.count * 0.2f);
                    while (stackCount > 0)
                    {
                        var thing = __instance.GetDirectlyHeldThings().FirstOrDefault(x => x.def == thingCost.thingDef);
                        if (thing != null)
                        {
                            var split = thing.SplitOff(stackCount);
                            stackCount -= split.stackCount;
                            split.Destroy();
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        public static void Postfix(Frame __instance, DestroyMode mode, (Map map, IntVec3 pos, Rot4 rotation) __state)
        {
            if (GameComponent_VehicleUseTracker.Instance.frameWrecks.TryGetValue(__instance, out var wreckDef))
            {
                GameComponent_VehicleUseTracker.Instance.frameWrecks.Remove(__instance);
                var wreck = ThingMaker.MakeThing(wreckDef);
                GenSpawn.Spawn(wreck, __state.pos, __state.map, __state.rotation);
            }
        }
    }
}
