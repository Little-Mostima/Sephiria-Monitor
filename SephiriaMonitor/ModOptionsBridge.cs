using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Registers a page in the game's own "Mod Options" screen (Options -> Mods) for this plugin,
    /// so a player can flip debug toggles without touching files. The game API is internal, so
    /// everything here goes through reflection; values persist in DeviceOptions.xml under
    /// "ModOptions:&lt;modId&gt;:&lt;key&gt;" exactly like Workshop mods.
    /// </summary>
    internal static class ModOptionsBridge
    {
        internal const string ModId = "com.surveillance.monitor";
        internal const string DisplayName = "Sephiria Monitor";

        private static bool registered;
        private static bool gaveUp;
        private static object options;
        private static object page;

        internal static void Tick()
        {
            if (registered || gaveUp)
            {
                return;
            }
            if (OptionsBinding.Instance == null)
            {
                return;
            }
            try
            {
                Register();
                registered = true;
                MonitorPlugin.LogInfo("ModOptions: registered page " + ModId + " (3 toggles)");
            }
            catch (Exception e)
            {
                gaveUp = true;
                MonitorPlugin.LogError("ModOptions.Register: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Register()
        {
            Type tOptions = StateApi.FindType("ModOptions");
            Type tRegistry = StateApi.FindType("ModOptionsRegistry");
            Type tToggle = StateApi.FindType("ModToggleOption");
            Type tSlider = StateApi.FindType("ModSliderOption");
            Type tText = StateApi.FindType("ModOptionText");
            if (tOptions == null || tRegistry == null || tToggle == null || tSlider == null || tText == null)
            {
                throw new InvalidOperationException("mod options types missing: options=" + (tOptions != null) + " registry=" + (tRegistry != null) + " toggle=" + (tToggle != null) + " slider=" + (tSlider != null) + " text=" + (tText != null));
            }

            ConstructorInfo optCtor = tOptions.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (optCtor == null)
            {
                throw new MissingMethodException("ModOptions(string)");
            }
            options = optCtor.Invoke(new object[] { ModId });

            MethodInfo getOrCreate = tRegistry.GetMethod("GetOrCreate",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (getOrCreate == null)
            {
                throw new MissingMethodException("ModOptionsRegistry.GetOrCreate");
            }
            page = getOrCreate.Invoke(null, new[] { options });

            PropertyInfo displayName = page.GetType().GetProperty("DisplayName");
            if (displayName != null && displayName.CanWrite)
            {
                displayName.SetValue(page, Text(tText, DisplayName));
            }

            bool god = AddToggle(tOptions, tToggle, tText, "god", "上帝模式（只回血，敌人仍会索敌）", false, OnGod);
            bool invuln = AddToggle(tOptions, tToggle, tText, "invuln", "上帝模式：同时无敌（敌人会丢失目标）", false, OnInvuln);
            bool recordNpc = AddToggle(tOptions, tToggle, tText, "npcDamage", "记录 NPC 互殴伤害（事件量约翻倍）", false, OnRecordNpc);
            float damage = AddSlider(tOptions, tSlider, tText, "damageBonus", "全伤害加成（%，100000 = 一击必杀）", 0f, 200000f, 0f, 5000f);

            PlayerOps.God = god;
            PlayerOps.InvulnMode = invuln;
            Events.SkipNpcDamage = !recordNpc;
            long existing = 0;
            try
            {
                MethodInfo getInt = options.GetType().GetMethod("GetInt", new[] { typeof(string), typeof(int) });
                if (getInt != null)
                {
                    existing = Convert.ToInt64(getInt.Invoke(options, new object[] { "damageBonus", 0 }));
                }
            }
            catch
            {
            }
            if (existing > 0)
            {
                try
                {
                    PlayerOps.SetStat("ALLDAMAGEBONUS", (int)existing);
                }
                catch (Exception e)
                {
                    MonitorPlugin.LogError("ModOptions.damageBonus apply: " + e.Message);
                }
            }
            MonitorPlugin.LogInfo("ModOptions: initial god=" + god + " invuln=" + invuln + " npcDamage=" + recordNpc + " damageBonus=" + damage);
        }

        private static float AddSlider(Type tOptions, Type tSlider, Type tText, string key, string label, float min, float max, float fallback, float step)
        {
            ConstructorInfo ctor = null;
            ConstructorInfo[] all = tSlider.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < all.Length; i++)
            {
                ParameterInfo[] ps = all[i].GetParameters();
                if (ps.Length == 8 && ps[2].ParameterType == typeof(float) && ps[3].ParameterType == typeof(float))
                {
                    ctor = all[i];
                    break;
                }
            }
            if (ctor == null)
            {
                throw new MissingMethodException("ModSliderOption(ModOptions,string,float,float,float,float,ModOptionText,Action<float>)");
            }
            Action<float> changed = delegate (float v) { OnDamageBonus(v); };
            object def = ctor.Invoke(new object[] { options, key, min, max, fallback, step, Text(tText, label), changed });
            AddItem(def);
            PropertyInfo value = def.GetType().GetProperty("Value");
            object current = value != null ? value.GetValue(def) : fallback;
            return current is float ? (float)current : fallback;
        }

        private static void OnDamageBonus(float value)
        {
            try
            {
                if (value > 0f)
                {
                    PlayerOps.SetStat("ALLDAMAGEBONUS", (int)value);
                }
                MonitorPlugin.LogInfo("ModOptions: damageBonus -> " + value);
            }
            catch (Exception e)
            {
                MonitorPlugin.LogError("ModOptions.damageBonus: " + e.Message);
            }
        }

        private static bool AddToggle(Type tOptions, Type tToggle, Type tText, string key, string label, bool fallback, Action<bool> changed)
        {
            ConstructorInfo ctor = null;
            ConstructorInfo[] all = tToggle.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < all.Length; i++)
            {
                ParameterInfo[] ps = all[i].GetParameters();
                if (ps.Length == 5 && ps[2].ParameterType == typeof(bool))
                {
                    ctor = all[i];
                    break;
                }
            }
            if (ctor == null)
            {
                throw new MissingMethodException("ModToggleOption(ModOptions,string,bool,ModOptionText,Action<bool>)");
            }
            object def = ctor.Invoke(new object[] { options, key, fallback, Text(tText, label), changed });
            AddItem(def);
            PropertyInfo value = def.GetType().GetProperty("Value");
            object current = value != null ? value.GetValue(def) : fallback;
            return current is bool ? (bool)current : fallback;
        }

        private static void AddItem(object def)
        {
            MethodInfo add = page.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (add == null)
            {
                throw new MissingMethodException("ModOptionsPage.Add");
            }
            add.Invoke(page, new[] { def });
        }

        private static object Text(Type tText, string value)
        {
            MethodInfo implicitString = tText.GetMethod("op_Implicit",
                BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (implicitString == null)
            {
                throw new MissingMethodException("ModOptionText.op_Implicit(string)");
            }
            return implicitString.Invoke(null, new object[] { value });
        }

        private static void Persist()
        {
            try
            {
                if (OptionsBinding.Instance != null)
                {
                    OptionsBinding.Instance.Save();
                }
            }
            catch (Exception e)
            {
                MonitorPlugin.LogError("ModOptions.Persist: " + e.Message);
            }
        }

        private static void OnGod(bool on)
        {
            PlayerOps.God = on;
            if (!on)
            {
                PlayerOps.GodSet(false, null);
            }
            else
            {
                PlayerOps.GodSet(true, PlayerOps.InvulnMode);
            }
            Persist();
            MonitorPlugin.LogInfo("ModOptions: god -> " + on);
        }

        private static void OnInvuln(bool on)
        {
            PlayerOps.InvulnMode = on;
            PlayerOps.GodSet(PlayerOps.God || on, on);
            Persist();
            MonitorPlugin.LogInfo("ModOptions: invuln -> " + on);
        }

        private static void OnRecordNpc(bool on)
        {
            Events.SkipNpcDamage = !on;
            Persist();
            MonitorPlugin.LogInfo("ModOptions: recordNpcDamage -> " + on);
        }

        /// <summary>Lists every registered page (ours and Workshop mods') with current values.</summary>
        internal static JObject Describe()
        {
            JObject root = new JObject();
            root["modId"] = ModId;
            root["registered"] = registered;
            root["error"] = gaveUp ? "registration failed, see Player.log" : null;
            JArray pages = new JArray();
            try
            {
                Type tRegistry = StateApi.FindType("ModOptionsRegistry");
                FieldInfo field = tRegistry != null ? tRegistry.GetField("pages", BindingFlags.Static | BindingFlags.NonPublic) : null;
                IDictionary dict = field != null ? field.GetValue(null) as IDictionary : null;
                if (dict != null)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        object p = entry.Value;
                        if (p == null)
                        {
                            continue;
                        }
                        JObject row = new JObject();
                        row["modId"] = Convert.ToString(entry.Key);
                        PropertyInfo dn = p.GetType().GetProperty("DisplayName");
                        if (dn != null)
                        {
                            object v = dn.GetValue(p);
                            row["displayName"] = v != null ? v.ToString() : "";
                        }
                        JArray items = new JArray();
                        PropertyInfo itemsProp = p.GetType().GetProperty("Items");
                        IEnumerable list = itemsProp != null ? itemsProp.GetValue(p) as IEnumerable : null;
                        if (list != null)
                        {
                            foreach (object def in list)
                            {
                                if (def == null)
                                {
                                    continue;
                                }
                                JObject it = new JObject();
                                PropertyInfo key = def.GetType().GetProperty("Key");
                                it["key"] = key != null ? (string)key.GetValue(def) : "";
                                PropertyInfo label = def.GetType().GetProperty("Label");
                                if (label != null)
                                {
                                    object lv = label.GetValue(def);
                                    it["label"] = lv != null ? lv.ToString() : "";
                                }
                                PropertyInfo kind = def.GetType().GetProperty("Kind");
                                if (kind != null)
                                {
                                    it["kind"] = kind.GetValue(def).ToString();
                                }
                                PropertyInfo val = def.GetType().GetProperty("Value");
                                if (val != null && val.CanRead)
                                {
                                    try { it["value"] = new JValue(val.GetValue(def)); } catch { }
                                }
                                items.Add(it);
                            }
                        }
                        row["items"] = items;
                        pages.Add(row);
                    }
                }
            }
            catch (Exception e)
            {
                root["error"] = e.GetType().Name + ": " + e.Message;
            }
            root["pages"] = pages;
            return root;
        }
    }
}
