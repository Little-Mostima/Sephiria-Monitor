using System;
using System.Collections.Generic;
using System.Globalization;

namespace SephiriaMonitor
{
    /// <summary>
    /// 战斗侧在线聚合：事件流照旧落盘，但这里维持一份 O(1) 的统计，
    /// 让 /combat 一次调用就拿到「命中率 / 各来源伤害 / 玩家承伤 / 分段时间线」，
    /// 不必再把上千条事件拉到客户端算。
    /// 进程内常驻，跨局由 RunTracker 在换局时 Reset()。
    /// </summary>
    public static class Combat
    {
        private const int BucketSeconds = 60;

        private class Agg
        {
            public int Hits;
            public double Total;
            public double Max;

            public void Add(double v)
            {
                Hits++;
                Total += v;
                if (v > Max) Max = v;
            }

            public Dictionary<string, object> Json(int digits)
            {
                return SephiriaMonitor.Json.Obj()
                    .Add("hits", Hits)
                    .Add("total", Math.Round(Total, digits))
                    .Add("max", Math.Round(Max, digits))
                    .Add("avg", Hits == 0 ? 0 : Math.Round(Total / Hits, digits));
            }
        }

        private static readonly object Gate = new object();

        private static int damageRows, probes, kills, deaths, playerDeaths, loots, moneys, exps, spawns, despawns;
        private static int failTotal, failAbsolute, failDodge, failBlock, failPass;
        private static double dealtTotal;
        private static int takenHits;
        private static double takenTotal, takenMax;
        private static double firstT = -1, lastT = -1;

        private static readonly Dictionary<string, int> ByResult = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ByElement = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ByType = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ByFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Agg> ById = new Dictionary<string, Agg>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Agg> ByVictimType = new Dictionary<string, Agg>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Agg> ByVictim = new Dictionary<string, Agg>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> DeathByKiller = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly SortedDictionary<long, Agg> Buckets = new SortedDictionary<long, Agg>();

        public static int Rows { get { lock (Gate) { return damageRows; } } }

        public static void Reset()
        {
            lock (Gate)
            {
                damageRows = probes = kills = deaths = playerDeaths = loots = moneys = exps = spawns = despawns = 0;
                failTotal = failAbsolute = failDodge = failBlock = failPass = 0;
                dealtTotal = 0;
                takenHits = 0;
                takenTotal = takenMax = 0;
                firstT = lastT = -1;
                ByResult.Clear(); ByElement.Clear(); ByType.Clear(); ByFrom.Clear();
                ById.Clear(); ByVictimType.Clear(); ByVictim.Clear(); DeathByKiller.Clear(); Buckets.Clear();
            }
        }

        /// <summary>由 Events.Add 调用（每条入库事件一次，代价是几次字典查找）。</summary>
        public static void Observe(Dictionary<string, object> e)
        {
            if (e == null) return;
            string kind = e.TryGetValue("kind", out object k) ? (k as string) : null;
            if (kind == null) return;

            double t = Num(e, "t", -1);
            lock (Gate)
            {
                if (t >= 0)
                {
                    if (firstT < 0) firstT = t;
                    lastT = t;
                }

                switch (kind)
                {
                    case "damage":
                        ObserveDamage(e, t);
                        return;
                    case "death":
                        deaths++;
                        if (Bool(e, "isPlayer")) playerDeaths++;
                        else kills++;
                        string killer = Str(e, "killer");
                        if (killer != null) Bump(DeathByKiller, killer);
                        return;
                    case "loot": loots++; return;
                    case "money": moneys++; return;
                    case "exp": exps++; return;
                    case "spawn": spawns++; return;
                    case "despawn": despawns++; return;
                }
            }
        }

        private static void ObserveDamage(Dictionary<string, object> e, double t)
        {
            damageRows++;
            double raw = Num(e, "raw", 0);
            string result = Str(e, "result") ?? "?";
            string element = Str(e, "element") ?? "?";
            string dtype = Str(e, "damageType") ?? "?";
            string from = Str(e, "fromType") ?? "?";
            string id = Str(e, "id");
            string victimType = Str(e, "victimType") ?? "?";
            string victim = Str(e, "victim");

            Bump(ByResult, result);
            Bump(ByElement, element);
            Bump(ByType, dtype);
            Bump(ByFrom, from);

            bool success = result == "Success";
            if (success)
            {
                dealtTotal += Num(e, "dealt", 0);
            }
            else
            {
                failTotal++;
                if (result.IndexOf("Absolute", StringComparison.Ordinal) >= 0) failAbsolute++;
                else if (result.IndexOf("Dodge", StringComparison.Ordinal) >= 0) failDodge++;
                else if (result.IndexOf("Block", StringComparison.Ordinal) >= 0) failBlock++;
                else if (result.IndexOf("Pass", StringComparison.Ordinal) >= 0) failPass++;
            }

            if (id != null) AggOf(ById, id).Add(raw);
            AggOf(ByVictimType, victimType).Add(raw);
            if (victim != null) AggOf(ByVictim, victim).Add(raw);

            if (victimType == "PlayerAvatar")
            {
                takenHits++;
                takenTotal += raw;
                if (raw > takenMax) takenMax = raw;
            }

            if (t >= 0)
            {
                long b = (long)(t / BucketSeconds) * BucketSeconds;
                AggOf(Buckets, b).Add(raw);
            }
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            map.TryGetValue(key, out int v);
            map[key] = v + 1;
        }

        private static Agg AggOf(Dictionary<string, Agg> map, string key)
        {
            if (!map.TryGetValue(key, out Agg a))
            {
                a = new Agg();
                map[key] = a;
            }
            return a;
        }

        private static Agg AggOf(SortedDictionary<long, Agg> map, long key)
        {
            if (!map.TryGetValue(key, out Agg a))
            {
                a = new Agg();
                map[key] = a;
            }
            return a;
        }

        private static string Str(Dictionary<string, object> e, string key)
        {
            if (!e.TryGetValue(key, out object v) || v == null) return null;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static double Num(Dictionary<string, object> e, string key, double fallback)
        {
            if (!e.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool Bool(Dictionary<string, object> e, string key)
        {
            if (!e.TryGetValue(key, out object v) || v == null) return false;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        public static string Stats(int top)
        {
            if (top <= 0) top = 10;
            lock (Gate)
            {
                Dictionary<string, object> root = SephiriaMonitor.Json.Obj()
                    .Add("live", true)
                    .Add("rows", damageRows)
                    .Add("probesSkipped", Events.ProbeCount)
                    .Add("kills", kills)
                    .Add("deaths", deaths)
                    .Add("playerDeaths", playerDeaths)
                    .Add("loot", loots)
                    .Add("money", moneys)
                    .Add("exp", exps)
                    .Add("spawn", spawns)
                    .Add("despawn", despawns)
                    .Add("dealtTotal", Math.Round(dealtTotal, 1))
                    .Add("durationSec", firstT < 0 ? 0 : Math.Round(lastT - firstT, 1));

                int successRows = damageRows - failTotal;
                root.Add("hitRate", damageRows == 0 ? 0 : Math.Round(100.0 * successRows / damageRows, 1));
                root.Add("fail", SephiriaMonitor.Json.Obj()
                    .Add("total", failTotal)
                    .Add("absolute", failAbsolute)
                    .Add("dodge", failDodge)
                    .Add("block", failBlock)
                    .Add("pass", failPass));
                root.Add("playerTaken", SephiriaMonitor.Json.Obj()
                    .Add("hits", takenHits)
                    .Add("total", Math.Round(takenTotal, 1))
                    .Add("max", Math.Round(takenMax, 1))
                    .Add("avg", takenHits == 0 ? 0 : Math.Round(takenTotal / takenHits, 1)));

                root.Add("byResult", IntMap(ByResult));
                root.Add("byElement", IntMap(ByElement));
                root.Add("byDamageType", IntMap(ByType));
                root.Add("byFromType", IntMap(ByFrom));
                root.Add("byDamageId", Top(ById, top, 1));
                root.Add("byVictimType", Top(ByVictimType, top, 1));
                root.Add("byVictim", Top(ByVictim, top, 1));
                root.Add("deathByKiller", IntMap(DeathByKiller));

                List<object> curve = new List<object>(Buckets.Count);
                foreach (KeyValuePair<long, Agg> kv in Buckets)
                {
                    curve.Add(SephiriaMonitor.Json.Obj()
                        .Add("t0", kv.Key)
                        .Add("hits", kv.Value.Hits)
                        .Add("total", Math.Round(kv.Value.Total, 1)));
                }
                root.Add("curve", curve);
                return SephiriaMonitor.Json.Write(root);
            }
        }

        private static Dictionary<string, object> IntMap(Dictionary<string, int> map)
        {
            Dictionary<string, object> o = SephiriaMonitor.Json.Obj();
            foreach (KeyValuePair<string, int> kv in map) o[kv.Key] = kv.Value;
            return o;
        }

        private static List<object> Top(Dictionary<string, Agg> map, int top, double round)
        {
            List<KeyValuePair<string, Agg>> all = new List<KeyValuePair<string, Agg>>(map);
            all.Sort(delegate (KeyValuePair<string, Agg> a, KeyValuePair<string, Agg> b)
            {
                int c = b.Value.Total.CompareTo(a.Value.Total);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            List<object> list = new List<object>(Math.Min(top, all.Count));
            for (int i = 0; i < all.Count && i < top; i++)
            {
                Dictionary<string, object> row = all[i].Value.Json(1);
                row["name"] = all[i].Key;
                list.Add(row);
            }
            return list;
        }
    }
}
