using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using StardewValley.Objects;
using Object = StardewValley.Object;

// 让 logic_test 测试工程能访问 internal 方法（数量预检等）
[assembly: InternalsVisibleTo("logic_test")]

namespace ShedConsoleComputer
{
    /// <summary>
    /// 自动化核心（纯逻辑，不依赖 SMAPI/界面）：
    /// 投料、收成品、机器判定全部集中在这里，便于本地测试覆盖全部情况。
    /// ConsoleManager 只是它的"SMAPI 胶水"（缓存、存档、事件）。
    /// </summary>
    public static class AutomationCore
    {
        /// <summary>电脑大物件的数据键（与 ModEntry.ConsoleUnqualified 一致，ModEntry 引用这里）。</summary>
        public const string ConsoleUnqualified = "Claude.ShedConsoleComputer_Console";

        /*********
        ** 机器判定
        *********/

        public static bool IsMachine(Object? obj, ModConfig config)
        {
            if (obj == null || !obj.bigCraftable.Value)
                return false;
            return obj.ItemId switch
            {
                "12" => true,                          // 小桶
                "163" => config.IncludeCasks,          // 木桶（陈酿）
                "15" => config.IncludePreservesJar,    // 罐头瓶
                _ => false
            };
        }

        public static bool IsConsole(Object? obj)
        {
            return obj != null
                && obj.bigCraftable.Value
                && obj.ItemId == ConsoleUnqualified;
        }

        /// <summary>预检：这台机器能不能吃这个物品（不查数量——数量由原版规则 RequiredCount 决定）。</summary>
        public static bool CanMachineAccept(Object machine, Item? item)
        {
            if (item is not Object obj || machine == null)
                return false;
            return machine.ItemId switch
            {
                "12" => // 小桶：水果 / 蔬菜 / 啤酒花 / 小麦 / 咖啡豆 / 茶叶 / 蜂蜜
                    obj.Category == -79 || obj.Category == -75
                    || obj.QualifiedItemId is "(O)304" or "(O)433" or "(O)262" or "(O)815" or "(O)340",
                "163" => // 木桶：酒
                    obj.Category == -26,
                "15" => // 罐头瓶：水果 / 蔬菜
                    obj.Category == -79 || obj.Category == -75,
                _ =>
                    obj.Category < 0
            };
        }

        /// <summary>机器数据源。游戏里默认用原版 GetMachineData()（走 content 管线）；
        /// 测试里注入模拟数据（AutomationCore 的参数类型是 Object，虚调用不会命中子类的
        /// new 隐藏方法，所以需要一个可替换的数据源）。</summary>
        public static Func<Object, MachineData?> MachineDataProvider = DefaultMachineData;

        private static MachineData? DefaultMachineData(Object o)
        {
            try
            {
                return o.GetMachineData();
            }
            catch (Exception)
            {
                return null; // 数据异常按"无规则"处理，安全方向
            }
        }

        /// <summary>收成品时的"放入成果箱"策略。默认走原版 Utility.addItemToThisInventoryList
        /// （游戏里行为与原版完全一致）；测试里注入模拟实现（纯逻辑，避免原版方法在无游戏
        /// 环境下原生崩溃）。返回放不下的剩余（null = 全放进去了）。</summary>
        public static Func<Object, Inventory, Item?> CollectPutDelegate = DefaultCollectPut;

        private static Item? DefaultCollectPut(Object held, Inventory output)
        {
            return Utility.addItemToThisInventoryList(held, output, 36);
        }

        /*********
        ** 投料
        *********/

        /// <summary>
        /// 按优先级试投（真实箱子直接交给原版 AttemptAutoLoad）：
        /// 1. 数量预检：跳过"第一条匹配规则数量不够"的候选（咖啡豆不足 5 颗时，
        ///    原版 TryGetMachineOutputRule 会 fallthrough 到无限制规则把它按 1:1 吃掉——必须先拦）。
        /// 2. 把候选临时提到箱子最前，调原版 AttemptAutoLoad(真实箱子)：
        ///    原版按序试投、成功即按规则 RequiredCount 从箱子真实扣减（咖啡豆 5 换 1 正确扣 5），
        ///    失败不扣任何东西。投完恢复箱子顺序（候选没被消耗时才需要还原）。
        /// </summary>
        /// <returns>是否投料成功。</returns>
        public static bool TryFeedMachine(Object machine, IList<Item> input, ConsoleState state, Farmer? who)
        {
            if (machine == null || input == null || input.Count == 0)
                return false;

            Item? candidate = FindCandidate(machine, input, state, who);
            if (candidate == null)
                return false;

            int idx = input.IndexOf(candidate);
            if (idx != 0)
                (input[0], input[idx]) = (input[idx], input[0]);

            // 原版 AttemptAutoLoad 要 IInventory；真实箱子本来就是 Inventory（Chest.Items），
            // 非 IInventory 的 IList 只可能是测试或其他 mod 传入，按原版语义逐个试投兜底。
            bool loaded;
            try
            {
                if (input is IInventory inv)
                    loaded = machine.AttemptAutoLoad(inv, who);
                else
                    loaded = TryFeedByLoop(machine, input, who);
            }
            catch (Exception)
            {
                // 机器数据异常时绝不能拖垮游戏；视为投料失败。
                loaded = false;
            }
            if (!loaded && idx != 0)
                (input[0], input[idx]) = (input[idx], input[0]);
            return loaded;

            // 复刻原版 AttemptAutoLoad(IInventory) 的遍历语义（非 IInventory 兜底）。
            // 注意：autoLoadFrom 是 Object 的静态字段（跨机器共享），绝不能在这里设置——
            // 会污染其他机器的投料状态。真实游戏永远走 IInventory 路径（AttemptAutoLoad
            // 内部自己管理 autoLoadFrom），此兜底只服务测试/极端情况。
            static bool TryFeedByLoop(Object m, IList<Item> items, Farmer? w)
            {
                foreach (Item item in items)
                {
                    if (item == null)
                        continue;
                    if (m.performObjectDropInAction(item, probe: false, w))
                        return true;
                }
                return false;
            }
        }

        /// <summary>按优先级顺序找第一个可投候选（数量预检 + 可接受性检查都通过）。</summary>
        private static Item? FindCandidate(Object machine, IList<Item> input, ConsoleState state, Farmer? who)
        {
            foreach (Item item in OrderedCandidates(machine, input, state))
            {
                if (item == null || item.Stack <= 0)
                    continue;
                if (!CanMachineAccept(machine, item))
                    continue;
                if (!HasEnoughCountForRule(machine, item, who))
                    continue;
                return item;
            }
            return null;
        }

        /// <summary>
        /// 数量预检：找机器规则里"第一条匹配该物品"的规则（含数量不满足的），
        /// 返回物品堆叠是否 ≥ 该规则 RequiredCount。匹配语义对齐原版 CanApplyOutput
        /// （RequiredItemId / RequiredTags / Trigger 类型），跳过 Condition
        /// （机器条件很少用，且 GameStateQuery 在极端环境下可能不可用——这里保守跳过）。
        /// </summary>
        internal static bool HasEnoughCountForRule(Object machine, Item item, Farmer? who)
        {
            MachineData? data;
            try
            {
                data = MachineDataProvider(machine);
            }
            catch (Exception)
            {
                return false; // 拿不到机器数据 → 不投（安全方向）
            }
            if (data?.OutputRules == null)
                return false;

            foreach (MachineOutputRule rule in data.OutputRules)
            {
                if (rule.Triggers == null)
                    continue;
                foreach (MachineOutputTriggerRule trigger in rule.Triggers)
                {
                    if (!trigger.Trigger.HasFlag(MachineOutputTrigger.ItemPlacedInMachine))
                        continue;
                    if (trigger.RequiredItemId != null && !ItemRegistry.HasItemId(item, trigger.RequiredItemId))
                        continue;
                    if (trigger.RequiredTags != null && trigger.RequiredTags.Count > 0)
                    {
                        try
                        {
                            if (!ItemContextTagManager.DoAllTagsMatch(trigger.RequiredTags, item.GetContextTags()))
                                continue;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }
                    // 第一条匹配规则：数量不足 → 不投（攒够再投），不 fallthrough 到下一条规则。
                    return item.Stack >= trigger.RequiredCount;
                }
            }
            return false;
        }

        /// <summary>候选顺序：优先级列表在前，其余按箱内顺序（原版 AttemptAutoLoad 是"遍历第一个能投的"，这里保持同样语义）。</summary>
        private static IEnumerable<Item> OrderedCandidates(Object machine, IList<Item> input, ConsoleState state)
        {
            List<Item>? prioritized = null;
            if (state?.InputPriority != null && state.InputPriority.Count > 0)
            {
                prioritized = new List<Item>();
                foreach (string wantId in state.InputPriority)
                {
                    Item? hit = input.FirstOrDefault(i => i != null && i.QualifiedItemId == wantId);
                    if (hit != null && !prioritized.Contains(hit))
                        prioritized.Add(hit);
                }
            }

            if (prioritized != null)
            {
                foreach (Item item in prioritized)
                    yield return item;
                foreach (Item item in input)
                {
                    if (item != null && !prioritized.Contains(item))
                        yield return item;
                }
            }
            else
            {
                foreach (Item item in input)
                {
                    if (item != null)
                        yield return item;
                }
            }
        }

        /*********
        ** 收成品
        *********/

        /// <summary>把加工完成的产物收进成果箱；箱满则留在机器里。返回是否真的收走了。</summary>
        public static bool TryCollectFromMachine(Object machine, Inventory output)
        {
            if (machine == null || output == null)
                return false;
            Object held = machine.heldObject.Value;
            if (held == null || !machine.readyForHarvest.Value || machine.MinutesUntilReady > 0)
                return false;

            Item leftover = CollectPutDelegate(held, output);
            if (leftover == null)
            {
                machine.heldObject.Value = null;
                machine.readyForHarvest.Value = false;
                machine.showNextIndex.Value = false;
                machine.MinutesUntilReady = 0;
                return true;
            }

            // 只收走了一部分：更新堆叠，但机器保持待收状态。
            if (leftover.Stack < held.Stack)
                held.Stack = leftover.Stack;
            return false;
        }
    }
}
