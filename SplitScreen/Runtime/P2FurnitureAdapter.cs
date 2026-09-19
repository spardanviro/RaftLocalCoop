using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    internal static class P2FurnitureAdapter
    {
        static readonly MethodInfo s_seatTryTake =
            AccessTools.Method(typeof(PlayerSeat), "TryTakingSeat");
        static readonly MethodInfo s_seatLeaveByPlayer =
            AccessTools.Method(typeof(PlayerSeat), "LeaveSeat", new[] { typeof(Network_Player) });
        static readonly FieldInfo s_wardrobePlaceableField =
            typeof(Block_Wardrobe).GetField("placeableWardrobe", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_wardrobeInputReleasedField =
            typeof(Block_Wardrobe).GetField("inputReleased", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool SeatHasFree(PlayerSeat seat)
        {
            var seats = seat?.Seats;
            if (seats == null) return false;
            foreach (var attach in seats)
                if (attach != null && !attach.HasPlayerAttached) return true;
            return false;
        }

        internal static bool TryTakeSeat(PlayerSeat seat, Network_Player player)
        {
            if (seat == null || player == null || !SeatHasFree(seat) || s_seatTryTake == null) return false;

            using (new PlayerItemBusyScope())
            using (P2OriginalScope.Interaction())
            {
                object[] args = { player, -1 };
                var result = s_seatTryTake.Invoke(seat, args);
                return result is bool ok && ok;
            }
        }

        internal static void LeaveSeat(PlayerSeat seat, Network_Player player)
        {
            if (seat == null || player == null || s_seatLeaveByPlayer == null) return;

            using (new PlayerItemBusyScope())
            using (P2OriginalScope.Interaction())
                s_seatLeaveByPlayer.Invoke(seat, new object[] { player });
        }

        internal static bool StopAttachedPlayer(AttachPlayer attach, Network_Player player, bool manipulatePosition)
        {
            if (attach == null || player == null || !ReferenceEquals(attach.carriedPlayer, player)) return false;

            using (new PlayerItemBusyScope())
            using (P2OriginalScope.Interaction())
                attach.StopCarryingPlayer(manipulatePosition);
            return true;
        }

        internal static void CompleteBedRespawn(Player player)
        {
            if (player == null) return;
            using (new PlayerItemBusyScope())
            using (P2OriginalScope.Interaction())
                player.OnRespawnComplete();
        }

        internal static void StopSleeping(BedComponent bedComponent)
        {
            if (bedComponent == null) return;
            using (new PlayerItemBusyScope())
            using (P2OriginalScope.Interaction())
                bedComponent.StopSleep();
        }

        internal static bool OpenWardrobe(Block_Wardrobe wardrobe, Network_Player player)
        {
            if (wardrobe == null || player == null) return false;
            s_wardrobeInputReleasedField?.SetValue(wardrobe, false);
            WardrobeMenu.wardrobeOpened = null;
            SendWardrobeMessage(wardrobe, player, Messages.Wardrobe_Open);
            return true;
        }

        internal static void CloseWardrobe(Block_Wardrobe wardrobe, Network_Player player)
        {
            if (wardrobe == null || player == null) return;
            SendWardrobeMessage(wardrobe, player, Messages.Wardrobe_Close);
            WardrobeMenu.wardrobeOpened = null;
        }

        static void SendWardrobeMessage(Block_Wardrobe wardrobe, Network_Player player, Messages message)
        {
            var placeable = s_wardrobePlaceableField?.GetValue(wardrobe) as Placeable_Wardrobe;
            var network = placeable != null ? placeable.network : null;
            if (placeable == null || network == null || player == null) return;

            var msg = new Message_NetworkBehaviour_ID_SteamID(
                message,
                network.NetworkIDManager,
                placeable.ObjectIndex,
                player.steamID);
            placeable.Deserialize(msg, network.HostID);
        }
    }
}
