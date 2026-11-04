using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FMODUnity;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Read-only dumps for the "systems" domain: quests, UI panels, audio, miniscript, shops.
    /// Everything here is observation only — no state is changed.
    /// </summary>
    internal static class SystemOps
    {
        public static JObject Systems(string kind, int limit)
        {
            if (limit <= 0) limit = 400;
            switch ((kind ?? "quests").ToLowerInvariant())
            {
                case "quests": return Quests(limit);
                case "panels": return Panels(limit);
                case "audio": return Audio(limit);
                case "miniscript": return Miniscript(limit);
                case "shop": return Shops(limit);
                default:
                    return new JObject
                    {
                        ["error"] = "unknown kind",
                        ["kinds"] = new JArray("quests", "panels", "audio", "miniscript", "shop"),
                    };
            }
        }

        // ---------------------------------------------------------------- quests

        private static JObject Quests(int limit)
        {
            var result = new JObject();
            var quests = new JArray();
            try
            {
                foreach (var q in Resources.LoadAll<QuestEntity>("Quest").Take(limit))
                {
                    if (q == null) continue;
                    quests.Add(new JObject
                    {
                        ["kind"] = "quest",
                        ["id"] = q.id,
                        ["name"] = Text(q.questName),
                        ["description"] = Text(q.questDescription),
                        ["acceptCriteria"] = q.acceptCriteria,
                        ["rewardsRaw"] = q.rewards,
                        ["rewards"] = Rewards(q.rewards),
                        ["next"] = q.nextQuest != null ? q.nextQuest.id : null,
                        ["objectives"] = Objectives(q.objectivePrefabs),
                        ["failCriteria"] = Objectives(q.failCriteriaPrefabs),
                    });
                }
            }
            catch (Exception e) { result["questError"] = e.GetType().Name + ": " + e.Message; }
            result["quests"] = quests;

            var destiny = new JArray();
            try
            {
                foreach (var q in Resources.LoadAll<DestinyQuestEntity>("DestinyQuest").Take(limit))
                {
                    if (q == null) continue;
                    destiny.Add(new JObject
                    {
                        ["kind"] = "destiny",
                        ["id"] = q.id,
                        ["name"] = Text(q.questName),
                        ["description"] = Text(q.questDescription),
                        ["npc"] = q.targetNPC != null ? q.targetNPC.name : null,
                        ["autoAcceptOnStart"] = q.autoAcceptOnStart,
                        ["rewardsRaw"] = q.rewards,
                        ["rewards"] = Rewards(q.rewards),
                        ["next"] = q.nextQuest != null ? q.nextQuest.id : null,
                        ["objectives"] = Objectives(q.objectivePrefabs),
                    });
                }
            }
            catch (Exception e) { result["destinyError"] = e.GetType().Name + ": " + e.Message; }
            result["destinyQuests"] = destiny;

            var main = new JArray();
            try
            {
                foreach (var n in Resources.LoadAll<MainQuestNodeEntity>("MainQuest").Take(limit))
                {
                    if (n == null) continue;
                    main.Add(new JObject
                    {
                        ["nodeID"] = n.nodeID,
                        ["title"] = Text(n.questTitle),
                        ["objectives"] = new JArray(n.objectiveNames.Select(Text)),
                        ["chapter"] = n.questChapterNum,
                        ["subChapter"] = n.subChapterNum,
                        ["order"] = n.questOrder,
                        ["stream"] = n.streamNumber,
                        ["targetRaceId"] = n.targetRaceId,
                        ["isEndOfChapter"] = n.isEndOfChapter,
                        ["sideStories"] = new JArray(n.relatedSideStoryStages),
                    });
                }
            }
            catch (Exception e) { result["mainError"] = e.GetType().Name + ": " + e.Message; }
            result["mainQuests"] = main;
            return result;
        }

        private static JArray Objectives(QuestCheckMethod[] methods)
        {
            var arr = new JArray();
            if (methods == null) return arr;
            foreach (var m in methods)
            {
                if (m == null) continue;
                arr.Add(new JObject
                {
                    ["text"] = Text(m.objectiveString),
                    ["floor"] = m.questMarkerFloorName,
                    ["object"] = m.questMarkerObjectName,
                    ["objects"] = new JArray(m.questMarkerObjectNames ?? new string[0]),
                    ["type"] = m.GetType().Name,
                });
            }
            return arr;
        }

        private static JArray Rewards(string raw)
        {
            var arr = new JArray();
            if (string.IsNullOrEmpty(raw)) return arr;
            try
            {
                foreach (var r in QuestDatabase.ParseRewards(raw))
                {
                    if (r == null) continue;
                    arr.Add(new JObject
                    {
                        ["type"] = r.type.ToString(),
                        ["gold"] = r.goldValue,
                        ["item"] = r.itemValue != null ? r.itemValue.id : 0,
                        ["weapon"] = r.weaponValue != null ? r.weaponValue.id : 0,
                        ["status"] = r.status,
                        ["rerollDice"] = r.rerollDiceValue,
                        ["destinyKey"] = r.destinyChangeKey,
                    });
                }
            }
            catch (Exception e) { arr.Add(new JObject { ["error"] = e.GetType().Name }); }
            return arr;
        }

        private static string Text(LocalizedString s)
        {
            try { return s == null ? "" : s.ToString(); } catch { return ""; }
        }

        // ---------------------------------------------------------------- panels

        private static JObject Panels(int limit)
        {
            var result = new JObject();
            var types = new JArray();
            try
            {
                var asm = typeof(UIBase).Assembly;
                foreach (var t in asm.GetTypes()
                    .Where(t => typeof(UIBase).IsAssignableFrom(t) && !t.IsAbstract)
                    .OrderBy(t => t.Name).Take(limit))
                {
                    types.Add(new JObject
                    {
                        ["name"] = t.Name,
                        ["base"] = t.BaseType != null ? t.BaseType.Name : "",
                        ["namespace"] = t.Namespace ?? "",
                    });
                }
            }
            catch (Exception e) { result["typeError"] = e.GetType().Name + ": " + e.Message; }
            result["types"] = types;

            var live = new JArray();
            try
            {
                var mgr = UIManager.Instance;
                if (mgr != null)
                {
                    int depth = 0;
                    foreach (var layer in mgr.AllControlStack)
                    {
                        foreach (var ui in layer)
                        {
                            if (ui == null) continue;
                            live.Add(new JObject
                            {
                                ["depth"] = depth,
                                ["name"] = ui.name,
                                ["type"] = ui.GetType().Name,
                            });
                        }
                        depth++;
                    }
                }
            }
            catch (Exception e) { result["liveError"] = e.GetType().Name + ": " + e.Message; }
            result["live"] = live;
            return result;
        }

        // ---------------------------------------------------------------- audio

        private static JObject Audio(int limit)
        {
            var result = new JObject();
            try
            {
                var sm = SoundManager.Instance;
                if (sm != null)
                {
                    result["master"] = sm.GetMasterVolume();
                    result["fx"] = sm.GetFXVolume();
                    result["bgm"] = sm.GetBGMVolume();
                    result["ambience"] = sm.GetAmbienceVolume();
                    result["currentBgm"] = EventPath(sm.currentPlayingBGMEvent);
                }
            }
            catch (Exception e) { result["managerError"] = e.GetType().Name + ": " + e.Message; }

            var emitters = new JArray();
            try
            {
                foreach (var e in UnityEngine.Object.FindObjectsByType<StudioEventEmitter>(UnityEngine.FindObjectsSortMode.None).Take(limit))
                {
                    if (e == null) continue;
                    emitters.Add(new JObject
                    {
                        ["object"] = e.gameObject.name,
                        ["event"] = EventPath(e.EventReference),
                        ["playOnAwake"] = e.PlayEvent.ToString(),
                    });
                }
            }
            catch (Exception e) { result["emitterError"] = e.GetType().Name + ": " + e.Message; }
            result["emitters"] = emitters;
            return result;
        }

        private static string EventPath(FMODUnity.EventReference r)
        {
            try
            {
                var p = typeof(FMODUnity.EventReference).GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(r) as string;
                var f = typeof(FMODUnity.EventReference).GetField("path", BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(r) as string;
            }
            catch { }
            return "";
        }

        // ---------------------------------------------------------------- miniscript

        private static JObject Miniscript(int limit)
        {
            var result = new JObject();
            var list = new JArray();
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<MiniscriptBehaviour>(UnityEngine.FindObjectsSortMode.None);
                result["count"] = all.Length;
                foreach (var b in all.Take(limit))
                {
                    if (b == null) continue;
                    var script = b.miniscript ?? "";
                    list.Add(new JObject
                    {
                        ["object"] = b.gameObject.name,
                        ["length"] = script.Length,
                        ["script"] = script.Length > 400 ? script.Substring(0, 400) : script,
                    });
                }
            }
            catch (Exception e) { result["error"] = e.GetType().Name + ": " + e.Message; }
            result["behaviours"] = list;
            return result;
        }

        // ---------------------------------------------------------------- shops

        private static JObject Shops(int limit)
        {
            var result = new JObject();
            var inv = new JArray();
            try
            {
                foreach (var s in UnityEngine.Object.FindObjectsByType<InventoryShop>(UnityEngine.FindObjectsSortMode.None).Take(limit))
                {
                    if (s == null) continue;
                    inv.Add(new JObject
                    {
                        ["object"] = s.gameObject.name,
                        ["price"] = s.price,
                        ["storage"] = s.storage,
                        ["appearRate"] = s.appearRate,
                        ["activated"] = s.activated,
                    });
                }
            }
            catch (Exception e) { result["inventoryError"] = e.GetType().Name + ": " + e.Message; }
            result["inventoryShops"] = inv;

            var exp = new JArray();
            try
            {
                foreach (var s in UnityEngine.Object.FindObjectsByType<ExpShop>(UnityEngine.FindObjectsSortMode.None).Take(limit))
                {
                    if (s == null) continue;
                    exp.Add(new JObject
                    {
                        ["object"] = s.gameObject.name,
                        ["expPrice"] = s.expPrice,
                        ["useRatio"] = s.useRatioExpAmount,
                    });
                }
            }
            catch (Exception e) { result["expError"] = e.GetType().Name + ": " + e.Message; }
            result["expShops"] = exp;
            return result;
        }
    }
}
