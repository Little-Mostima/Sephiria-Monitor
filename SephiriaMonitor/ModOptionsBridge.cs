using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SephiriaMonitor
{
    /// <summary>
    /// Registers a page in the game's own "Mod Options" screen (Options -> Gameplay) for this plugin,
    /// so a player can flip debug toggles without touching files. The game API is internal, so
    /// everything here goes through reflection; values persist in DeviceOptions.xml under
    /// "ModOptions:&lt;modId&gt;:&lt;key&gt;" exactly like Workshop mods.
    /// Two rows only: god mode (targetable, damage 0) and one-hit kill.
    /// </summary>
    internal static class ModOptionsBridge
    {
        internal const string ModId = "com.surveillance.monitor";
        internal const string DisplayName = "Sephiria Monitor";

        private static bool registered;
        private static bool gaveUp;
        private static object options;
        private static object page;

        internal static bool OneHitKillEnabled;

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
                MonitorPlugin.LogInfo("ModOptions: page registered for " + ModId);
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
            Type tText = StateApi.FindType("ModOptionText");
            if (tOptions == null || tRegistry == null || tToggle == null || tText == null)
            {
                throw new InvalidOperationException("mod options types missing: options=" + (tOptions != null) + " registry=" + (tRegistry != null) + " toggle=" + (tToggle != null) + " text=" + (tText != null));
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
            SetDisplayName(tText, page, DisplayName);

            bool god = AddToggle(tToggle, tText, "god", "上帝模式（敌人能锁定你，但伤害为 0）", false, OnGod);
            bool oneHit = AddToggle(tToggle, tText, "oneHitKill", "一击必杀（所有伤害 ×1000）", false, OnOneHitKill);

            PlayerOps.InvulnMode = false;
            Events.SkipNpcDamage = true; // NPC-vs-NPC noise is backend policy, not a player-facing toggle
            PlayerOps.GodSet(god, false);
            OneHitKillEnabled = oneHit;
            ApplyOneHitKill(oneHit);
            MonitorPlugin.LogInfo("ModOptions: initial god=" + god + " oneHitKill=" + oneHit);
        }

        private static void SetDisplayName(Type tText, object target, string text)
        {
            try
            {
                PropertyInfo prop = target.GetType().GetProperty("DisplayName");
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(target, Text(tText, text));
                }
            }
            catch (Exception e)
            {
                MonitorPlugin.LogInfo("ModOptions.DisplayName: " + e.Message);
            }
        }

        private static bool AddToggle(Type tToggle, Type tText, string key, string label, bool fallback, Action<bool> changed)
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

        private static void OnGod(bool on)
        {
            PlayerOps.GodSet(on, false);
            Persist();
            MonitorPlugin.LogInfo("ModOptions: god -> " + on);
        }

        private static void OnOneHitKill(bool on)
        {
            OneHitKillEnabled = on;
            ApplyOneHitKill(on);
            Persist();
            MonitorPlugin.LogInfo("ModOptions: oneHitKill -> " + on);
        }

        private static void ApplyOneHitKill(bool on)
        {
            try
            {
                PlayerOps.SetStat("ALLDAMAGEBONUS", on ? 100000 : 0);
            }
            catch (Exception e)
            {
                MonitorPlugin.LogError("ModOptions.oneHitKill: " + e.Message);
            }
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
                                it["key"] = key != null ? Convert.ToString(key.GetValue(def)) : "";
                                PropertyInfo label = def.GetType().GetProperty("Label");
                                if (label != null)
                                {
                                    object lv = label.GetValue(def);
                                    it["label"] = lv != null ? lv.ToString() : "";
                                }
                                PropertyInfo kind = def.GetType().GetProperty("Kind");
                                if (kind != null)
                                {
                                    object kv = kind.GetValue(def);
                                    it["kind"] = kv != null ? kv.ToString() : "";
                                }
                                PropertyInfo value = def.GetType().GetProperty("Value");
                                if (value != null)
                                {
                                    object vv = value.GetValue(def);
                                    if (vv is bool b)
                                    {
                                        it["value"] = b;
                                    }
                                    else if (vv is int n)
                                    {
                                        it["value"] = n;
                                    }
                                    else
                                    {
                                        it["value"] = vv != null ? vv.ToString() : "";
                                    }
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
