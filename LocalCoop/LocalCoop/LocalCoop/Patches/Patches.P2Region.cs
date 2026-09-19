using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // 【P2 看不到/穿过海底建筑(瓦鲁那 Foreman's Office 等)】的真正根因。
    // 建筑内容的显隐由 Region/Subregion 系统驱动:SubRegionComponent_ObjectEnabler 按
    // subregion.playerIsWithinSubregion 对区域内对象(如 MeshCombine_Underwater)SetActive
    // (连碰撞一起,故隐藏时 P2 能穿过)。而 playerIsWithinSubregion 只由 RegionManager 按
    // 【单个本地玩家 P1 的位置】更新:
    //   RegionManager.LateUpdate: localPlayer = ComponentManager<Network_Player>.Value(=P1);
    //   UpdateCurrentRegionAndSubregions(P1头/相机位置) → 每个 subregion 只判 P1 在不在区域内。
    // 于是 P1 一进水(进入 Varuna 水下区域体积)就激活内容,P1 在水面则整片对所有人关闭 ——
    // 哪怕 P2 正潜在建筑里。这也解释"P1 在水里就行、不用潜水"(是进入区域体积,不是相机没入)。
    // 与 ClosestPlayer/IsAttached 同族:vanilla 按单个本地玩家做的判定,分屏第二人被无视。
    //
    // 修法:接管 UpdateCurrentRegionAndSubregions,把每个 region/subregion 的"在区域内"判定
    // 改为【P1 或 P2 任一】在内。P1 侧仍用 vanilla 传入的采样位置;未开分屏直接走原版。
    // 覆盖所有 SubregionComponent(物件/光照/声音/天气/氧气),不止建筑。方向安全(只多激活)。
    [HarmonyPatch(typeof(RegionManager), "UpdateCurrentRegionAndSubregions")]
    static class Patch_RegionManager_IncludeP2
    {
        static bool In(AreaZone area, Vector3 a, Vector3 b)
            => area != null && (area.GetNormalizedDistanceToPoint(a) > 0f || area.GetNormalizedDistanceToPoint(b) > 0f);

        static bool Prefix(Vector3 position)
        {
            var p2 = Main.player2;
            if (p2 == null) return true;   // 未开分屏 → 纯 vanilla

            // P2 采样位置:身体头骨优先(区域是大体积,身体位置最稳);兜底相机、再兜底 P1 位置。
            Vector3 p2Pos = p2.PlayerHeadBone != null ? p2.PlayerHeadBone.position
                          : (p2.CameraTransform != null ? p2.CameraTransform.position : position);

            // —— 以下复刻 vanilla UpdateCurrentRegionAndSubregions,唯一改动:In() 判 P1 或 P2 ——
            if (RegionManager.CurrentRegions == null) RegionManager.CurrentRegions = new List<Region>();
            else RegionManager.CurrentRegions.Clear();

            if (RegionManager.CurrentSubregions == null) RegionManager.CurrentSubregions = new List<Subregion>();
            else
            {
                foreach (var s in RegionManager.CurrentSubregions)
                    if (s != null) s.playerIsWithinSubregion = false;
                RegionManager.CurrentSubregions.Clear();
            }

            if (RegionManager.AllRegions != null)
            {
                foreach (var region in RegionManager.AllRegions)
                {
                    if (region == null || region.regionArea == null) continue;
                    if (!region.gameObject.activeInHierarchy) continue;
                    if (!In(region.regionArea, position, p2Pos)) continue;

                    RegionManager.CurrentRegions.Add(region);
                    if (region.subregions == null) continue;
                    for (int i = 0; i < region.subregions.Length; i++)
                    {
                        var sub = region.subregions[i];
                        if (sub == null) continue;

                        sub.playerIsWithinSubregion = In(sub.regionArea, position, p2Pos);
                        if (sub.playerIsWithinSubregion) RegionManager.CurrentSubregions.Add(sub);
                    }
                }
            }

            if (RegionManager.OnRegionUpdate != null) RegionManager.OnRegionUpdate();
            return false;
        }
    }
}
