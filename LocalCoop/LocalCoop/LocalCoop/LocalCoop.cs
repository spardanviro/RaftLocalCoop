using RaftModLoader;
using UnityEngine;
using HMLLibrary;
using SplitScreen;

public class LocalCoop : Mod
{
    public void Start()
    {
        bool active = Main.Boot();
        if (active)
            Debug.Log("[SplitScreen] LocalCoop 已加载（RaftModLoader）。F9 生成 P2，X=P2 复活，Y=P2 菜单。");
        else
            Debug.Log("[SplitScreen] LocalCoop disabled because another SplitScreen loader is active.");
    }

    public void Update()
    {
        Main.OnUpdate(Time.deltaTime);
    }

    public void OnModUnload()
    {
        Main.Shutdown();
        Debug.Log("[SplitScreen] LocalCoop 已卸载。");
    }
}
