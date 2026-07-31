using System;
using System.Collections.Generic;
using StardewValley;
using StardewValley.Inventories;
using Object = StardewValley.Object;

namespace ShedConsoleComputer
{
    /// <summary>
    /// 内置箱 UI 的数据桥（纯逻辑，可本地测试）：
    /// ItemGrabMenu 需要固定 36 格的数组；Chest.Items 是"紧凑列表"（只含有物品的格子）。
    /// 打开时展开成 36 格，关闭时压缩回紧凑列表。
    /// 注意：原版 Chest.grabItemFromInventory / grabItemFromChest 内部会调 ShowMenu()
    /// 重建 ItemGrabMenu，mod 的 36 格数组和 exitFunction 会全部失效——所以箱子 UI
    /// 必须用自定义闭包回调操作数组，绝不走原版 Chest 回调。
    /// </summary>
    public static class ChestUiCore
    {
        public const int SlotCount = 36;

        /// <summary>展开：把紧凑列表复制到固定 36 格数组（空箱也显示完整 36 格）。</summary>
        public static List<Item> ExpandToFixedSlots(IList<Item> chestItems)
        {
            var fixedItems = new List<Item>(new Item[SlotCount]);
            if (chestItems == null)
                return fixedItems;
            for (int i = 0; i < chestItems.Count && i < SlotCount; i++)
            {
                if (chestItems[i] != null)
                    fixedItems[i] = chestItems[i];
            }
            return fixedItems;
        }

        /// <summary>压缩：把 36 格数组写回紧凑列表（跳过空格）。</summary>
        public static void CompressBack(List<Item> fixedItems, IList<Item> chestItems)
        {
            if (fixedItems == null)
                return;
            chestItems.Clear();
            foreach (Item? item in fixedItems)
            {
                if (item != null)
                    chestItems.Add(item);
            }
        }

        /// <summary>玩家从箱子拿走一格（行为OnItemGrab 的自定义实现，操作数组、不重建菜单）。</summary>
        public static Item? GrabFromChest(List<Item> fixedItems, int index)
        {
            if (fixedItems == null || index < 0 || index >= fixedItems.Count)
                return null;
            Item? item = fixedItems[index];
            fixedItems[index] = null;
            return item;
        }

        /// <summary>玩家放东西进箱子：先尝试堆叠到已有同类物品，否则放进第一个空位。返回放不下的剩余（null = 全放进去了）。</summary>
        public static Item? PutIntoFixedSlots(List<Item> fixedItems, Item item)
        {
            if (fixedItems == null || item == null)
                return item;

            // 先堆叠到同类
            foreach (Item? slot in fixedItems)
            {
                if (slot == null || slot.canStackWith(item))
                {
                    int leftover = slot == null ? 0 : slot.addToStack(item);
                    if (slot == null && leftover == 0)
                    {
                        // 空位直接放
                        int idx = fixedItems.IndexOf(null);
                        if (idx >= 0)
                            fixedItems[idx] = item;
                        return null;
                    }
                    if (slot != null && leftover == 0)
                        return null;
                    if (slot != null)
                        item.Stack = leftover;
                    break;
                }
            }
            // 剩余放不进空位就返回
            int empty = fixedItems.IndexOf(null);
            if (empty >= 0)
            {
                fixedItems[empty] = item;
                return null;
            }
            return item;
        }

        /// <summary>统计：数组里还有几个物品（调试/测试用）。</summary>
        public static int CountItems(List<Item> fixedItems)
        {
            int count = 0;
            if (fixedItems == null)
                return 0;
            foreach (Item? item in fixedItems)
            {
                if (item != null)
                    count++;
            }
            return count;
        }
    }
}
