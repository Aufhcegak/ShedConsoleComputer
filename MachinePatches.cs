using System.Collections.Generic;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using Object = StardewValley.Object;

namespace ShedConsoleComputer
{
    /// <summary>
    /// 机器状态每变一次（倒计时、投料、收料都会触发）就标记一次：
    /// 把机器加入待处理队列，由主线程 UpdateTicked 在冻结期外统一处理（收成品/投原料）。
    ///
    /// <b>关键</b>：minutesElapsed / performObjectDropInAction 跑在 passTimeForObjects 的
    /// objects.Lock() 冻结期内，直接写对象表（收/投）会排队到 Unlock 集中爆发——这就是
    /// 与 BidirectionalHopper 等机器 mod 共存时每 10 分钟切换卡顿的来源。
    /// 冻结期内只做标记（不写对象表），处理挪到主线程，卡顿消除。
    /// </summary>
    [HarmonyPatch(typeof(Object), nameof(Object.minutesElapsed))]
    internal static class MachineMinutesPatch
    {
        private static void Postfix(Object __instance)
        {
            ModEntry? mod = ModEntry.Instance;
            if (mod?.Manager == null || !Context.IsWorldReady)
                return;

            // 快速返回：不是受支持的机器（非大物件/是箱子）就直接跳过，
            // 避免每个作物、每个栅栏都跑一次 GetState/扫描。
            if (!__instance.bigCraftable.Value || __instance is StardewValley.Objects.Chest)
                return;

            // 联机:只主机排队处理(访客的机器由主机同步状态,访客本地不跑自动化)
            if (!Context.IsMainPlayer)
                return;

            GameLocation? location = __instance.Location;
            if (location == null || !mod.Manager.HasConsole(location))
                return;

            bool ready = __instance.readyForHarvest.Value && __instance.heldObject.Value != null && __instance.MinutesUntilReady <= 0;
            bool emptyIdle = __instance.heldObject.Value == null && !__instance.readyForHarvest.Value;
            if (ready || emptyIdle)
                mod.Manager.QueueMachine(location, __instance); // 冻结期内只标记，不处理
        }
    }

    /// <summary>
    /// 玩家手动往机器里放原料后：让电脑立刻看到这个空位变化（主要给其他机器腾优先级）。
    /// 同冻结期安全策略：只标记，主线程处理。
    /// </summary>
    [HarmonyPatch(typeof(Object), nameof(Object.performObjectDropInAction))]
    internal static class MachineDropInPatch
    {
        private static void Postfix(Object __instance)
        {
            ModEntry? mod = ModEntry.Instance;
            if (mod?.Manager == null || !Context.IsWorldReady)
                return;

            if (!__instance.bigCraftable.Value || __instance is StardewValley.Objects.Chest)
                return;

            // 联机:只主机排队处理
            if (!Context.IsMainPlayer)
                return;

            GameLocation? location = __instance.Location;
            if (location != null && mod.Manager.HasConsole(location))
                mod.Manager.QueueMachine(location, __instance); // 只标记，主线程处理
        }
    }

    /// <summary>
    /// 检测"玩家用左键一次性收走整堆产物"（原版左键收酒走这条路）：
    /// 拦住随后 30 秒的自动投料。否则玩家收酒的同一秒 Mod 就把新水果塞回机器，
    /// 左键会从"收酒"变成"放水果"，表现为点很多次才能把酒拿到手。
    /// </summary>
    [HarmonyPatch(typeof(Object), nameof(Object.checkForAction))]
    internal static class ManualCollectPatch
    {
        private sealed class Snapshot
        {
            public Object? Held;
            public bool Ready;
        }

        private static void Prefix(Object __instance, out Snapshot __state)
        {
            __state = new Snapshot
            {
                Held = __instance.heldObject.Value,
                Ready = __instance.readyForHarvest.Value
            };
        }

        private static void Postfix(Object __instance, bool __result, Snapshot __state)
        {
            if (!__result || __state.Held == null || !__state.Ready)
                return;

            ModEntry? mod = ModEntry.Instance;
            if (mod?.Manager == null || !Context.IsWorldReady)
                return;

            if (!__instance.bigCraftable.Value || __instance is StardewValley.Objects.Chest)
                return;

            // 联机:只有主机记录手动收酒冷却(访客收酒由主机状态同步,冷却只对主机自动化有意义)
            if (!Context.IsMainPlayer)
                return;

            // 整堆被拿走（heldObject 清空）才算手动收走；
            // 部分收取（右键单收一半）不触发冷却，避免正常自动化被误停。
            if (__instance.heldObject.Value == null)
            {
                GameLocation? location = __instance.Location;
                if (location != null && mod.Manager.HasConsole(location))
                    mod.Manager.NotifyManualCollect(location, __instance);
            }
        }
    }
}
