using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.BigCraftables;
using Object = StardewValley.Object;
using SObject = StardewValley.Object;

namespace ShedConsoleComputer
{
    public class ModEntry : Mod
    {
        public const string ConsoleId = "(BC)Claude.ShedConsoleComputer_Console";
        public const string ConsoleUnqualified = AutomationCore.ConsoleUnqualified;
        public const string RecipeKey = "Claude.ShedConsoleComputer_Console";

        internal static ModEntry? Instance;

        internal ConsoleManager? Manager;
        internal ModConfig? Config;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();
            Manager = new ConsoleManager(helper, Monitor, Config);

            var harmony = new Harmony(ModManifest.UniqueID);
            harmony.PatchAll();

            helper.Events.Content.AssetRequested += OnAssetRequested;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
            helper.Events.World.ObjectListChanged += OnObjectListChanged;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            Manager!.RebuildCache();
            Manager.LoadFromSave();
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            Manager!.SaveToSave();
        }

        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            Manager!.RebuildCache();
        }

        /// <summary>每秒扫描一次：收成品、投原料、刷新亮灯。卡顿优化：菜单打开时跳过。</summary>
        private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
        {
            Manager!.RunAutomationForAll();
        }

        /// <summary>主线程每 tick：处理冻结期内标记的待处理机器（避开 passTimeForObjects 的 Lock）。</summary>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            Manager!.ProcessPendingMachines();
        }

        private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            foreach (var pair in e.Added)
                Manager!.OnObjectAdded(e.Location, pair.Key, pair.Value);
            foreach (var pair in e.Removed)
                Manager!.OnObjectRemoved(e.Location, pair.Key, pair.Value);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            Monitor.Log("小屋主控电脑已就绪。", LogLevel.Info);
            RegisterGmcm();
        }

        private void RegisterGmcm()
        {
            var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gmcm == null)
            {
                Monitor.Log("未检测到 GMCM，跳过配置注册。", LogLevel.Debug);
                return;
            }

            gmcm.Register(
                mod: ModManifest,
                reset: () => Config = new ModConfig(),
                save: () => Helper.WriteConfig(Config!)
            );

            gmcm.AddSectionTitle(ModManifest, () => "小屋主控电脑");
            gmcm.AddParagraph(ModManifest, () => "右键打开电脑：设置投料优先级、查看小屋综述、管理内置箱（投料箱放原料，成果箱收成品）。");

            gmcm.AddBoolOption(
                ModManifest,
                getValue: () => Config!.AutoFeed,
                setValue: v => Config!.AutoFeed = v,
                name: () => "自动投料",
                tooltip: () => "机器空时，按优先级从投料箱自动投放原料。"
            );

            gmcm.AddBoolOption(
                ModManifest,
                getValue: () => Config!.AutoCollect,
                setValue: v => Config!.AutoCollect = v,
                name: () => "自动收成品",
                tooltip: () => "机器加工完成的瞬间立刻收进成果箱，不用等扫描。"
            );

            gmcm.AddBoolOption(
                ModManifest,
                getValue: () => Config!.IncludeCasks,
                setValue: v => Config!.IncludeCasks = v,
                name: () => "连木桶（陈酿）也管",
                tooltip: () => "是否把木桶纳入自动化（酒→陈酿）。"
            );

            gmcm.AddBoolOption(
                ModManifest,
                getValue: () => Config!.IncludePreservesJar,
                setValue: v => Config!.IncludePreservesJar = v,
                name: () => "连罐头瓶也管",
                tooltip: () => "是否把罐头瓶纳入自动化。"
            );
        }

        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (e.Name.IsEquivalentTo("Mods/Claude.ShedConsoleComputer/Console"))
            {
                e.LoadFromModFile<Microsoft.Xna.Framework.Graphics.Texture2D>("assets/console.png", AssetLoadPriority.Medium);
            }
            else if (e.Name.IsEquivalentTo("Data/BigCraftables"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, BigCraftableData>().Data;
                    data[ConsoleUnqualified] = new BigCraftableData
                    {
                        Name = ConsoleUnqualified,
                        DisplayName = Helper.Translation.Get("item.console.name"),
                        Description = Helper.Translation.Get("item.console.description"),
                        Price = 5000,
                        Fragility = 0,
                        CanBePlacedOutdoors = true,
                        CanBePlacedIndoors = true,
                        IsLamp = false,
                        Texture = "Mods/Claude.ShedConsoleComputer/Console",
                        SpriteIndex = 0
                    };
                });
            }
            else if (e.Name.IsEquivalentTo("Data/CraftingRecipes"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    data[RecipeKey] = "337 5 80 10/Home/(BC)Claude.ShedConsoleComputer_Console/true/none/";
                });
            }
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            UnlockRecipeIfEligible();
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null)
                return;
            // 只响应"使用/开门"键（默认鼠标右键、手柄 X），不再拦左键，左键留给稿子破坏。
            if (!e.Button.IsActionButton())
                return;

            Vector2 grabTile = e.Cursor.GrabTile;
            if (Game1.currentLocation.Objects.TryGetValue(grabTile, out SObject obj)
                && obj != null
                && obj.ItemId == ConsoleUnqualified)
            {
                Manager!.OpenConsoleMenu(Game1.currentLocation, grabTile, obj);
                Helper.Input.Suppress(e.Button);
            }
        }

        private void UnlockRecipeIfEligible()
        {
            if (!Context.IsWorldReady)
                return;
            if (Game1.player.craftingRecipes.ContainsKey(RecipeKey))
                return;
            if (Game1.player.farmingLevel.Value < 10)
                return;

            Game1.player.craftingRecipes.Add(RecipeKey, 0);
            Game1.addHUDMessage(new HUDMessage("学会了新配方：小屋主控电脑", 2));
            Monitor.Log("已达耕种10级，发放电脑配方。", LogLevel.Info);
        }
    }
}
