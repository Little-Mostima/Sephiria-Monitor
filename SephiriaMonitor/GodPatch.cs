using System;
using HarmonyLib;

namespace SephiriaMonitor
{
    /// <summary>
    /// Turns "god mode" into "targetable but takes 0 damage".
    /// UnitAvatar.ApplyDefense(float,int,int) is a small private method that returns the
    /// post-mitigation damage (clamped to >= 1f inside), so a prefix that returns 0 for the
    /// player keeps the AI aggro chain intact (invulnerable units are skipped by
    /// UnitAI_NewBasic.cs:1489-1505) while no damage is ever applied.
    /// </summary>
    [HarmonyPatch(typeof(UnitAvatar), "ApplyDefense")]
    internal static class GodPatch
    {
        private static bool Prefix(UnitAvatar __instance, ref float __result)
        {
            if (!PlayerOps.God)
            {
                return true;
            }
            if (__instance == null || !(__instance is PlayerAvatar))
            {
                return true;
            }
            __result = 0f;
            return false;
        }
    }
}
