namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Cm — 共享单例安全访问器(阶段 2:ComponentManager 安全化,mod 侧)
    //
    //  ComponentManager<T> 是 vanilla 泛型,Harmony 无法给它加方法,故在 mod 侧
    //  提供安全入口。语义与直接读 .Value 完全一致(仍经 Patch_CM_Get_* 路由到
    //  PlayerContext.Active),额外做两层检查:
    //    · null 检查;
    //    · Unity "已 Destroy 但引用未置 null" 检查——靠 (UnityEngine.Object)v != null
    //      触发 Unity 的重载相等(普通 v != null 检测不到已销毁对象 → 解引用即 NRE)。
    //
    //  旧入口 ComponentManager<T>.Value 保留不动;新代码/崩溃高发点改用此处,
    //  避免退出世界 / P2 生灭 / 逐帧 Tick 窗口读到 null 或已销毁单例。
    // ══════════════════════════════════════════════════════════════════════
    internal static class Cm
    {
        // 读共享单例;不可用(null 或已销毁)时返回 null。
        public static T Get<T>() where T : UnityEngine.Object
        {
            var v = ComponentManager<T>.Value;                 // 经现有 getter 补丁路由,语义不变
            return (UnityEngine.Object)v != null ? v : null;   // 覆盖"未 null 但已 Destroy"的 Unity 对象
        }

        // 读共享单例;可用返回 true 并输出,否则 false + out null。
        //  典型用法:if (!Cm.TryGet<PlayerInventory>(out var inv)) return;
        public static bool TryGet<T>(out T value) where T : UnityEngine.Object
        {
            value = Get<T>();
            return value != null;
        }
    }
}
