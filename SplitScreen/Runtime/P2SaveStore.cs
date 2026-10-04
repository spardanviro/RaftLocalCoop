using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2SaveStore — P2 独立持久化存档
    //
    //  P2 没有独立的真实 PlayerInventory（与 P1 共享单例），其物品实际存在：
    //    · 快捷栏 Main._p2Hotbar (ItemInstance[10])
    //    · 背包   P2InventoryStore._p2Slots (RGD_Slot[]，非热栏槽快照)
    //  故不能用 RGD_Player(P2) 序列化(会撞 P1)。改为把这两处导出成简单 DTO，
    //  以 JSON 存到 PlayerPath 下、按世界 GUID 区分的独立文件，与 P1 存档互不干扰。
    //
    //  保存：SaveAndLoad.SaveUser 之后(Postfix)。
    //  恢复：F9 生成 P2 后。无存档 → P2 沿用默认快捷栏(锤/斧/刷) + 空背包。
    // ══════════════════════════════════════════════════════════════════════
    public class P2SaveEntry
    {
        public int slot;        // 快捷栏=槽号0-9；背包=allSlots 索引
        public int itemIndex;   // Item_Base.UniqueIndex
        public int amount;
        public int uses;
        public string exclusive;
    }

    class P2SaveStats { public float health, bonusHealth, hunger, bonusHunger, thirst, bonusThirst, oxygen; }
    class P2SavePos { public bool parented; public float x, y, z, roty; }

    public static class P2SaveStore
    {
        // 本次 P2 会话内最近一次成功落盘的帧号(-1=本会话尚未落盘)。供 ResetForWorldLeave 核验
        //  "清内存前是否已落盘"的生命周期不变量;P2 生成(LoadAndRestoreP2)时归 -1 表示新会话。
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

        // 行格式(制表符分隔)：kind  slot  itemIndex  amount  uses  exclusive
        //  kind = H(热栏) / B(背包)；exclusive 放最后(可空，允许含空格)。无外部序列化依赖。
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

                // 状态(生命/奖励生命/饥饿/奖励饥饿/口渴/奖励口渴/氧气)
                var st = p2.Stats;
                if (st != null)
                    sb.AppendLine(string.Join("\t", "S",
                        F(st.stat_health.NormalValue), F(st.stat_BonusHealth.NormalValue),
                        F(st.stat_hunger.Normal.NormalValue), F(st.stat_hunger.Bonus.NormalValue),
                        F(st.stat_thirst.Normal.NormalValue), F(st.stat_thirst.Bonus.NormalValue),
                        F(st.stat_oxygen.NormalValue)));

                // 装备:用 P2EquipmentStore 功能集(权威,含滑索工具等【非模型装备】) → 物品 UniqueName。
                //  旧版只存 PlayerEquipment.GetEquipedIndexes(模型装备索引)→ 滑索工具不是模型装备,从不被存 → 重进消失。
                int eq = 0;
                foreach (var name in P2EquipmentStore.EquippedNames())
                { sb.AppendLine("EN\t" + name); eq++; }

                // 位置(仅记录在筏上的局部坐标 + 朝向)
                if (p2.PersonController != null && p2.PersonController.HasRaftAsParent)
                {
                    var lp = p2.transform.localPosition;
                    sb.AppendLine(string.Join("\t", "P", "1", F(lp.x), F(lp.y), F(lp.z), F(p2.transform.localEulerAngles.y)));
                }

                // 增益(buff)：唯一索引 + 归一化剩余时间
                int bf = 0;
                if (p2.BuffManager != null && p2.BuffManager.buffs != null)
                    foreach (var b in p2.BuffManager.buffs)
                        if (b != null && b.Asset != null)
                        { sb.AppendLine(string.Join("\t", "U", ((int)b.Asset.UniqueBuffIndex).ToString(CultureInfo.InvariantCulture), F(b.NormalizedTimer))); bf++; }

                // 防化服剩余时间
                if (p2.HazmatSuit != null && p2.HazmatSuit.Timer > 0f)
                    sb.AppendLine("Z\t" + F(p2.HazmatSuit.Timer));

                WriteAtomic(path, sb.ToString());
                LastSaveFrame = Time.frameCount;     // 记录落盘帧:供离开世界时核验"先落盘后清内存"
                Main.LogV($"[P2Save] saved -> {path} (hotbar {hotbar.Count} / backpack {bp} / equip {eq} / buff {bf} / stats+position+protection)");
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log($"[P2Save] 保存异常: {e.Message}");
            }
        }

        
        // 先写临时文件再替换,并留一份 .bak:写到一半崩溃/断电不会毁掉原存档。
        static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            try { File.Replace(tmp, path, path + ".bak"); }
            catch (Exception)
            {
                File.Copy(path, path + ".bak", true);
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
        }

        // 主文件缺失或为空(上次写入中断)时退回 .bak。
        static string PreferBackupIfBroken(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string bak = path + ".bak";
            if (!File.Exists(bak)) return path;
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            Main.ModEntry.Logger.Log("[P2Save] 主存档缺失或为空,改读备份: " + bak);
            return bak;
        }

        // 留一份损坏原件,避免之后的存档把它覆盖掉。
        static void PreserveCorrupt(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string keep = path + ".corrupt";
                File.Copy(path, keep, true);
                Main.ModEntry.Logger.Log("[P2Save] 存档含损坏内容,已另存一份: " + keep);
            }
            catch (Exception e) { Main.ModEntry.Logger.Log("[P2Save] 另存损坏存档失败: " + e.Message); }
        }

        public static bool LoadAndRestoreP2(Network_Player p2)
        {
            try
            {
                LastSaveFrame = -1;                  // 新 P2 会话开始:本会话尚未落盘
                if ((Object)p2 == null) return false;
                string requestedPath = GetPath();
                string path = PreferBackupIfBroken(ResolveLoadPath(requestedPath));
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
                int badLines = 0;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    try   // 逐行容错:一行坏掉只丢这一行,不整份放弃
                    {
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
                        equipNames.Add(p[1]);                                         // 新:装备 UniqueName(功能集,含滑索工具)
                    else if (kind == "E" && p.Length >= 2)
                        equip.Add(int.Parse(p[1], CultureInfo.InvariantCulture));     // 旧:PlayerEquipment 模型索引(兼容老存档护甲)
                    else if (kind == "P" && p.Length >= 6)
                        pos = new P2SavePos { parented = p[1] == "1", x = PF(p[2]), y = PF(p[3]), z = PF(p[4]), roty = PF(p[5]) };
                    else if (kind == "U" && p.Length >= 3)
                        buffs.Add(new KeyValuePair<int, float>(int.Parse(p[1], CultureInfo.InvariantCulture), PF(p[2])));
                    else if (kind == "Z" && p.Length >= 2)
                        hazmat = PF(p[1]);
                    }
                    catch (Exception pe) { badLines++; Main.ModEntry.Logger.Log("[P2Save] 跳过损坏行: " + pe.Message); }
                }

                if (badLines > 0) PreserveCorrupt(path);
                // 装备 store 是纯托管静态,先清掉上个世界的残留(store+模型),再按本存档恢复。
                P2EquipmentStore.ResetForFreshWorld(p2);
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
                PreserveCorrupt(GetPath());
                // 读档失败时 P2 以干净状态开局,不带上个世界的手持栏/装备。
                try { Main.ResetP2HotbarForFreshWorld(); P2EquipmentStore.ResetForFreshWorld(p2); } catch (Exception) { }
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
            // 诊断日志：每次 InitializeComponents 之后都报告关键组件状态
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

                // 旧存档(E=模型装备索引):先 EquipItemNetwork 上模型,再从模型重建功能集(兼容老护甲存档)。
                if (p2.PlayerEquipment != null && equip != null && equip.Count > 0)
                {
                    foreach (int idx in equip) p2.PlayerEquipment.EquipItemNetwork(idx);
                    P2EquipmentStore.RebuildFromModel(p2);
                }
                // 新存档(EN=UniqueName,权威功能集,含滑索工具):逐件装备(功能集 + 模型装备上 P2 身)。
                P2EquipmentStore.RestoreFromNames(equipNames);

                // 位置：仅恢复"在筏上"的局部坐标(常见情形)，避免世界漂移/换图的复杂处理。
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
