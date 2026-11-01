using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Compact player/world snapshot: this is what the agent reads instead of a screenshot.</summary>
    public static class PlayerState
    {
        public static Dictionary<string, object> Summary()
        {
            Dictionary<string, object> d = Json.Obj();
            d["scene"] = SceneTracker.Current;

            d["t"] = Time.realtimeSinceStartup;
            d["wall"] = DateTime.Now.ToString("HH:mm:ss");

            List<UnityEngine.Object> players = StateApi.Instances(typeof(PlayerAvatar));
            d["playerCount"] = players.Count;
            PlayerAvatar player = players.Count > 0 ? players[0] as PlayerAvatar : null;
            if (player != null)
            {
                List<object> pos = StateApi.Vec(player.transform.position);
                d["pos"] = pos;
                d["hp"] = Math.Round(player.hp, 1);
                d["maxHp"] = Math.Round(player.MaxHp, 1);
                d["mp"] = player.mp;
                d["maxMp"] = player.MaxMp;
                d["money"] = player.Money;
                d["name"] = player.name;
                d["status"] = DescribeStatus(player);
                LevelController lc = player.GetComponent<LevelController>();
                if (lc != null)
                {
                    d["level"] = lc.currentLevel;
                    d["exp"] = lc.currentExp;
                    d["expRatio"] = Math.Round(lc.Ratio, 3);
                    d["statPoints"] = lc.AvailableStatPoint;
                }
            }

            List<UnityEngine.Object> units = StateApi.Instances(typeof(UnitAvatar));
            List<object> near = new List<object>();
            int hostile = 0;
            string playerFaction = player != null ? StateApi.FactionOf(player) : "";
            for (int i = 0; i < units.Count; i++)
            {
                UnitAvatar u = units[i] as UnitAvatar;
                if (u == null || u == player)
                {
                    continue;
                }
                float dist = player != null ? Vector3.Distance(player.transform.position, u.transform.position) : 0f;
                if (u.hp <= 0f)
                {
                    continue;
                }
                bool isHostile = StateApi.IsHostileToPlayer(u, playerFaction);
                if (dist <= 30f && near.Count < 24)
                {
                    near.Add(Json.Obj().Add("n", u.name).Add("t", u.GetType().Name).Add("hp", Math.Round(u.hp, 1))
                        .Add("maxHp", Math.Round(u.MaxHp, 1)).Add("d", Math.Round(dist, 1)).Add("pos", StateApi.Vec(u.transform.position))
                        .Add("faction", StateApi.FactionOf(u)).Add("hostile", isHostile));
                }
                if (isHostile)
                {
                    hostile++;
                }
            }
            d["unitCount"] = units.Count;
            d["hostile"] = hostile;
            d["nearby"] = near;
            d["damage"] = Events.DamageCount;

            d["events"] = Events.Count;
            d["cursor"] = Events.Cursor;
            d["floor"] = Events.LastFloor;
            d["kills"] = Events.KillCount;
            d["rooms"] = FloorDump.RoomProgress();
            // Frame-integrated motion: external polling cannot see dashes, this can.
            d["move"] = Movement.Summary();

            return d;
        }

        /// <summary>
        /// Active status effects live on the separate StatusEffectController component
        /// (private Dictionary&lt;StatusEffectEntity, StatusEffectModule&gt; activated).
        /// </summary>
        private static object DescribeStatus(UnitAvatar u)
        {
            try
            {
                StatusEffectController sec = u.GetComponent<StatusEffectController>();
                if (sec == null)
                {
                    return null;
                }
                System.Reflection.FieldInfo f = typeof(StatusEffectController).GetField("activated",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                System.Collections.IEnumerable pairs = f == null ? null : f.GetValue(sec) as System.Collections.IEnumerable;
                if (pairs == null)
                {
                    return null;
                }
                List<object> names = new List<object>();
                foreach (object pair in pairs)
                {
                    if (pair == null)
                    {
                        continue;
                    }
                    if (names.Count >= 12)
                    {
                        break;
                    }
                    System.Reflection.FieldInfo keyField = pair.GetType().GetField("key",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    object key = keyField == null ? pair : keyField.GetValue(pair);
                    UnityEngine.Object so = key as UnityEngine.Object;
                    names.Add(so != null ? so.name : (key == null ? "?" : key.ToString()));
                }
                return names;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Write out/state.json every second so a plain file read also works.</summary>
        public static void WriteFile()
        {
            if (Session.Root == null)
            {
                return;
            }
            try
            {
                string path = Path.Combine(Session.Root, "state.json");
                File.WriteAllText(path, Json.Write(Summary()), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("state.json: " + ex.Message);
            }
        }
    }
}
