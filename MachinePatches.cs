using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using Object = StardewValley.Object;

namespace ShedConsoleComputer
{
    /// <summary>
    /// 机器状态每变一次（倒计时、投料、收料都会触发）就检查一次：
    /// 有成品立刻收进成果箱、有空位立刻投下一份原料，不用等每秒扫描。
    /// 这就是"酒好了马上自动收"的来源。
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

            GameLocation? location = __instance.Location;
            if (location == null || !mod.Manager.HasConsole(location))
                return;

            bool ready = __instance.readyForHarvest.Value && __instance.heldObject.Value != null && __instance.MinutesUntilReady <= 0;
            bool emptyIdle = __instance.heldObject.Value == null && !__instance.readyForHarvest.Value;
            if (ready || emptyIdle)
                mod.Manager.ProcessMachineNow(location, __instance);
        }
    }

    /// <summary>
    /// 玩家手动往机器里放原料后：让电脑立刻看到这个空位变化（主要给其他机器腾优先级）。
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

            GameLocation? location = __instance.Location;
            if (location != null && mod.Manager.HasConsole(location))
                mod.Manager.ProcessMachineNow(location, __instance);
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
