using UnityEngine;

namespace SplitScreen
{
    public sealed class UmmShim
    {
        public readonly UmmLogger Logger = new UmmLogger();
        public readonly UmmInfo   Info   = new UmmInfo();
    }

    public sealed class UmmLogger
    {
        public void Log(string msg) => Debug.Log("[SplitScreen] " + msg);
    }

    public sealed class UmmInfo
    {
        public string Id = "com.localcoop.splitscreen";
    }
}
