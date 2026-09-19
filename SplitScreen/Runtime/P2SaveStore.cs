using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    //
    
    
    
    public class P2SaveEntry
    {
        public int slot;        
        public int itemIndex;   // Item_Base.UniqueIndex
        public int amount;
        public int uses;
        public string exclusive;
    }

    class P2SaveStats { public float health, bonusHealth, hunger, bonusHunger, thirst, bonusThirst, oxygen; }
    class P2SavePos { public bool parented; public float x, y, z, roty; }

    public static class P2SaveStore
    {
        
        
        public static int LastSaveFrame = -1;

        static string GetPath()
        {
            string dir = SaveAndLoad.PlayerPath;
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, "SplitScreen_P2_World_" + GetWorldSaveKey() + ".dat");
        }

        static string GetLegacyGuidPath()
        {
            string dir = SaveAndLoad.PlayerPath;
            if (string.IsNullOrEmpty(dir) || SaveAndLoad.WorldGuid == Guid.Empty) return null;
            return Path.Combine(dir, "SplitScreen_P2_" + SaveAndLoad.WorldGuid + ".dat");
        }

        static string GetWorldSaveKey()
        {
            string name = SaveAndLoad.WorldGuid != Guid.Empty ? SaveAndLoad.WorldGuid.ToString() : null;
            if (string.IsNullOrWhiteSpace(name))
                name = SaveAndLoad.CurrentGameFileName;
            if (string.IsNullOrWhiteSpace(name) && SaveAndLoad.WorldToLoad != null)
                name = SaveAndLoad.WorldToLoad.name;
            if (string.IsNullOrWhiteSpace(name))
                name = "UnknownWorld";

            name = SaveAndLoad.SanitizeFileName(name.Trim());
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        
        
        static string Line(string kind, P2SaveEntry e) =>
            string.Join("\t", kind, e.slot.ToString(CultureInfo.InvariantCulture),
                e.itemIndex.ToString(CultureInfo.InvariantCulture),
                e.amount.ToString(CultureInfo.InvariantCulture),
                e.uses.ToString(CultureInfo.InvariantCulture), e.exclusive ?? "");

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        static float PF(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public static void SaveP2(Network_Player p2)
        {
            try
            {
                if ((Object)p2 == null) return;
                string path = GetPath();
                if (path == null) { Main.LogV("[P2Save] skip save: PlayerPath unavailable"); return; }

                var sb = new StringBuilder();
                var hotbar = Main.GetP2HotbarEntries();
                foreach (var e in hotbar) sb.AppendLine(Line("H", e));
                int bp = 0;
                foreach (var r in P2InventoryStore.GetP2BackpackSnapshot() ?? new RGD_Slot[0])
                    if (r != null && r.HasItem)
                    {
                        sb.AppendLine(Line("B", new P2SaveEntry { slot = r.slotIndex, itemIndex = r.itemIndex, amount = r.itemAmount, uses = r.itemUses, exclusive = r.exclusiveString }));
                        bp++;
                    }

                
                var st = p2.Stats;
                if (st != null)
                    sb.AppendLine(string.Join("\t", "S",
                        F(st.stat_health.NormalValue), F(st.stat_BonusHealth.NormalValue),
                        F(st.stat_hunger.Normal.NormalValue), F(st.stat_hunger.Bonus.NormalValue),
                        F(st.stat_thirst.Normal.NormalValue), F(st.stat_thirst.Bonus.NormalValue),
                        F(st.stat_oxygen.NormalValue)));

                
                
                int eq = 0;
                foreach (var name in P2EquipmentStore.EquippedNames())
                { sb.AppendLine("EN\t" + name); eq++; }

                
                if (p2.PersonController != null && p2.PersonController.HasRaftAsParent)
                {
                    var lp = p2.transform.localPosition;
                    sb.AppendLine(string.Join("\t", "P", "1", F(lp.x), F(lp.y), F(lp.z), F(p2.transform.localEulerAngles.y)));
                }

                
                int bf = 0;
                if (p2.BuffManager != null && p2.BuffManager.buffs != null)
                    foreach (var b in p2.BuffManager.buffs)
                        if (b != null && b.Asset != null)
                        { sb.AppendLine(string.Join("\t", "U", ((int)b.Asset.UniqueBuffIndex).ToString(CultureInfo.InvariantCulture), F(b.NormalizedTimer))); bf++; }

                
                if (p2.HazmatSuit != null && p2.HazmatSuit.Timer > 0f)
                    sb.AppendLine("Z\t" + F(p2.HazmatSuit.Timer));

                File.WriteAllText(path, sb.ToString());
                LastSaveFrame = Time.frameCount;     
                Main.LogV($"[P2Save] saved -> {path} (hotbar {hotbar.Count} / backpack {bp} / equip {eq} / buff {bf} / stats+position+protection)");
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log($"[P2Save] 保存异常: {e.Message}");
            }
        }

        
        public static bool LoadAndRestoreP2(Network_Player p2)
        {
            try
            {
                LastSaveFrame = -1;                  
                if ((Object)p2 == null) return false;
                string requestedPath = GetPath();
                string path = ResolveLoadPath(requestedPath);
                if (path == null || !File.Exists(path))
                {
                    // Backpack data is reset on world leave, while hotbar and
                    // equipment are purely in-memory stores.  Clear all three
                    // ownership surfaces when this world has no save file.
                    Main.ResetP2HotbarForFreshWorld();
                    P2EquipmentStore.ResetForFreshWorld(p2);
                    Main.LogV("[P2Save] no save file, P2 starts fresh");
                    return false;
                }

                var hotbar = new List<P2SaveEntry>();
                var slots = new List<RGD_Slot>();
                P2SaveStats stats = null; var equip = new List<int>(); var equipNames = new List<string>(); P2SavePos pos = null;
                var buffs = new List<KeyValuePair<int, float>>(); float hazmat = -1f;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var p = raw.Split('\t');
                    string kind = p[0];
                    if ((kind == "H" || kind == "B") && p.Length >= 5)
                    {
                        var e = new P2SaveEntry
                        {
                            slot      = int.Parse(p[1], CultureInfo.InvariantCulture),
                            itemIndex = int.Parse(p[2], CultureInfo.InvariantCulture),
                            amount    = int.Parse(p[3], CultureInfo.InvariantCulture),
                            uses      = int.Parse(p[4], CultureInfo.InvariantCulture),
                            exclusive = p.Length >= 6 ? p[5] : ""
                        };
                        if (kind == "H") hotbar.Add(e);
                        else slots.Add(new RGD_Slot { slotIndex = (short)e.slot, itemIndex = (short)e.itemIndex, itemAmount = (short)e.amount, itemUses = (short)e.uses, exclusiveString = e.exclusive });
                    }
                    else if (kind == "S" && p.Length >= 8)
                        stats = new P2SaveStats { health = PF(p[1]), bonusHealth = PF(p[2]), hunger = PF(p[3]), bonusHunger = PF(p[4]), thirst = PF(p[5]), bonusThirst = PF(p[6]), oxygen = PF(p[7]) };
                    else if (kind == "EN" && p.Length >= 2)
                        equipNames.Add(p[1]);                                         
                    else if (kind == "E" && p.Length >= 2)
                        equip.Add(int.Parse(p[1], CultureInfo.InvariantCulture));     
                    else if (kind == "P" && p.Length >= 6)
                        pos = new P2SavePos { parented = p[1] == "1", x = PF(p[2]), y = PF(p[3]), z = PF(p[4]), roty = PF(p[5]) };
                    else if (kind == "U" && p.Length >= 3)
                        buffs.Add(new KeyValuePair<int, float>(int.Parse(p[1], CultureInfo.InvariantCulture), PF(p[2])));
                    else if (kind == "Z" && p.Length >= 2)
                        hazmat = PF(p[1]);
                }

                Main.LoadP2HotbarEntries(hotbar);
                P2InventoryStore.SetP2Backpack(slots.Count > 0 ? slots.ToArray() : null);
                ApplyP2Extras(p2, stats, equip, equipNames, pos, buffs, hazmat);
                SyncFallbackToRequestedPath(path, requestedPath);

                Main.LogV($"[P2Save] restored <- {path} (hotbar {hotbar.Count} / backpack {slots.Count} / equip {equipNames.Count + equip.Count} / buff {buffs.Count} / stats {(stats != null)})");
                return true;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log($"[P2Save] 恢复异常: {e.Message}");
                return false;
            }
        }

        
        static string ResolveLoadPath(string requestedPath)
        {
            if (string.IsNullOrEmpty(requestedPath)) return null;

            if (File.Exists(requestedPath))
                return requestedPath;

            string legacy = GetLegacyGuidPath();
            if (!string.IsNullOrEmpty(legacy) && File.Exists(legacy))
            {
                Main.ModEntry.Logger.Log("[P2Save] Migrating legacy WorldGuid-bound P2 save for current world: " + legacy);
                return legacy;
            }
            return requestedPath;
        }

        static void SyncFallbackToRequestedPath(string loadedPath, string requestedPath)
        {
            if (string.IsNullOrEmpty(loadedPath) || string.IsNullOrEmpty(requestedPath)) return;
            if (string.Equals(loadedPath, requestedPath, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                File.Copy(loadedPath, requestedPath, true);
                Main.ModEntry.Logger.Log("[P2Save] Fallback save synced to current WorldGuid: " + requestedPath);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log("[P2Save] Fallback sync failed: " + e.Message);
            }
        }

        static void ApplyP2Extras(Network_Player p2, P2SaveStats s, List<int> equip, List<string> equipNames, P2SavePos pos,
                                  List<KeyValuePair<int, float>> buffs, float hazmat)
        {
            try
            {
                var st = p2.Stats;
                if (s != null && st != null)
                {
                    st.stat_health.NormalValue       = s.health;
                    st.stat_BonusHealth.NormalValue  = s.bonusHealth;
                    st.stat_hunger.Normal.NormalValue = s.hunger;
                    st.stat_hunger.Bonus.NormalValue  = s.bonusHunger;
                    st.stat_thirst.Normal.NormalValue = s.thirst;
                    st.stat_thirst.Bonus.NormalValue  = s.bonusThirst;
                    st.stat_oxygen.NormalValue        = s.oxygen;
                }

                
                if (p2.PlayerEquipment != null && equip != null && equip.Count > 0)
                {
                    foreach (int idx in equip) p2.PlayerEquipment.EquipItemNetwork(idx);
                    P2EquipmentStore.RebuildFromModel(p2);
                }
                
                P2EquipmentStore.RestoreFromNames(equipNames);

                
                if (pos != null && pos.parented)
                {
                    var pivot = SingletonGeneric<GameManager>.Singleton != null ? SingletonGeneric<GameManager>.Singleton.lockedPivot : null;
                    if (pivot != null)
                    {
                        p2.transform.SetParent(pivot);
                        p2.transform.localPosition = new Vector3(pos.x, pos.y, pos.z);
                        p2.transform.localEulerAngles = new Vector3(0f, pos.roty, 0f);
                    }
                }

                if (buffs != null && p2.BuffManager != null)
                    foreach (var kv in buffs)
                    {
                        if (kv.Value < 0f || kv.Key < 0) continue;
                        var asset = BuffManager.GetBuffFromIndex(kv.Key);
                        if (asset != null) p2.BuffManager.AddBuff(asset)?.SetNormalizedTimer(kv.Value);
                    }

                if (hazmat > 0f && p2.HazmatSuit != null) p2.HazmatSuit.EquipNetworked(hazmat);
            }
            catch (Exception e) { Main.ModEntry.Logger.Log("[P2Save] 应用状态/装备/位置/buff 异常: " + e.Message); }
        }
    }
}
