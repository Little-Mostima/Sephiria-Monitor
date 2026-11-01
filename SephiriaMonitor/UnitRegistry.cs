using System.Collections.Generic;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// UnitAvatar 注册表：Harmony 在 Awake / OnDestroy 上登记与注销，读的时候只拷列表。
    /// 这样面板、摘要、移动追踪都不必再走 Resources.FindObjectsOfTypeAll（全对象注册表扫描，
    /// 毫秒级开销，是 1Hz 采样卡顿的主因）。
    /// </summary>
    public static class UnitRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<UnitAvatar> Items = new List<UnitAvatar>();
        private static float lastReseedAt = -999f;

        public static int Count
        {
            get { lock (Gate) { return Items.Count; } }
        }

        public static float LastReseed
        {
            get { return lastReseedAt; }
        }

        public static void Register(UnitAvatar unit)
        {
            if (unit == null)
            {
                return;
            }
            lock (Gate)
            {
                if (!Items.Contains(unit))
                {
                    Items.Add(unit);
                }
            }
        }

        public static void Unregister(UnitAvatar unit)
        {
            if (unit == null)
            {
                return;
            }
            lock (Gate)
            {
                Items.Remove(unit);
            }
        }

        /// <summary>拷贝一份，顺手剔掉已销毁的对象（Unity 的 == null 认已销毁实例）。</summary>
        public static List<UnitAvatar> Snapshot()
        {
            List<UnitAvatar> copy = new List<UnitAvatar>(Items.Count);
            lock (Gate)
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    UnitAvatar u = Items[i];
                    if (u == null)
                    {
                        Items.RemoveAt(i);
                        continue;
                    }
                    copy.Add(u);
                }
            }
            copy.Reverse();
            return copy;
        }

        public static void Clear()
        {
            lock (Gate) { Items.Clear(); }
        }

        /// <summary>场景切换后补扫一次：兜住插件加载前就存在、或没走 Awake 的对象。</summary>
        public static void Reseed()
        {
            lastReseedAt = Time.realtimeSinceStartup;
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(UnitAvatar));
            lock (Gate)
            {
                Items.Clear();
                for (int i = 0; i < all.Length; i++)
                {
                    UnitAvatar u = all[i] as UnitAvatar;
                    if (u == null || !u.gameObject.scene.IsValid())
                    {
                        continue;
                    }
                    Items.Add(u);
                }
            }
        }
    }
}
