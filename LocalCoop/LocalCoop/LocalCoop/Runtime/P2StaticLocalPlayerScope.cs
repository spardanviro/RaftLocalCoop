using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SplitScreen
{
    // Temporarily redirects vanilla static localPlayer caches while P2 is driving
    // an original IRaycastable path. Kept narrow: capture, set, restore.
    internal sealed class P2StaticLocalPlayerScope : IDisposable
    {
        struct Entry
        {
            public FieldInfo Field;
            public Network_Player OldValue;
        }

        static readonly Dictionary<Type, FieldInfo[]> s_cache = new Dictionary<Type, FieldInfo[]>();
        readonly List<Entry> _entries = new List<Entry>();
        bool _disposed;

        public P2StaticLocalPlayerScope(Network_Player player, IEnumerable<IRaycastable> raycastables)
        {
            if (player == null || raycastables == null) return;

            foreach (var obj in raycastables)
            {
                var comp = obj as Component;
                if (comp == null) continue;

                foreach (var field in GetFields(comp.GetType()))
                {
                    var old = field.GetValue(null) as Network_Player;
                    if (ReferenceEquals(old, player)) continue;
                    field.SetValue(null, player);
                    _entries.Add(new Entry { Field = field, OldValue = old });
                }
            }
        }

        static FieldInfo[] GetFields(Type type)
        {
            if (type == null) return Array.Empty<FieldInfo>();
            if (s_cache.TryGetValue(type, out var cached)) return cached;

            var fields = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                var f = t.GetField("localPlayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null && f.FieldType == typeof(Network_Player))
                    fields.Add(f);
            }

            cached = fields.ToArray();
            s_cache[type] = cached;
            return cached;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = _entries.Count - 1; i >= 0; --i)
            {
                var e = _entries[i];
                if (e.Field == null) continue;
                var restore = e.OldValue != null ? e.OldValue : Main.player1;
                e.Field.SetValue(null, restore);
            }
            _entries.Clear();
        }
    }
}
