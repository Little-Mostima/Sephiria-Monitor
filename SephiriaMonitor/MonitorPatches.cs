using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Harmony hooks: damage events and the in-game GameLogWriter channel.</summary>
    public static class MonitorPatches
    {
        public static int Patched;
        public static int Deduped;
        public static int LateDuplicates;
        public static readonly List<string> Report = new List<string>();
        private static readonly HashSet<string> FrameHits = new HashSet<string>(StringComparer.Ordinal);
        private static int hitFrame = -1;
        // (victim|damage instance|damage object) -> last time this exact hit was recorded.
        private static readonly Dictionary<string, float> RecentHits = new Dictionary<string, float>(StringComparer.Ordinal);
        private static float lastHitSweep;
        private static Harmony harmony;

        public static void Apply()
        {
            harmony = new Harmony(MonitorPlugin.Guid);
            TryDamage(typeof(UnitAvatar));
            TryDamage(typeof(BreakableProp));
            TryDamage(typeof(DamageDummy));
            TryDamage(typeof(CombatBehaviour));
            TryWriteLog();
            TryRegistryHooks();
            TryGameHooks();
            if (MonitorConfig.EnableTitleHooks)
            {
                TryTitleHooks();
            }
            else
            {
                Report.Add("titleHooks:disabled");
            }
            MonitorPlugin.LogInfo("HARMONY|summary|patched=" + Patched + "|" + string.Join(" ", Report.ToArray()));
        }

        private static void TryDamage(Type target)
        {
            try
            {
                MethodInfo m = target.GetMethod("ApplyDamage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(DamageInstance) }, null);
                if (m == null)
                {
                    Report.Add(target.Name + ":missing");
                    return;
                }
                MethodInfo post = typeof(MonitorPatches).GetMethod(nameof(PostApplyDamage), BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(m, null, new HarmonyMethod(post));
                Patched++;
                Report.Add(target.Name + ":OK");
            }
            catch (Exception ex)
            {
                Report.Add(target.Name + ":FAIL:" + ex.GetType().Name + ":" + ex.Message);
                MonitorPlugin.LogError("HARMONY|" + target.Name + "|" + ex);
            }
        }

        /// <summary>
        /// Combat/economy hooks: deaths, experience gains, world item spawns and money deltas.
        /// These supplement the damage hook, which only reports hits.
        /// </summary>
        /// <summary>单位注册表：Awake 登记、OnDestroy 注销，替代每次统计都全场景扫描。</summary>
        private static void TryRegistryHooks()
        {
            PatchPost(typeof(UnitAvatar), "Awake", new Type[0], nameof(PostUnitAwake), "UnitsAwake");
            PatchPost(typeof(UnitAvatar), "OnDestroy", new Type[0], nameof(PostUnitDestroy), "UnitsDestroy");
        }

        public static void PostUnitAwake(UnitAvatar __instance)
        {
            try { UnitRegistry.Register(__instance); }
            catch (Exception ex) { MonitorPlugin.LogError("unit register: " + ex.Message); }
        }

        public static void PostUnitDestroy(UnitAvatar __instance)
        {
            try { UnitRegistry.Unregister(__instance); }
            catch (Exception ex) { MonitorPlugin.LogError("unit unregister: " + ex.Message); }
        }

        private static void TryGameHooks()
        {
            if (!MonitorConfig.EnableGameHooks)
            {
                Report.Add("gameHooks:disabled");
                return;
            }
            PatchPost(typeof(LevelController), "LocalAddExp", new[] { typeof(int) }, nameof(PostAddExp), "AddExp");
            PatchPost(typeof(Item), "Initialize", new[] { typeof(int), typeof(int), typeof(sbyte) }, nameof(PostItemInit), "ItemInit");
            PatchPost(typeof(UnitAvatar), "AddMoney", new[] { typeof(int) }, nameof(PostAddMoney), "AddMoney");
            PatchPost(typeof(UnitAvatar), "SubMoney", new[] { typeof(int) }, nameof(PostSubMoney), "SubMoney");
            // Every chest / drop / shop / quest item reaches the inventory through this one
            // server-side funnel (Item.Initialize only covers items that first exist in the world).
            PatchPost(typeof(GridInventory), "NotifyAddItem", new[] { typeof(NewItemOwnInstance) }, nameof(PostLoot), "ItemAdded");
        }

        public static void PostLoot(GridInventory __instance, NewItemOwnInstance item)
        {
            try
            {
                Events.Loot(__instance, item);
            }
            catch (Exception ex) { MonitorPlugin.LogError("post loot: " + ex.Message); }
        }

        public static void PostAddExp(LevelController __instance, int exp)
        {
            try
            {
                Events.Exp(__instance, exp);
            }
            catch (Exception ex) { MonitorPlugin.LogError("post exp: " + ex.Message); }
        }

        public static void PostItemInit(Item __instance)
        {
            try
            {
                Events.ItemSpawn(__instance);
            }
            catch (Exception ex) { MonitorPlugin.LogError("post item: " + ex.Message); }
        }

        public static void PostAddMoney(UnitAvatar __instance, int m)
        {
            try
            {
                Events.Money(__instance, m, "add");
            }
            catch (Exception ex) { MonitorPlugin.LogError("post money: " + ex.Message); }
        }

        public static void PostSubMoney(UnitAvatar __instance, int m)
        {
            try
            {
                Events.Money(__instance, -m, "sub");
            }
            catch (Exception ex) { MonitorPlugin.LogError("post money: " + ex.Message); }
        }

        private static void TryWriteLog()
        {
            try
            {
                MethodInfo m = typeof(GameLogWriter).GetMethod("WriteLog",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(string), typeof(Color) }, null);
                if (m == null)
                {
                    Report.Add("GameLogWriter:missing");
                    return;
                }
                MethodInfo post = typeof(MonitorPatches).GetMethod(nameof(PostWriteLog), BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(m, null, new HarmonyMethod(post));
                Patched++;
                Report.Add("GameLogWriter:OK");
            }
            catch (Exception ex)
            {
                Report.Add("GameLogWriter:FAIL:" + ex.GetType().Name);
                MonitorPlugin.LogError("HARMONY|GameLogWriter|" + ex);
            }
        }

        /// <summary>Title-screen hooks so the menu stage produces meaningful log lines too.</summary>
        private static void TryTitleHooks()
        {
            PatchPost(typeof(UI_SaveProfilePanel), "OnOpened", new Type[0], nameof(PostPanelOpened), "TitlePanel");
            PatchPost(typeof(UI_SaveProfilePanel), "SelectProfile", new[] { typeof(UI_SaveProfileButton) }, nameof(PostSelectProfile), "TitleSelect");
            PatchPost(typeof(UI_TitleLobby), "GameStart", new Type[0], nameof(PostGameStart), "TitleGameStart");
            PatchPost(typeof(UI_TitleLobby), "NetworkHost", new Type[0], nameof(PostNetworkHost), "TitleNetworkHost");
        }


        private static void PatchPost(Type target, string methodName, Type[] args, string postfixName, string reportKey)
        {
            try
            {
                MethodInfo m = target.GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly |
                    BindingFlags.FlattenHierarchy, null, args, null);
                if (m == null)
                {
                    Report.Add(reportKey + ":missing");
                    return;
                }
                MethodInfo post = typeof(MonitorPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(m, null, new HarmonyMethod(post));
                Patched++;
                Report.Add(reportKey + ":OK");
            }
            catch (Exception ex)
            {
                Report.Add(reportKey + ":FAIL:" + ex.GetType().Name);
                MonitorPlugin.LogError("HARMONY|" + reportKey + "|" + ex.Message);
            }
        }

        public static void PostPanelOpened()
        {
            try
            {
                MonitorPlugin.LogInfo("TITLE|panel_opened|occupancy=" + TitleControl.Occupancy() + "|selected=" + TitleControl.SelectedProfile());
                Events.Note("title", "panel_opened|" + TitleControl.Occupancy());
            }
            catch (Exception ex) { MonitorPlugin.LogError("post panel: " + ex.Message); }
        }

        public static void PostSelectProfile(object[] __args)
        {
            try
            {
                UI_SaveProfileButton b = (__args != null && __args.Length > 0) ? __args[0] as UI_SaveProfileButton : null;
                string slot = b != null ? b.slotName : "?";
                MonitorPlugin.LogInfo("TITLE|profile_selected|" + slot);
                Events.Note("title", "profile_selected|" + slot);
            }
            catch (Exception ex) { MonitorPlugin.LogError("post selectprofile: " + ex.Message); }
        }

        public static void PostGameStart()
        {
            try
            {
                MonitorPlugin.LogInfo("TITLE|game_start_clicked|selected=" + TitleControl.SelectedProfile());
                Events.Note("title", "game_start_clicked");
            }
            catch (Exception ex) { MonitorPlugin.LogError("post gamestart: " + ex.Message); }
        }

        public static void PostNetworkHost()
        {
            try
            {
                bool server = false;
                bool client = false;
                try { server = Mirror.NetworkServer.active; client = Mirror.NetworkClient.active; } catch { }
                MonitorPlugin.LogInfo("TITLE|networkhost|profile=" + TitleControl.SelectedProfile() +
                                      "|server=" + (server ? "1" : "0") + "|client=" + (client ? "1" : "0") +
                                      "|scene=" + SceneTracker.Current);
                Events.Note("title", "networkhost|" + TitleControl.SelectedProfile());
            }
            catch (Exception ex) { MonitorPlugin.LogError("post networkhost: " + ex.Message); }
        }

        public static void PostApplyDamage(CombatBehaviour __instance, object[] __args, EApplyDamageResult __result, MethodBase __originalMethod)
        {
            try
            {
                if (__originalMethod != null && __originalMethod.Name != "ApplyDamage")
                {
                    return;
                }
                DamageInstance damage = (__args != null && __args.Length > 0) ? (__args[0] as DamageInstance) : null;
                if (damage == null)
                {
                    return;
                }
                if (IsDuplicateHit(__instance, damage))
                {
                    return;
                }
                Events.Damage(__instance, damage, __result);
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("post damage: " + ex.Message);
            }
        }

        /// <summary>
        /// Derived overrides (UnitAvatar / BreakableProp / DamageDummy) call the base ApplyDamage,
        /// so one hit reaches this postfix twice, in the same frame and from the same nested call.
        /// Scope the remembered keys to the frame (59% of boss-fight rows were such duplicates, all
        /// multi-target projectile damage) and put the payload in the key so a projectile that
        /// legitimately lands twice on the same target in one frame is still counted twice unless
        /// every field matches. LateDuplicates counts identical rows that only showed up in a
        /// later frame, which would mean the same-frame assumption is wrong.
        /// </summary>
        private static bool IsDuplicateHit(CombatBehaviour victim, DamageInstance damage)
        {
            int frame = Time.frameCount;
            if (frame != hitFrame)
            {
                hitFrame = frame;
                FrameHits.Clear();
            }
            int victimId = victim == null ? 0 : victim.GetInstanceID();
            int damageHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(damage);
            string source = damage.damageObject == null ? "" : damage.damageObject.name;
            string key = victimId + "|" + damageHash + "|" + source;
            if (!FrameHits.Add(key))
            {
                Deduped++;
                return true;
            }

            float now = Time.realtimeSinceStartup;
            if (now - lastHitSweep > 2f)
            {
                RecentHits.Clear();
                lastHitSweep = now;
            }
            float prev;
            if (RecentHits.TryGetValue(key, out prev) && now - prev <= 0.5f)
            {
                LateDuplicates++;
            }
            RecentHits[key] = now;
            return false;
        }

        public static void PostWriteLog(string log, Color color)
        {
            try
            {
                Events.GameLog(log, color);
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("post gamelog: " + ex.Message);
            }
        }
    }
}