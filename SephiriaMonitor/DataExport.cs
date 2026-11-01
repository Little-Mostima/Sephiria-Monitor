using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>One-shot export of the 27 game databases into CSVs under out/session-*/db/.</summary>
    public static class DataExport
    {
        public static string Status = "wait";
        private static float nextTry;
        private static int attempts;
        private static int rows;
        private static int files;
        private static bool slowLogged;

        public static int Rows => rows;
        public static int Files => files;

        /// <summary>
        /// Export runs a few tables per frame instead of all 34 in one Update: a synchronous
        /// ExportAll froze the main thread for the duration of the whole export.
        /// </summary>
        private static List<Action> steps;
        private static int stepIndex;

        public static void Tick()
        {
            if (Status == "done" || Status == "gaveup")
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (steps != null)
            {
                if (stepIndex >= steps.Count)
                {
                    steps = null;
                    return;
                }
                Action step = steps[stepIndex];
                stepIndex++;
                try
                {
                    step();
                }
                catch (Exception ex)
                {
                    MonitorPlugin.LogError("DB|step|" + stepIndex + "|" + ex.GetType().Name + ":" + ex.Message);
                }
                return;
            }
            if (now < nextTry)
            {
                return;
            }
            float t = Time.realtimeSinceStartup;
            if (t < nextTry)
            {
                return;
            }
            nextTry = t + 2f;
            attempts++;
            bool ready;
            try
            {
                int[] ids = ItemDatabase.GetAllItemID();
                ready = ids != null && ids.Length > 0;
            }
            catch
            {
                ready = false;
            }
            if (!ready)
            {
                // Eventual consistency: the static databases only fill in once a run scene has
                // loaded, which can be minutes after the title screen. Slow down after a while
                // instead of giving up, so a slow boot still ends with a full export.
                if (attempts > 120)
                {
                    nextTry = t + 10f;
                    Status = "slow:" + attempts;
                    if (!slowLogged)
                    {
                        slowLogged = true;
                        MonitorPlugin.LogInfo("DB|export|slow|attempts=" + attempts);
                    }
                }
                else
                {
                    Status = "wait:" + attempts;
                }
                return;
            }
            StartSteps();
        }

        private static void StartSteps()
        {
            steps = BuildSteps();
            stepIndex = 0;
            Status = "exporting:" + steps.Count;
            MonitorPlugin.LogInfo("DB|export|start|tables=" + steps.Count);
        }

        private static void Finish()
        {
            Session.WriteText("db/index.json", Json.Write(Json.Obj()
                .Add("files", files).Add("rows", rows)
                .Add("at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))));
            MonitorPlugin.LogInfo("DB|export|done|files=" + files + "|rows=" + rows);
            Session.Flush();
            Status = "done";
        }

        private static List<Action> BuildSteps()
        {
            List<Action> list = new List<Action>();
            list.Add(() => Dump("item.csv", Items()));
            // The "all" tables are the raw Resources assets, so entries disabled in the shipped
            // build stay visible: WeaponSkillDatabase only keeps 3 enabled skills while the
            // WeaponSkill folder holds the full set. Both views share one schema family.
            list.Add(() => Dump("item_all.csv", Safe(() => Resources.LoadAll<ItemEntity>("Item"))));
            list.Add(() => Dump("item_category.csv", ItemCats()));
            list.Add(() => Dump("weapon.csv", Safe(() => WeaponDatabase.GetAll())));
            list.Add(() => Dump("weapon_all.csv", Safe(() => Resources.LoadAll<WeaponEntity>("Weapon"))));
            list.Add(() => Dump("weapon_skill.csv", Safe(() => WeaponSkillDatabase.GetAll())));
            list.Add(() => Dump("weapon_skill_all.csv", Safe(() => Resources.LoadAll<WeaponSkillEntity>("WeaponSkill"))));
            list.Add(() => Dump("active_skill.csv", Safe(() => ActiveSkillDatabase.GetAll())));
            list.Add(() => Dump("active_skill_all.csv", Safe(() => Resources.LoadAll<ActiveSkillEntity>("ActiveSkill"))));
            list.Add(() => Dump("miracle.csv", Safe(() => MiracleDatabase.GetAll())));
            list.Add(() => Dump("costume.csv", Safe(() => CostumeDatabase.GetAll())));
            list.Add(() => Dump("prop.csv", Safe(() => PropDatabase.GetAll())));
            list.Add(() => Dump("tree_shop.csv", Safe(() => TreeShopItemDatabase.GetAll())));
            list.Add(() => Dump("lore.csv", Safe(() => LoreDatabase.GetAll())));
            list.Add(() => Dump("custom_game.csv", Safe(() => CustomGameDatabase.GetAll())));
            list.Add(() => Dump("race.csv", Safe(() => RaceDatabase.GetAll())));
            list.Add(() => Dump("faction.csv", Safe(() => FactionDatabase.GetAll())));
            list.Add(() => Dump("unit_spawn.csv", Safe(() => UnitDatabase.GetSpawnEntities())));
            list.Add(() => Dump("passive.csv", Values(typeof(PassiveDatabase), "data")));
            list.Add(() => Dump("hardmode_shard.csv", Values(typeof(HardModeDatebase), "data")));
            list.Add(() => Dump("hardmode_reward.csv", Values(typeof(HardModeDatebase), "sortedRewardData")));
            list.Add(() => Dump("keyword.csv", Values(typeof(KeywordDatabase), "keywordDictionary")));
            list.Add(() => DumpMap("const_int.csv", typeof(KeywordDatabase), "ConstValueDictionary"));
            list.Add(() => DumpMap("const_float.csv", typeof(KeywordDatabase), "ConstFloatValueDictionary"));
            list.Add(() => Dump("status.csv", Values(typeof(StatusDatabase), "entityDictionary")));
            list.Add(() => Dump("status_effect.csv", Values(typeof(StatusEffectDatabase), "statusEffects")));
            list.Add(() => Dump("quest.csv", Values(typeof(QuestDatabase), "questDictionary")));
            list.Add(() => Dump("social_id.csv", Values(typeof(SocialIDDatabase), "socialEntities")));
            list.Add(() => Dump("avatar_spawn.csv", Values(typeof(AvatarSpawnDatabase), "spawnEntities")));
            list.Add(() => Dump("switch.csv", Values(typeof(SwitchDatabase), "destinySwitchPairs")));
            list.Add(() => Dump("key.csv", Values(typeof(KeyDatabase), "keyPairs")));
            list.Add(() => Dump("effect_hud.csv", Values(typeof(EffectHUDDatabase), "effectHUDDictionary")));
            list.Add(() => Dump("tile_wall.csv", Values(typeof(TileDatabase), "wallTiles")));
            list.Add(() => Dump("tile_ground.csv", Values(typeof(TileDatabase), "groundTiles")));
            list.Add(() => Dump("tile_cliff.csv", Values(typeof(TileDatabase), "cliffTiles")));
            list.Add(() => Dump("farm_ability.csv", Values(typeof(FarmAbilityDatabase), "abilities")));
            list.Add(() => Dump("buff.csv", Values(typeof(UnitDatabase), "buffEntities")));
            list.Add(() => Dump("debuff.csv", Values(typeof(UnitDatabase), "debuffEntities")));
            // v0.5.0: the 11 databases the first export pass missed.
            list.Add(() => DumpKeyed("boss_hard.csv", typeof(UnitDatabase), "bossHardEntities"));
            list.Add(() => DumpKeyed("lore_category.csv", typeof(LoreDatabase), "loreCategoryDictionary"));
            list.Add(() => Dump("costume_unlock.csv", Values(typeof(CostumeDatabase), "costumeUnlockEntities")));
            list.Add(() => DumpKeyed("costume_skin.csv", typeof(CostumeDatabase), "costumeSkinDictionary"));
            list.Add(() => DumpKeyed("main_quest_node.csv", typeof(QuestDatabase), "mainQuestNodesDictionary"));
            list.Add(() => DumpKeyed("destiny_quest.csv", typeof(QuestDatabase), "destinyQuestDictionary"));
            list.Add(() => DumpKeyed("damage_id.csv", typeof(KeywordDatabase), "damageIdDictionary"));
            list.Add(() => DumpKeyed("arrangement_bonus.csv", typeof(ItemDatabase), "arrangementBonuses"));
            list.Add(() => DumpKeyedList("weapon_enhancement.csv", typeof(WeaponDatabase), "weaponEnhancementDictionary"));
            list.Add(() => DumpKeyed("special_stat_category.csv", typeof(SpecialStatDatabase), "categories"));
            list.Add(() => DumpKeyed("special_stat_status.csv", typeof(SpecialStatDatabase), "statuses"));
            list.Add(ExportMinimap);
            list.Add(DumpLocalization);
            list.Add(Finish);
            return list;
        }

        private static IEnumerable<object> Safe(Func<object> get)
        {
            object value = null;
            try { value = get(); }
            catch (Exception ex) { MonitorPlugin.LogError("DB|source|" + ex.GetType().Name + ":" + ex.Message); }
            return Flatten(value);
        }

        private static IEnumerable<object> Items()
        {
            int[] ids = null;
            try { ids = ItemDatabase.GetAllItemID(); }
            catch { }
            if (ids == null)
            {
                yield break;
            }
            for (int i = 0; i < ids.Length; i++)
            {
                object e = null;
                try { e = ItemDatabase.FindItemById(ids[i]); }
                catch { }
                if (e != null)
                {
                    yield return e;
                }
            }
        }

        private static IEnumerable<object> ItemCats()
        {
            try { return Flatten(ItemDatabase.GetAllItemCategory()); }
            catch { return new List<object>(); }
        }

        private static IEnumerable<object> Values(Type owner, string member)
        {
            object container = null;
            try { container = GetValue(owner, member); }
            catch (Exception ex) { MonitorPlugin.LogError("DB|field|" + owner.Name + "." + member + "|" + ex.GetType().Name); }
            return Flatten(container is IDictionary ? DictValues(container) : container);
        }

        private static IEnumerable<object> DictValues(object dict)
        {
            IDictionary d = dict as IDictionary;
            if (d == null)
            {
                yield break;
            }
            foreach (DictionaryEntry e in d)
            {
                if (e.Value != null)
                {
                    yield return e.Value;
                }
            }
        }

        private static IEnumerable<object> Flatten(object value)
        {
            if (value == null)
            {
                yield break;
            }
            if (value is string)
            {
                yield break;
            }
            IEnumerable seq = value as IEnumerable;
            if (seq == null)
            {
                yield return value;
                yield break;
            }
            foreach (object item in seq)
            {
                if (item == null)
                {
                    continue;
                }
                if (item is IEnumerable && !(item is string) && !(item is IDictionary))
                {
                    foreach (object inner in (IEnumerable)item)
                    {
                        if (inner != null)
                        {
                            yield return inner;
                        }
                    }
                }
                else
                {
                    yield return item;
                }
            }
        }

        private static object GetValue(Type type, string member)
        {
            FieldInfo f = type.GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
            {
                return f.GetValue(null);
            }
            PropertyInfo p = type.GetProperty(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanRead)
            {
                return p.GetValue(null, null);
            }
            f = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            List<UnityEngine.Object> instances = StateApi.Instances(type);
            for (int i = 0; i < instances.Count; i++)
            {
                if (f != null)
                {
                    return f.GetValue(instances[i]);
                }
                if (p != null && p.CanRead)
                {
                    return p.GetValue(instances[i], null);
                }
            }
            return null;
        }

        private static void Dump(string fileName, IEnumerable<object> items)
        {
            string[] columns = null;
            CsvTable table = null;
            int n = 0;
            try
            {
                foreach (object item in items)
                {
                    if (item == null)
                    {
                        continue;
                    }
                    if (columns == null)
                    {
                        columns = FieldNames(item.GetType());
                        if (columns.Length == 0)
                        {
                            Type t = item.GetType();
                            if (t.IsPrimitive || t.IsEnum || item is string)
                            {
                                columns = new string[] { "value" };
                            }
                            else
                            {
                                MonitorPlugin.LogInfo("DB|skip|no fields|" + t.Name);
                                continue;
                            }
                        }
                        table = new CsvTable(Path.Combine(Session.Root, "db", fileName), columns);
                        files++;
                    }
                    table.Row(FieldValues(item, columns));
                    n++;
                    rows++;
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("DB|dump|" + fileName + "|" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                if (table != null)
                {
                    table.Close();
                }
            }
            if (table == null)
            {
                MonitorPlugin.LogInfo("DB|empty|" + fileName);
            }
            else
            {
                MonitorPlugin.LogInfo("DB|" + fileName + "|rows=" + n);
            }
        }

        private static string[] FieldNames(Type type)
        {
            List<string> names = new List<string>();
            Type t = type;
            while (t != null && t != typeof(object) && t != typeof(UnityEngine.Object) && t != typeof(MonoBehaviour))
            {
                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    // Delegate fields (SpecialStatDatabase.Registration.reader) and Mirror sync
                    // plumbing cannot be rendered into a CSV cell and would only add noise columns.
                    if (typeof(Delegate).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(IntPtr)
                        || field.FieldType == typeof(UIntPtr) || field.FieldType.IsPointer
                        || field.Name.Contains("_Mirror_"))
                    {
                        continue;
                    }
                    if (!names.Contains(field.Name))
                    {
                        names.Add(field.Name);
                    }
                }
                t = t.BaseType;
            }
            return names.ToArray();
        }

        private static void DumpMap(string fileName, Type owner, string member)
        {
            object container = null;
            try { container = GetValue(owner, member); }
            catch (Exception ex) { MonitorPlugin.LogError("DB|field|" + owner.Name + "." + member + "|" + ex.GetType().Name); return; }
            IDictionary dict = container as IDictionary;
            if (dict == null)
            {
                MonitorPlugin.LogInfo("DB|skip|not a dict|" + fileName);
                return;
            }
            CsvTable table = null;
            int n = 0;
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    if (table == null)
                    {
                        table = new CsvTable(Path.Combine(Session.Root, "db", fileName), new string[] { "key", "value" });
                        files++;
                    }
                    if (e.Value == null)
                    {
                        continue;
                    }
                    table.Row(new object[] { Cell(e.Key), Cell(e.Value) });
                    n++;
                    rows++;
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("DB|dump|" + fileName + "|" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                if (table != null)
                {
                    table.Close();
                }
            }
            MonitorPlugin.LogInfo("DB|" + fileName + "|pairs=" + n + "|dict=" + dict.Count);
        }

        /// <summary>
        /// Like Dump, but for a private static Dictionary: the dictionary key becomes a "key"
        /// column, so ids stay visible even when the entity itself does not carry them.
        /// </summary>
        private static void DumpKeyed(string fileName, Type owner, string member)
        {
            IDictionary dict = null;
            try { dict = GetValue(owner, member) as IDictionary; }
            catch (Exception ex) { MonitorPlugin.LogError("DB|field|" + owner.Name + "." + member + "|" + ex.GetType().Name); }
            if (dict == null)
            {
                MonitorPlugin.LogInfo("DB|skip|not a dict|" + fileName);
                return;
            }
            string[] columns = null;
            CsvTable table = null;
            int n = 0;
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    object value = e.Value;
                    if (value == null)
                    {
                        continue;
                    }
                    if (value is IEnumerable && !(value is string) && !(value is IDictionary))
                    {
                        // Lists are DumpKeyedList's job; skip here so one table keeps one shape.
                        continue;
                    }
                    bool plain = false;
                    if (columns == null)
                    {
                        List<string> cols = new List<string>();
                        cols.Add("key");
                        string[] fields = FieldNames(value.GetType());
                        if (fields.Length == 0)
                        {
                            plain = true;
                            cols.Add("value");
                        }
                        cols.AddRange(fields);
                        columns = cols.ToArray();
                        table = new CsvTable(Path.Combine(Session.Root, "db", fileName), columns);
                        files++;
                    }
                    object[] values = FieldValues(value, columns);
                    values[0] = Cell(e.Key);
                    plain = columns.Length == 2 && columns[1] == "value";
                    if (plain)
                    {
                        values[1] = Cell(value);
                    }
                    table.Row(values);
                    n++;
                    rows++;
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("DB|dump|" + fileName + "|" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                if (table != null)
                {
                    table.Close();
                }
            }
            if (table == null)
            {
                MonitorPlugin.LogInfo("DB|empty|" + fileName);
            }
            else
            {
                MonitorPlugin.LogInfo("DB|" + fileName + "|pairs=" + n);
            }
        }

        /// <summary>Dictionary&lt;key, List&lt;T&gt;&gt;: one row per inner item, with the key and the list index.</summary>
        private static void DumpKeyedList(string fileName, Type owner, string member)
        {
            IDictionary dict = null;
            try { dict = GetValue(owner, member) as IDictionary; }
            catch (Exception ex) { MonitorPlugin.LogError("DB|field|" + owner.Name + "." + member + "|" + ex.GetType().Name); }
            if (dict == null)
            {
                MonitorPlugin.LogInfo("DB|skip|not a dict|" + fileName);
                return;
            }
            string[] columns = null;
            CsvTable table = null;
            int n = 0;
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    IEnumerable seq = e.Value as IEnumerable;
                    if (seq == null || e.Value is string)
                    {
                        continue;
                    }
                    int index = 0;
                    foreach (object inner in seq)
                    {
                        if (inner == null)
                        {
                            index++;
                            continue;
                        }
                        if (columns == null)
                        {
                            List<string> cols = new List<string>();
                            cols.Add("key");
                            cols.Add("index");
                            string[] fields = FieldNames(inner.GetType());
                            if (fields.Length == 0)
                            {
                                cols.Add("value");
                            }
                            cols.AddRange(fields);
                            columns = cols.ToArray();
                            table = new CsvTable(Path.Combine(Session.Root, "db", fileName), columns);
                            files++;
                        }
                        object[] values = FieldValues(inner, columns);
                        values[0] = Cell(e.Key);
                        values[1] = index;
                        if (columns.Length == 3 && columns[2] == "value")
                        {
                            values[2] = Cell(inner);
                        }
                        table.Row(values);
                        n++;
                        rows++;
                        index++;
                    }
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("DB|dump|" + fileName + "|" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                if (table != null)
                {
                    table.Close();
                }
            }
            if (table == null)
            {
                MonitorPlugin.LogInfo("DB|empty|" + fileName);
            }
            else
            {
                MonitorPlugin.LogInfo("DB|" + fileName + "|rows=" + n);
            }
        }

        /// <summary>
        /// Some databases keep a localization key in aName/aFlavorText (weapon_skill.csv shipped
        /// raw "WeaponSkill_0000_Name" rows while item.csv already had real text), because those
        /// types declare a plain string instead of a LocalizedString. Resolve the key against
        /// LocalizationManager and keep it verbatim when it does not resolve, so nothing vanishes.
        /// </summary>
        private static string Localize(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 3 || key.IndexOf(' ') >= 0)
            {
                return key;
            }
            try
            {
                LocalizationManager lm = LocalizationManager.Instance;
                if (lm == null)
                {
                    return key;
                }
                string text = lm.GetText(lm.CurrentLanguage, key);
                string[] fallbacks = { "zh-CN", "ko-KR", "en-US" };
                for (int i = 0; (string.IsNullOrEmpty(text) || text == key) && i < fallbacks.Length; i++)
                {
                    text = lm.GetText(fallbacks[i], key);
                }
                return string.IsNullOrEmpty(text) ? key : text;
            }
            catch
            {
                return key;
            }
        }

        private static bool IsTextColumn(string column)
        {
            return column == "aName" || column == "aFlavorText" || column == "name"
                || column == "title" || column == "description" || column == "label";
        }

        private static object[] FieldValues(object item, string[] columns)
        {
            object[] values = new object[columns.Length];
            Type type = item.GetType();
            if (columns.Length == 1 && columns[0] == "value")
            {
                values[0] = Cell(item);
                return values;
            }
            for (int i = 0; i < columns.Length; i++)
            {
                try
                {
                    FieldInfo f = null;
                    Type t = type;
                    while (t != null && f == null)
                    {
                        f = t.GetField(columns[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        t = t.BaseType;
                    }
                    values[i] = f == null ? "" : Cell(f.GetValue(item));
                    if (f != null && values[i] is string cellText && cellText.Length > 0 && IsTextColumn(columns[i]))
                    {
                        values[i] = Localize(cellText);
                    }
                }
                catch
                {
                    values[i] = "<err>";
                }
            }
            return values;
        }

        private static object Cell(object v)
        {
            if (v == null)
            {
                return "";
            }
            Type vt = v.GetType();
            if (v is string)
            {
                return v;
            }
            if (vt.IsEnum)
            {
                return v.ToString();
            }
            if (v is float f)
            {
                return Math.Round(f, 3);
            }
            if (v is double d)
            {
                return Math.Round(d, 4);
            }
            if (vt.IsPrimitive || v is decimal)
            {
                return v;
            }
            if (v is UnityEngine.Object uo)
            {
                return uo == null ? "" : uo.name;
            }
            if (v is Vector2 v2)
            {
                return v2.x + "|" + v2.y;
            }
            if (v is Vector3 v3)
            {
                return v3.x + "|" + v3.y + "|" + v3.z;
            }
            if (vt.Name == "LocalizedString")
            {
                try { return Convert.ToString(v); }
                catch { return ""; }
            }
            IEnumerable seq = v as IEnumerable;
            if (seq != null && !(v is IDictionary))
            {
                List<string> parts = new List<string>();
                int n = 0;
                foreach (object item in seq)
                {
                    if (n++ >= 20)
                    {
                        parts.Add("...");
                        break;
                    }
                    parts.Add(Convert.ToString(Cell(item)));
                }
                return string.Join("|", parts.ToArray());
            }
            if (v is IDictionary)
            {
                try { return "<dict>" + ((IDictionary)v).Count; }
                catch { return "<dict>"; }
            }
            // Plain (non-UnityEngine) data classes would otherwise all render as their type name,
            // e.g. every arrangement inside ArrangementBonusEntity.stats as "ArrangementBonusEntity+Arrangement".
            string label = Label(v);
            if (label != null)
            {
                return label;
            }
            try { return Convert.ToString(v); }
            catch { return "<obj>"; }
        }

        private static string Label(object v)
        {
            Type t = v.GetType();
            string[] names = { "aName", "name", "id" };
            for (int i = 0; i < names.Length; i++)
            {
                object value = null;
                try
                {
                    PropertyInfo p = t.GetProperty(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                    {
                        value = p.GetValue(v, null);
                    }
                    else
                    {
                        FieldInfo f = t.GetField(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null)
                        {
                            value = f.GetValue(v);
                        }
                    }
                }
                catch { value = null; }
                if (value is string s && s.Length > 0)
                {
                    return s;
                }
                if (value is int n)
                {
                    return n.ToString();
                }
            }
            return null;
        }

        private static void ExportMinimap()
        {
            CsvTable table = null;
            int n = 0;
            try
            {
                object container = GetValue(typeof(UIMinimapElementImageDatabase), "animations");
                IDictionary d = container as IDictionary;
                if (d == null)
                {
                    return;
                }
                table = new CsvTable(Path.Combine(Session.Root, "db", "minimap_anim.csv"),
                    new[] { "animation", "state", "fps", "repeat", "frames" });
                foreach (DictionaryEntry e in d)
                {
                    object set = e.Value;
                    if (set == null)
                    {
                        continue;
                    }
                    FieldInfo sprites = set.GetType().GetField("sprites", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    IEnumerable states = sprites == null ? null : sprites.GetValue(set) as IEnumerable;
                    if (states == null)
                    {
                        continue;
                    }
                    foreach (object state in states)
                    {
                        if (state == null)
                        {
                            continue;
                        }
                        Type st = state.GetType();
                        object stateName = ReadField(st, state, "state");
                        object fps = ReadField(st, state, "fps");
                        object repeat = ReadField(st, state, "repeat");
                        IEnumerable timeline = ReadField(st, state, "timeline") as IEnumerable;
                        int frames = 0;
                        if (timeline != null)
                        {
                            foreach (object _ in timeline) { frames++; }
                        }
                        table.Row(Convert.ToString(e.Key), Convert.ToString(stateName), fps, repeat, frames);
                        n++;
                    }
                }
                files++;
                rows += n;
                MonitorPlugin.LogInfo("DB|minimap_anim.csv|rows=" + n);
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("DB|minimap|" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                // The writer buffers; without Close an exception loses every row already written.
                if (table != null)
                {
                    try { table.Close(); }
                    catch (Exception ex) { MonitorPlugin.LogError("DB|minimap|close|" + ex.GetType().Name); }
                }
            }
        }

        /// <summary>
        /// The localization table itself (LocalizationManager.textTable: language -> key -> text).
        /// Exported as one wide CSV so aName columns that only carry a key (weapon_skill.csv ships
        /// "WeaponSkill_0000_Name") can be joined to their text, and so a missing translation is
        /// visible instead of guessed. One column per loaded language, one row per key.
        /// </summary>
        private static void DumpLocalization()
        {
            CsvTable table = null;
            int n = 0;
            try
            {
                object container = GetValue(typeof(LocalizationManager), "textTable");
                IDictionary byLanguage = container as IDictionary;
                if (byLanguage == null)
                {
                    MonitorPlugin.LogInfo("DB|skip|no textTable");
                    return;
                }
                List<string> languages = new List<string>();
                List<DictionaryEntry> perLanguage = new List<DictionaryEntry>();
                foreach (DictionaryEntry e in byLanguage)
                {
                    IDictionary texts = e.Value as IDictionary;
                    if (texts == null)
                    {
                        continue;
                    }
                    languages.Add(Convert.ToString(e.Key));
                    perLanguage.Add(e);
                }
                List<string> keys = new List<string>();
                HashSet<string> seen = new HashSet<string>();
                for (int i = 0; i < perLanguage.Count; i++)
                {
                    foreach (DictionaryEntry kv in (IDictionary)perLanguage[i].Value)
                    {
                        string key = Convert.ToString(kv.Key);
                        if (seen.Add(key))
                        {
                            keys.Add(key);
                        }
                    }
                }
                List<string> columns = new List<string>();
                columns.Add("key");
                columns.AddRange(languages);
                table = new CsvTable(Path.Combine(Session.Root, "db", "localization.csv"), columns.ToArray());
                files++;
                for (int i = 0; i < keys.Count; i++)
                {
                    object[] values = new object[columns.Count];
                    values[0] = keys[i];
                    for (int l = 0; l < languages.Count; l++)
                    {
                        IDictionary texts = (IDictionary)perLanguage[l].Value;
                        values[l + 1] = texts.Contains(keys[i]) ? texts[keys[i]] : "";
                    }
                    table.Row(values);
                    n++;
                    rows++;
                }
                MonitorPlugin.LogInfo("DB|localization.csv|rows=" + n + "|languages=" + string.Join("|", languages.ToArray()));
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("DB|localization|" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                if (table != null)
                {
                    try { table.Close(); }
                    catch (Exception ex) { MonitorPlugin.LogError("DB|localization|close|" + ex.GetType().Name); }
                }
            }
        }

        private static object ReadField(Type t, object target, string name)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
            {
                return null;
            }
            return f.GetValue(target);
        }
    }
}