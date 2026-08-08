using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.BigCraftables;
using StardewValley.Objects;
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

        /// <summary>联机消息类型。</summary>
        private const string MsgGuestOp = "sc_guest_op";
        private const string MsgHostState = "sc_host_state";

        private class GuestOpPayload
        {
            public string Loc = "";
            public float TileX;
            public float TileY;
            public string Op = "";       // "put_fruit" / "take_fruit" / "put_wine" / "take_wine" / "set_priority"
            public string ItemId = "";
            public int Stack;
            public int Quality;
            public List<string>? Priority;
        }

        private class HostStatePayload
        {
            public string Loc = "";
            public float TileX;
            public float TileY;
            public string Json = "";
        }

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
            helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
            helper.Events.Player.Warped += OnWarped;
        }

        /// <summary>访客操作转发 / 主机状态回传。</summary>
        private void OnModMessageReceived(object? sender, StardewModdingAPI.Events.ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModManifest.UniqueID) return;
            if (Manager == null || !Context.IsWorldReady) return;
            try
            {
                switch (e.Type)
                {
                    case MsgGuestOp:
                        if (Context.IsMainPlayer)
                            HandleGuestOp(e.ReadAs<GuestOpPayload>(), e.FromPlayerID);
                        break;
                    case MsgHostState:
                        if (!Context.IsMainPlayer)
                            ApplyHostState(e.ReadAs<HostStatePayload>());
                        break;
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[sc_sync] 联机消息处理失败: {ex}", LogLevel.Error);
            }
        }

        /// <summary>主机执行访客操作(访客已扣钱/已拿到物品,主机改内置箱 + 落盘 modData 同步)。</summary>
        private void HandleGuestOp(GuestOpPayload payload, long fromPlayerId)
        {
            var loc = Game1.getLocationFromName(payload.Loc);
            if (loc == null) return;
            var tile = new Vector2(payload.TileX, payload.TileY);
            if (!Manager.HasConsole(loc)) return;

            var input = Manager.GetInputChestObj(loc, tile);
            var output = Manager.GetOutputChestObj(loc, tile);
            switch (payload.Op)
            {
                case "request_state":
                    // 访客打开菜单:把当前状态回给他(不修改任何东西)
                    SendHostState(loc, tile, new[] { fromPlayerId });
                    return;
                case "put_fruit":
                {
                    var item = ItemRegistry.Create(payload.ItemId, payload.Stack);
                    if (item != null)
                    {
                        item.Quality = payload.Quality;
                        input.Items.Add(item);
                    }
                    break;
                }
                case "take_fruit":
                    RemoveStack(input, payload.ItemId, payload.Stack);
                    break;
                case "put_wine":
                {
                    var item = ItemRegistry.Create(payload.ItemId, payload.Stack);
                    if (item != null)
                    {
                        item.Quality = payload.Quality;
                        output.Items.Add(item);
                    }
                    break;
                }
                case "take_wine":
                    RemoveStack(output, payload.ItemId, payload.Stack);
                    break;
                case "set_priority":
                {
                    var state = Manager.GetState(loc, tile);
                    state.InputPriority.Clear();
                    if (payload.Priority != null)
                        state.InputPriority.AddRange(payload.Priority);
                    break;
                }
            }
            // 落盘 modData(NetField 同步给访客 + 随存档保存)
            Manager.PersistToModData(loc, tile);
            // 把最新状态回给操作者
            SendHostState(loc, tile, new[] { fromPlayerId });
            Monitor.Log($"[sc_sync] 主机已执行访客操作 {payload.Op} ({payload.ItemId} x{payload.Stack})", LogLevel.Info);
        }

        /// <summary>从箱子按 ID+数量扣减(先扣先得)。</summary>
        private static void RemoveStack(Chest chest, string id, int stack)
        {
            int remaining = stack;
            for (int i = chest.Items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                if (chest.Items[i] == null || chest.Items[i].QualifiedItemId != id) continue;
                int take = Math.Min(remaining, chest.Items[i].Stack);
                chest.Items[i].Stack -= take;
                remaining -= take;
                if (chest.Items[i].Stack <= 0) chest.Items.RemoveAt(i);
            }
        }

        /// <summary>主机把指定电脑的箱子状态序列化发给指定访客(菜单打开时同步用)。</summary>
        private void SendHostState(GameLocation loc, Vector2 tile, long[]? playerIds = null)
        {
            if (!Context.IsMainPlayer || Manager == null) return;
            var input = Manager.GetInputChestObj(loc, tile);
            var output = Manager.GetOutputChestObj(loc, tile);
            var state = Manager.GetState(loc, tile);
            var snap = new ConsoleManager.SyncSnapshotPublic(input, output, state.InputPriority);
            Helper.Multiplayer.SendMessage(
                new HostStatePayload
                {
                    Loc = loc.NameOrUniqueName,
                    TileX = tile.X,
                    TileY = tile.Y,
                    Json = System.Text.Json.JsonSerializer.Serialize(snap)
                },
                MsgHostState,
                modIDs: new[] { ModManifest.UniqueID },
                playerIDs: playerIds);
        }

        /// <summary>访客应用主机状态到本地缓存(打开菜单时用)。</summary>
        private void ApplyHostState(HostStatePayload payload)
        {
            if (Manager == null) return;
            var loc = Game1.getLocationFromName(payload.Loc);
            if (loc == null) return;
            var tile = new Vector2(payload.TileX, payload.TileY);
            try
            {
                var snap = System.Text.Json.JsonSerializer.Deserialize<ConsoleManager.SyncSnapshotPublic>(payload.Json);
                if (snap == null) return;
                var input = Manager.GetInputChestObj(loc, tile);
                var output = Manager.GetOutputChestObj(loc, tile);
                input.Items.Clear();
                foreach (var s in snap.Fruit) input.Items.Add(Recreate(s));
                output.Items.Clear();
                foreach (var s in snap.Wine) output.Items.Add(Recreate(s));
                var state = Manager.GetState(loc, tile);
                state.InputPriority.Clear();
                if (snap.Priority != null) state.InputPriority.AddRange(snap.Priority);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[sc_sync] 应用主机状态失败: {ex.Message}", LogLevel.Error);
            }
        }

        private static Item Recreate(ConsoleManager.SyncItemPublic s)
        {
            var item = ItemRegistry.Create(s.Id, s.Stack);
            if (item == null) return new StardewValley.Object(s.Id, s.Stack);
            item.Quality = s.Quality;
            return item;
        }

        /// <summary>访客:把箱子操作转发主机。</summary>
        public void ForwardGuestOp(GameLocation loc, Vector2 tile, string op, string itemId, int stack, int quality = 0, List<string>? priority = null)
        {
            if (Context.IsMainPlayer || Manager == null) return;
            Helper.Multiplayer.SendMessage(
                new GuestOpPayload
                {
                    Loc = loc.NameOrUniqueName,
                    TileX = tile.X,
                    TileY = tile.Y,
                    Op = op,
                    ItemId = itemId,
                    Stack = stack,
                    Quality = quality,
                    Priority = priority
                },
                MsgGuestOp,
                modIDs: new[] { ModManifest.UniqueID });
        }

        /// <summary>访客:打开菜单时请求主机状态。</summary>
        public void RequestHostState(GameLocation loc, Vector2 tile)
        {
            if (Context.IsMainPlayer || Manager == null) return;
            Helper.Multiplayer.SendMessage(
                new GuestOpPayload { Loc = loc.NameOrUniqueName, TileX = tile.X, TileY = tile.Y, Op = "request_state" },
                MsgGuestOp,
                modIDs: new[] { ModManifest.UniqueID });
        }

        /// <summary>换地点时:主机把所在地点的所有电脑状态同步给新到的访客。</summary>
        private void OnWarped(object? sender, StardewModdingAPI.Events.WarpedEventArgs e)
        {
            if (!Context.IsMainPlayer || Manager == null) return;
            if (e.NewLocation == null || !Manager.HasConsole(e.NewLocation)) return;
            foreach (var tile in Manager.GetConsoleTiles(e.NewLocation))
                SendHostState(e.NewLocation, tile);
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            // 联机:存档数据在主机(SMAPI ReadSaveData 在客机会抛 "remote host" 异常)。
            // 客机不读不写,内置箱内容走主机权威(见 ConsoleManager 的只读守卫)。
            if (!Context.IsMainPlayer)
            {
                Manager!.RebuildCache();
                return;
            }
            // 读档恢复:只走 modData(SyncKey)一条权威通道(RebuildCache 里 ApplyFromModData 恢复)。
            // ⚠️ 不再调 LoadFromSave —— 它从 shed-console-data(List<Item> 抽象类,Newtonsoft
            // 序列化不可靠,反序列化常返回空/丢)清空并覆盖 modData 刚恢复的内容 = "水果读档后消失"根因。
            Manager!.RebuildCache();
            Monitor.Log("[sc_sync] 读档:内置箱从电脑 modData 恢复(单一权威通道)。", LogLevel.Info);
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            if (!Context.IsMainPlayer) return;   // 联机:只有主机写存档
            // 先确保所有电脑的内置箱内容落盘 modData(随大件序列化进存档,双保险)
            if (Manager != null)
            {
                foreach (var loc in Game1.locations)
                {
                    if (!Manager.HasConsole(loc)) continue;
                    foreach (var tile in Manager.GetConsoleTiles(loc))
                        Manager.PersistToModData(loc, tile);
                }
            }
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
