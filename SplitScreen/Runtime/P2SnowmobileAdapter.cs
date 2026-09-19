using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    // 雪橇车(Snowmobile)P2 上/下车适配:复用 vanilla 私有 TryTakingSeat / LeaveSeat,
    // 外面套 P2OriginalScope.Interaction() 把 P2 临时强制成本地玩家,让 vanilla 座位逻辑
    // (相机/动画/AttachPlayer 挂载/引擎启动)以 P2 身份正确跑。整体仿 P2FurnitureAdapter(椅子)。
    // 说明:雪橇车与 PlayerSeat 结构同型——都是 AttachPlayer[] 座位 + TryTakingSeat(ref int)/LeaveSeat(player)。
    internal static class P2SnowmobileAdapter
    {
        static readonly FieldInfo s_attachScripts =
            AccessTools.Field(typeof(Snowmobile), "attachPlayerScripts");
        static readonly MethodInfo s_tryTake =
            AccessTools.Method(typeof(Snowmobile), "TryTakingSeat");
        static readonly MethodInfo s_leaveByPlayer =
            AccessTools.Method(typeof(Snowmobile), "LeaveSeat", new[] { typeof(Network_Player) });

        static AttachPlayer[] Seats(Snowmobile sm)
        {
            return s_attachScripts?.GetValue(sm) as AttachPlayer[];
        }

        internal static bool HasFreeSeat(Snowmobile sm)
        {
            var seats = Seats(sm);
            if (seats == null) return false;
            foreach (var a in seats)
                if (a != null && !a.HasPlayerAttached) return true;
            return false;
        }

        internal static bool IsPlayerSeated(Snowmobile sm, Network_Player player)
        {
            if (sm == null || player == null) return false;
            var seats = Seats(sm);
            if (seats == null) return false;
            foreach (var a in seats)
                if (a != null && a.carriedPlayer == player) return true;
            return false;
        }

        internal static bool TryTakeSeat(Snowmobile sm, Network_Player player)
        {
            if (sm == null || player == null || !HasFreeSeat(sm) || s_tryTake == null) return false;

            using (new PlayerItemBusyScope())
            using (P2OriginalScope.Interaction())
            {
                // TryTakingSeat(Network_Player player, ref int seatIndex):seatIndex=-1 让 host 自动找空位。
                // 反射对 ref int:传 object[] 装箱,方法写回数组元素即可(与 P2FurnitureAdapter 同法)。
                object[] args = { player, -1 };
                var result = s_tryTake.Invoke(sm, args);
                return result is bool ok && ok;
            }
        }

        internal static void LeaveSeat(Snowmobile sm, Network_Player player)
        {
            if (sm == null || player == null || s_leaveByPlayer == null) return;

            using (new PlayerItemBusyScope())
            using (P2OriginalScope.Interaction())
                s_leaveByPlayer.Invoke(sm, new object[] { player });
        }
    }
}
