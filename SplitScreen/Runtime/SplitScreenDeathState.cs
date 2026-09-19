namespace SplitScreen
{
    internal sealed class SplitScreenDeathState
    {
        internal bool P1CarryViewWasHeld { get; set; }

        internal void SyncVanillaLocalDeathFlag(bool bothPlayersDead)
        {
            Player.LocalPlayerIsDead = bothPlayersDead;
        }
    }
}
