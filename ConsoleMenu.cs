using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ShedConsoleComputer
{
    /// <summary>
    /// 电脑主界面：优先级设置 / 小屋综述 / 内置箱。
    /// 内置箱页直接内嵌一个原版 ItemGrabMenu（玩家背包 + 箱子双向存取），
    /// 替换了旧版"只能拿不能放"的打开方式——那就是"水果放不进"的根源。
    /// </summary>
    public class ConsoleMenu : IClickableMenu
    {
        /// <summary>界面分页。</summary>
        public const int PageMain = 0;
        public const int PagePriority = 1;
        public const int PageSummary = 2;

        private class PriorityIcon
        {
            public string Qid = "";
            public Item? Item;
            public Rectangle Bounds;
        }

        private readonly IModHelper Helper;
        private readonly ConsoleManager Manager;
        private readonly ConsoleState State;

        internal readonly GameLocation Location;
        internal readonly Vector2 Tile;

        internal int Page { get; private set; } = PageMain;

        private readonly List<ClickableComponent> MenuButtons = new();
        private readonly List<PriorityIcon> Candidates = new();
        private readonly List<PriorityIcon> Selected = new();

        public ConsoleMenu(IModHelper helper, ConsoleManager manager, GameLocation loc, Vector2 tile, ConsoleState state)
            : base(
                Game1.uiViewport.Width / 2 - (800 + IClickableMenu.borderWidth * 2) / 2,
                Game1.uiViewport.Height / 2 - (700 + IClickableMenu.borderWidth * 2) / 2,
                800 + IClickableMenu.borderWidth * 2,
                700 + IClickableMenu.borderWidth * 2,
                showUpperRightCloseButton: true)
        {
            Helper = helper;
            Manager = manager;
            Location = loc;
            Tile = tile;
            State = state;
            BuildMainButtons();

            // 联机：访客打开菜单时向主机请求最新内置箱状态（异步，稍后 ApplyHostState 刷新）
            if (!Context.IsMainPlayer)
                ModEntry.Instance?.RequestHostState(loc, tile);
        }

        private void BuildMainButtons()
        {
            MenuButtons.Clear();
            int cx = xPositionOnScreen + width / 2;
            int top = yPositionOnScreen + 180;
            for (int i = 0; i < 5; i++)
            {
                MenuButtons.Add(new ClickableComponent(new Rectangle(cx - 320, top + i * 82, 640, 68), i.ToString())
                {
                    myID = 100 + i
                });
            }
        }

        /*********
        ** 输入
        *********/

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            switch (Page)
            {
                case PageMain:
                    foreach (ClickableComponent button in MenuButtons)
                    {
                        if (!button.containsPoint(x, y))
                            continue;
                        Game1.playSound("smallSelect");
                        switch (button.name)
                        {
                            case "0":
                                Page = PagePriority;
                                BuildPriorityIcons();
                                break;
                            case "1":
                                Page = PageSummary;
                                break;
                            case "2":
                                OpenChestPage(isFruit: true);
                                break;
                            case "3":
                                OpenChestPage(isFruit: false);
                                break;
                            case "4":
                                exitThisMenu();
                                break;
                        }
                        return;
                    }
                    return;

                case PagePriority:
                    foreach (PriorityIcon icon in Candidates)
                    {
                        if (icon.Bounds.Contains(x, y))
                        {
                            Game1.playSound("smallSelect");
                            if (!State.InputPriority.Contains(icon.Qid))
                                State.InputPriority.Add(icon.Qid);
                            BuildPriorityIcons();
                            ForwardPriority();
                            return;
                        }
                    }
                    foreach (PriorityIcon icon in Selected)
                    {
                        if (icon.Bounds.Contains(x, y))
                        {
                            Game1.playSound("bigDeSelect");
                            State.InputPriority.Remove(icon.Qid);
                            BuildPriorityIcons();
                            ForwardPriority();
                            return;
                        }
                    }
                    if (BackButtonRect().Contains(x, y))
                    {
                        Game1.playSound("bigDeSelect");
                        Page = PageMain;
                    }
                    return;

                case PageSummary:
                    if (BackButtonRect().Contains(x, y))
                    {
                        Game1.playSound("bigDeSelect");
                        Page = PageMain;
                    }
                    return;
            }
        }

        /*********
        ** 内置箱页
        *********/

        /// <summary>打开内置箱：直接用游戏原生 ItemGrabMenu 替换当前界面，和开普通木箱完全一致。
        /// 不再嵌套子菜单——嵌套会导致空箱时鼠标消失、绘制错乱。
        /// 关键：ItemGrabMenu 的存取回调必须用自定义闭包操作 36 格数组。
        /// 原版 Chest.grabItemFromInventory/grabItemFromChest 内部会调 ShowMenu() 重建菜单，
        /// 导致数组和 exitFunction 失效（放东西后关箱子，物品凭空消失/界面错乱）。
        /// 联机：访客的放入/取走操作通过 ModEntry 转发主机（主机改权威箱 + 落盘 modData 同步）。</summary>
        private void OpenChestPage(bool isFruit)
        {
            // 联机:访客打开箱子页前再请求一次主机状态(确保展开的 36 格是最新的,
            // 主机状态晚到的话,展开的数组就是旧内容)
            if (!Context.IsMainPlayer)
                ModEntry.Instance?.RequestHostState(Location, Tile);

            Chest chest = isFruit ? Manager.GetInputChestObj(Location, Tile) : Manager.GetOutputChestObj(Location, Tile);

            // 展开成固定 36 格数组：空箱也显示完整 36 格；关箱子时压缩回紧凑列表。
            List<Item> fixedItems = ChestUiCore.ExpandToFixedSlots(chest.Items);

            // 自定义回调：操作 fixedItems 数组，绝不触发原版 ShowMenu() 重建。
            // 关键：放完必须清空/更新手上引用（heldItem）——否则同一个物品实例会同时
            // 出现在箱格和手上，下次点击自我堆叠翻倍（5→10→20→40）。
            // 原版 Chest.grabItemFromInventory 也是这么做的（heldItem = 剩余）。
            ItemGrabMenu? grabMenu = null;
            void GrabFromChest(Item item, Farmer who)
            {
                int idx = fixedItems.IndexOf(item);
                if (idx >= 0)
                {
                    int stack = item.Stack;
                    fixedItems[idx] = null;
                    // 联机：访客取走 → 转发主机扣减
                    if (!Context.IsMainPlayer)
                        ModEntry.Instance?.ForwardGuestOp(Location, Tile, isFruit ? "take_fruit" : "take_wine", item.QualifiedItemId, stack);
                }
            }
            // 放东西：先进数组；放不下的部分留在手上（ItemGrabMenu 行为与普通箱子一致）。
            // 注意：不能调 who.removeItemFromInventory——原版 InventoryMenu.leftClick 在
            // 调回调之前已经把物品从背包移除，再调会重复扣除。
            void PutIntoChest(Item item, Farmer who)
            {
                Item? leftover = ChestUiCore.PutIntoFixedSlots(fixedItems, item);
                if (grabMenu != null)
                    grabMenu.heldItem = leftover;
                // 联机：访客放入 → 转发主机（主机入箱 + 落盘同步）
                if (!Context.IsMainPlayer && leftover == null && item != null)
                    ModEntry.Instance?.ForwardGuestOp(Location, Tile, isFruit ? "put_fruit" : "put_wine", item.QualifiedItemId, item.Stack, item is StardewValley.Object o ? o.Quality : 0);
            }

            grabMenu = new ItemGrabMenu(
                inventory: fixedItems,
                reverseGrab: false,
                showReceivingMenu: true,
                highlightFunction: InventoryMenu.highlightAllItems,
                behaviorOnItemSelectFunction: PutIntoChest,
                message: null,
                behaviorOnItemGrab: GrabFromChest,
                snapToBottom: false,
                canBeExitedWithKey: true,
                playRightClickSound: true,
                allowRightClick: true,
                showOrganizeButton: true,
                source: 1,
                sourceItem: chest,
                whichSpecialButton: -1,
                context: chest);

            // 关闭时：把 36 格数组压缩回紧凑列表存回 Chest，然后回到主界面。
            grabMenu.exitFunction = (IClickableMenu.onExit)Delegate.Combine(
                grabMenu.exitFunction,
                (IClickableMenu.onExit)delegate
                {
                    ChestUiCore.CompressBack(fixedItems, chest.Items);
                    Game1.activeClickableMenu = new ConsoleMenu(Helper, Manager, Location, Tile, State);
                });

            Game1.activeClickableMenu = grabMenu;
        }

        /*********
        ** 优先级页
        *********/

        private void BuildPriorityIcons()
        {
            Candidates.Clear();
            Selected.Clear();

            List<string> seen = new();
            foreach (Item item in Manager.GetInputChestObj(Location, Tile).Items)
            {
                if (item != null && !seen.Contains(item.QualifiedItemId))
                    seen.Add(item.QualifiedItemId);
            }

            int startX = xPositionOnScreen + 100;
            int x = startX;
            int y = yPositionOnScreen + 220;
            foreach (string qid in seen)
            {
                Candidates.Add(new PriorityIcon
                {
                    Qid = qid,
                    Item = MakeItem(qid),
                    Bounds = new Rectangle(x, y, 64, 64)
                });
                x += 76;
                if (x > xPositionOnScreen + width - 120)
                {
                    x = startX;
                    y += 76;
                }
            }

            int sx = xPositionOnScreen + 100;
            int sy = yPositionOnScreen + 520;
            foreach (string qid in State.InputPriority)
            {
                Selected.Add(new PriorityIcon
                {
                    Qid = qid,
                    Item = MakeItem(qid),
                    Bounds = new Rectangle(sx, sy, 64, 64)
                });
                sx += 76;
            }
        }

        private static Item? MakeItem(string qid)
        {
            try
            {
                return ItemRegistry.Create(qid);
            }
            catch
            {
                return null;
            }
        }

        private Rectangle BackButtonRect() => new(xPositionOnScreen + 40, yPositionOnScreen + height - 110, 220, 64);

        /// <summary>联机：访客改优先级 → 转发主机（主机落盘同步）。</summary>
        private void ForwardPriority()
        {
            if (!Context.IsMainPlayer)
                ModEntry.Instance?.ForwardGuestOp(Location, Tile, "set_priority", "", 0, 0, new List<string>(State.InputPriority));
        }

        /*********
        ** 绘制
        *********/

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.75f);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            switch (Page)
            {
                case PageMain:
                    DrawMain(b);
                    break;
                case PagePriority:
                    DrawPriority(b);
                    break;
                case PageSummary:
                    DrawSummary(b);
                    break;
            }

            drawMouse(b);
        }

        private void DrawMain(SpriteBatch b)
        {
            DrawTitle(b, "小屋主控电脑");
            // 用居中绘制代替"起点+固定宽度"，保证长短文字都不会溢出按钮框。
            string[] labels = { "投料优先级设置", "小屋综述", "投料箱（放水果/原料）", "成果箱（收好的成品）", "关闭" };
            for (int i = 0; i < MenuButtons.Count; i++)
            {
                ClickableComponent button = MenuButtons[i];
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), button.bounds.X, button.bounds.Y, button.bounds.Width, button.bounds.Height, Color.White, 4f, true);
                SpriteText.drawStringHorizontallyCenteredAt(b, labels[i], button.bounds.X + button.bounds.Width / 2, button.bounds.Y + 18);
            }
        }

        private void DrawPriority(SpriteBatch b)
        {
            DrawTitle(b, "投料优先级（点上方图标加入，下方为顺序）");
            if (Candidates.Count == 0)
            {
                SpriteText.drawString(b, "投料箱是空的", xPositionOnScreen + 100, yPositionOnScreen + 230, 999999, -1, 999999, 1f, 0.88f);
                SpriteText.drawString(b, "先回主界面打开「投料箱」放入原料", xPositionOnScreen + 100, yPositionOnScreen + 300, 999999, -1, 999999, 1f, 0.88f);
            }
            else
            {
                SpriteText.drawString(b, "可选（来自投料箱）：", xPositionOnScreen + 100, yPositionOnScreen + 180, 999999, -1, 999999, 1f, 0.88f);
                foreach (PriorityIcon icon in Candidates)
                    DrawIcon(b, icon, State.InputPriority.Contains(icon.Qid));
            }

            SpriteText.drawString(b, "优先级顺序（点可移除）：", xPositionOnScreen + 100, yPositionOnScreen + 470, 999999, -1, 999999, 1f, 0.88f);
            if (Selected.Count == 0)
            {
                SpriteText.drawString(b, "（未设置 → 按箱内顺序投）", xPositionOnScreen + 100, yPositionOnScreen + 590, 999999, -1, 999999, 1f, 0.88f);
            }
            else
            {
                int num = 1;
                foreach (PriorityIcon icon in Selected)
                {
                    DrawIcon(b, icon, alreadyChosen: false);
                    SpriteText.drawString(b, num.ToString(), icon.Bounds.X - 4, icon.Bounds.Y - 6, 999999, -1, 999999, 1f, 0.88f);
                    num++;
                }
            }
            DrawBack(b, "返回");
        }

        private void DrawIcon(SpriteBatch b, PriorityIcon icon, bool alreadyChosen)
        {
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), icon.Bounds.X, icon.Bounds.Y, icon.Bounds.Width, icon.Bounds.Height, alreadyChosen ? Color.Gray * 0.5f : Color.White, 4f, true);
            icon.Item?.drawInMenu(b, new Vector2(icon.Bounds.X + 8, icon.Bounds.Y + 8), 1f, alreadyChosen ? 0.5f : 1f, 1f);
        }

        private void DrawSummary(SpriteBatch b)
        {
            DrawTitle(b, "小屋综述");
            ConsoleManager.Summary summary = Manager.BuildSummary(Location, Tile, State);
            int x = xPositionOnScreen + 110;
            int y = yPositionOnScreen + 210;
            const int lineHeight = 62;
            SpriteText.drawString(b, $"小桶总数: {summary.TotalKegs}", x, y, 999999, -1, 999999, 1f, 0.88f);
            y += lineHeight;
            SpriteText.drawString(b, $"正在酿造: {summary.Running}   可收取: {summary.Ready}   空闲: {summary.Idle}", x, y, 999999, -1, 999999, 1f, 0.88f);
            y += lineHeight;
            SpriteText.drawString(b, $"进料箱存量: {summary.InputCount}   成果箱存量: {summary.OutputCount}", x, y, 999999, -1, 999999, 1f, 0.88f);
            y += lineHeight;
            DrawBack(b, "返回");
        }

        private void DrawTitle(SpriteBatch b, string title)
        {
            // 中文 SpriteText 宽度计算有 bug（不乘 FontPixelZoom），自己算居中。
            int titleWidth = SpriteText.getWidthOfString(title);
            // 标题画在框内顶部留白区域，不再顶到框上边缘。
            SpriteText.drawString(b, title, xPositionOnScreen + width / 2 - titleWidth / 2, yPositionOnScreen + 96, 999999, -1, 999999, 1f, 0.88f);
        }

        private void DrawBack(SpriteBatch b, string label)
        {
            Rectangle rect = BackButtonRect();
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), rect.X, rect.Y, rect.Width, rect.Height, Color.White, 4f, true);
            SpriteText.drawString(b, label, rect.X + 60, rect.Y + 14, 999999, -1, 999999, 1f, 0.88f);
        }
    }
}
