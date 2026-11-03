using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Floor generators: which preset room directories each floor prefab uses, and what rooms
    /// live in those directories (Resources/Rooms/&lt;dir&gt;/*.xml via ProceduralFloorMetadataBaker).
    /// </summary>
    internal static class FloorGenOps
    {
        internal static string Generators(string q, string dir, int limit)
        {
            JObject root = new JObject();
            JArray rows = new JArray();
            try
            {
                if (dir == "current")
                {
                    return Current();
                }
                if (!string.IsNullOrEmpty(dir))
                {
                    root["dir"] = dir;
                    if (dir.StartsWith("*", StringComparison.Ordinal))
                    {
                        string needle = dir.Substring(1);
                        JArray names = new JArray();
                        TextAsset[] allRooms = Resources.LoadAll<TextAsset>("Rooms");
                        root["allRooms"] = allRooms != null ? allRooms.Length : 0;
                        if (allRooms != null)
                        {
                            foreach (TextAsset a in allRooms)
                            {
                                if (a != null && (needle.Length == 0 || a.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    names.Add(a.name);
                                }
                            }
                        }
                        root["names"] = names;
                        return root.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    root["rooms"] = Rooms(dir);
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }

                EnhancedProceduralFloorGenerator[] all = Resources.FindObjectsOfTypeAll<EnhancedProceduralFloorGenerator>();
                root["generators"] = all.Length;
                HashSet<string> dirs = new HashSet<string>();
                foreach (EnhancedProceduralFloorGenerator g in all)
                {
                    if (g == null)
                    {
                        continue;
                    }
                    string name = g.gameObject != null ? g.gameObject.name : "";
                    if (!string.IsNullOrEmpty(q) && name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    JObject r = new JObject();
                    r["name"] = name;
                    r["scene"] = g.gameObject != null && g.gameObject.scene.IsValid();
                    JObject presets = new JObject();
                    Add(presets, "Entrance", g.presetDirectory_Entrance, dirs);
                    Add(presets, "Entrance_Exp", g.presetDirectory_Entrance_Exp, dirs);
                    Add(presets, "Exit", g.presetDirectory_Exit, dirs);
                    Add(presets, "Essential", g.presetDirectory_Essential, dirs);
                    Add(presets, "Random", g.presetDirectory_Random, dirs);
                    Add(presets, "WitchHat", g.presetDirectory_WitchHat, dirs);
                    Add(presets, "Lucky", g.presetDirectory_Lucky, dirs);
                    Add(presets, "Merchant", g.presetDirectory_Merchant, dirs);
                    Add(presets, "Traveler", g.presetDirectory_Traveler, dirs);
                    Add(presets, "Combat", g.presetDirectory_Combat, dirs);
                    Add(presets, "Event", g.presetDirectory_Event, dirs);
                    Add(presets, "PocketDimension", g.presetDirectory_PocketDimension, dirs);
                    Add(presets, "Road", g.presetDirectory_Road, dirs);
                    if (g.presetDirectory_Hidden != null)
                    {
                        JArray hidden = new JArray();
                        foreach (string h in g.presetDirectory_Hidden)
                        {
                            if (!string.IsNullOrEmpty(h))
                            {
                                hidden.Add(h);
                                dirs.Add(h);
                            }
                        }
                        presets["Hidden"] = hidden;
                    }
                    r["presets"] = presets;
                    r["customPresetDirectoryQuery"] = g.customPresetDirectoryQuery;
                    r["combatRoomCount"] = g.combatRoomCount;
                    r["weakCombatRoomCount"] = g.weakCombatRoomCount;
                    r["roadRoomCreateChance"] = g.roadRoomCreateChance;
                    r["roadRoomCreateRatio"] = g.roadRoomCreateRatio;
                    r["roomCreateErrorChance"] = g.roomCreateErrorChance;
                    r["expBonus"] = g.expBonus;
                    r["additionalHPPercent"] = g.additionalHPPercent;
                    r["additionalAtkPercent"] = g.additionalAtkPercent;
                    r["additionalDefense"] = g.additionalDefense;
                    JArray tags = new JArray();
                    if (g.tagOptions != null)
                    {
                        foreach (EnhancedProceduralFloorGenerator.TagData t in g.tagOptions)
                        {
                            tags.Add(new JObject { ["tag"] = t.tag, ["level"] = t.targetLevel });
                        }
                    }
                    r["tags"] = tags;
                    JArray nodes = new JArray();
                    if (g.floorNodePrefabs != null)
                    {
                        foreach (GameObject go in g.floorNodePrefabs)
                        {
                            if (go != null)
                            {
                                nodes.Add(go.name);
                            }
                        }
                    }
                    r["floorNodes"] = nodes;
                    if (rows.Count < limit)
                    {
                        rows.Add(r);
                    }
                }
                root["dirs"] = new JArray(dirs.OrderBy(x => x, StringComparer.Ordinal));
                root["shown"] = rows.Count;
                root["rows"] = rows;
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void Add(JObject presets, string key, string value, HashSet<string> dirs)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            presets[key] = value;
            dirs.Add(value);
        }

        /// <summary>
        /// The live floor generator in the current scene: every preset room it loaded, grouped by
        /// purpose. This is the only way to see the Essential/WitchHat rooms, whose Resources
        /// folders do not resolve through Resources.LoadAll at runtime.
        /// </summary>
        private static string Current()
        {
            JObject root = new JObject();
            try
            {
                EnhancedProceduralFloorGenerator gen = null;
                foreach (EnhancedProceduralFloorGenerator g in Resources.FindObjectsOfTypeAll<EnhancedProceduralFloorGenerator>())
                {
                    if (g != null && g.gameObject != null && g.gameObject.scene.IsValid())
                    {
                        gen = g;
                        break;
                    }
                }
                if (gen == null)
                {
                    root["error"] = "no live floor generator";
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                root["floor"] = gen.gameObject.name;
                root["type"] = gen.GetType().Name;
                JObject presets = new JObject();
                Add(presets, "Entrance", gen.presetDirectory_Entrance, new HashSet<string>());
                Add(presets, "Entrance_Exp", gen.presetDirectory_Entrance_Exp, new HashSet<string>());
                Add(presets, "Exit", gen.presetDirectory_Exit, new HashSet<string>());
                Add(presets, "Essential", gen.presetDirectory_Essential, new HashSet<string>());
                Add(presets, "Random", gen.presetDirectory_Random, new HashSet<string>());
                Add(presets, "WitchHat", gen.presetDirectory_WitchHat, new HashSet<string>());
                Add(presets, "Lucky", gen.presetDirectory_Lucky, new HashSet<string>());
                Add(presets, "Merchant", gen.presetDirectory_Merchant, new HashSet<string>());
                Add(presets, "Traveler", gen.presetDirectory_Traveler, new HashSet<string>());
                Add(presets, "Combat", gen.presetDirectory_Combat, new HashSet<string>());
                Add(presets, "Event", gen.presetDirectory_Event, new HashSet<string>());
                Add(presets, "PocketDimension", gen.presetDirectory_PocketDimension, new HashSet<string>());
                Add(presets, "Road", gen.presetDirectory_Road, new HashSet<string>());
                root["presets"] = presets;

                JObject byPurpose = new JObject();
                byPurpose["Entrance"] = PresetNames(gen, "entrancePresetList");
                byPurpose["Exit"] = PresetNames(gen, "exitPresetListByPassageDir");
                byPurpose["Combat"] = PresetNames(gen, "combatPresetListByPassageDir");
                byPurpose["Essential"] = PresetNames(gen, "essentialPresetListByPassageDir");
                byPurpose["Event"] = PresetNames(gen, "eventPresetListByPassageDir");
                byPurpose["Hidden"] = PresetNames(gen, "hiddenPresetListByPassageDir");
                byPurpose["Road"] = PresetNames(gen, "roadPresetListByPassage");
                root["loaded"] = byPurpose;
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JArray PresetNames(object gen, string field)
        {
            JArray names = new JArray();
            try
            {
                System.Reflection.FieldInfo fi = gen.GetType().GetField(field,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                object value = fi != null ? fi.GetValue(gen) : null;
                if (value == null)
                {
                    return names;
                }
                System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
                if (list == null)
                {
                    return names;
                }
                foreach (object item in list)
                {
                    if (item == null)
                    {
                        continue;
                    }
                    // Dictionary<int, List<PresetData>> / Dictionary<int, List<TestRoomMetadata>>
                    System.Collections.IDictionary dict = item as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        foreach (object v in dict.Values)
                        {
                            AppendPresetNames(names, v);
                        }
                        continue;
                    }
                    AppendPresetNames(names, item);
                }
            }
            catch (Exception)
            {
            }
            return names;
        }

        private static void AppendPresetNames(JArray names, object value)
        {
            System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
            if (list == null || value is string)
            {
                return;
            }
            foreach (object item in list)
            {
                if (item == null)
                {
                    continue;
                }
                object meta = item;
                System.Reflection.FieldInfo mf = item.GetType().GetField("metadata",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (mf != null)
                {
                    meta = mf.GetValue(item);
                }
                if (meta == null)
                {
                    continue;
                }
                System.Reflection.FieldInfo nf = meta.GetType().GetField("name");
                string name = nf != null ? nf.GetValue(meta) as string : null;
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
        }

        private static JArray Rooms(string dir)
        {
            JArray rooms = new JArray();
            // Load the room XMLs straight from Resources: the baker singleton only exists inside
            // a dungeon scene, and GetPreset would return an empty list at the title screen.
            TextAsset[] assets = Resources.LoadAll<TextAsset>("Rooms/" + dir);
            if (assets == null)
            {
                return rooms;
            }
            foreach (TextAsset asset in assets)
            {
                if (asset == null)
                {
                    continue;
                }
                ProceduralFloorMetadataBaker.TestRoomMetadata m;
                try
                {
                    m = ProceduralFloorMetadataBaker.ImportFromTextAsset(asset);
                }
                catch (Exception)
                {
                    continue;
                }
                JObject r = new JObject();
                r["name"] = m.name;
                r["type"] = m.type.ToString();
                r["tag"] = m.tag;
                r["spawnMonster"] = m.spawnMonster;
                r["monsterDensity"] = m.monsterDensity;
                JArray passages = new JArray();
                if (m.hasPassage != null)
                {
                    for (int i = 0; i < m.hasPassage.Length; i++)
                    {
                        if (m.hasPassage[i])
                        {
                            passages.Add(i);
                        }
                    }
                }
                r["passages"] = passages;
                rooms.Add(r);
            }
            return rooms;
        }
    }
}
