using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Menus;
using StardewValley.Objects;
using Object = StardewValley.Object;

namespace ShedConsoleComputer
{
    /// <summary>
    /// 小屋主控电脑的核心逻辑：
    /// 每台电脑对应两个内置箱（投料箱 / 成果箱），周期性扫描所在地点里受支持的机器，
    /// 自动收成品、自动投下一份原料。
    /// </summary>
    public class ConsoleManager
    {
        /// <summary>界面"小屋综述"用的统计数据。</summary>
        public class Summary
        {
            public int TotalKegs;
            public int Running;
            public int Ready;
            public int Idle;
            public int InputCount;
            public int OutputCount;
        }

        /// <summary>玩家一次性拿走整个机器产物堆叠时，视作"手动收酒"，这台机器暂停自动投料的秒数。
        /// 否则玩家左键收酒的同一秒里 Mod 又把新水果塞进去，左键就变成"放下水果"，看起来要点很多次才能收酒。</summary>
        internal const double ManualCollectBackoffSeconds = 30;

        private readonly IModHelper Helper;
        private readonly IMonitor Monitor;
        private readonly ModConfig Config;

        private readonly Dictionary<string, ConsoleState> States = new();
        private readonly Dictionary<GameLocation, List<Vector2>> ConsoleCache = new();
        private readonly Dictionary<string, (Chest fruit, Chest wine)> InternalChests = new();

        /// <summary>手动收酒冷却：机器 key → 冷却结束的游戏内时间戳（秒）。</summary>
        private readonly Dictionary<string, double> ManualCollectBackoffUntil = new();

        /// <summary>本次秒级扫描是否有机器进入了待收状态（用于驱动红点亮灯）。</summary>
        private bool sawReadyThisTick;

        /// <summary>联机:访客操作转发主机(ModEntry 接线);主机侧应用访客操作。</summary>
        public Action<string, string, string, int>? GuestForwardHook;
        /// <summary>联机:访客收到主机状态后的应用(刷新内置箱缓存)。</summary>
        public Action<string>? ApplyHook;

        public ConsoleManager(IModHelper helper, IMonitor monitor, ModConfig config)
        {
            Helper = helper;
            Monitor = monitor;
            Config = config;
        }

        /*********
        ** 状态 key
        *********/

        private static string StateKey(GameLocation loc, Vector2 tile) => $"{loc.NameOrUniqueName}|{(int)tile.X},{(int)tile.Y}";

        private static string ChestKey(GameLocation loc, Vector2 tile) => StateKey(loc, tile);

        private static string MachineKey(GameLocation loc, Object machine) => StateKey(loc, machine.TileLocation);

        public ConsoleState GetState(GameLocation loc, Vector2 tile)
        {
            string key = StateKey(loc, tile);
            if (!States.TryGetValue(key, out ConsoleState? state))
            {
                state = new ConsoleState();
                States[key] = state;
            }
            return state;
        }

        /*********
        ** 电脑位置缓存
        *********/

        public void RebuildCache()
        {
            ConsoleCache.Clear();

            // 旧版本会把宿主箱落在地上（名字以 ShedConsole_ 开头），这里清掉并把东西还给玩家。
            Utility.ForEachLocation(loc =>
            {
                List<Vector2> orphans = new();
                foreach (KeyValuePair<Vector2, Object> pair in loc.Objects.Pairs)
                {
                    if (pair.Value is Chest chest && chest.Name != null && chest.Name.StartsWith("ShedConsole_"))
                    {
                        foreach (Item item in chest.Items.ToList())
                        {
                            if (item != null)
                                Game1.player.addItemToInventoryBool(item, false);
                        }
                        orphans.Add(pair.Key);
                    }
                }
                foreach (Vector2 tile in orphans)
                {
                    loc.Objects.Remove(tile);
                    Monitor.Log($"已清理地上的旧宿主箱 @{loc.Name} {tile}，东西已还给你。", LogLevel.Info);
                }

                foreach (KeyValuePair<Vector2, Object> pair in loc.Objects.Pairs)
                {
                    if (IsConsole(pair.Value))
                    {
                        AddToCache(loc, pair.Key);
                        // 读档恢复:从电脑大件 modData 读回内置箱内容(modData 随存档持久化,
                        // 是内置箱的权威存储)。旧版 "{}" 空快照 → 防御性当空列表处理。
                        ApplyFromModData(loc, pair.Key);
                    }
                }
                return true;
            });
        }

        public void OnObjectAdded(GameLocation loc, Vector2 tile, Object obj)
        {
            if (IsConsole(obj))
                AddToCache(loc, tile);
        }

        public void OnObjectRemoved(GameLocation loc, Vector2 tile, Object obj)
        {
            if (IsConsole(obj) && ConsoleCache.TryGetValue(loc, out List<Vector2>? tiles))
            {
                tiles.Remove(tile);
                if (tiles.Count == 0)
                    ConsoleCache.Remove(loc);
            }
        }

        private void AddToCache(GameLocation loc, Vector2 tile)
        {
            if (!ConsoleCache.TryGetValue(loc, out List<Vector2>? tiles))
            {
                tiles = new List<Vector2>();
                ConsoleCache[loc] = tiles;
            }
            if (!tiles.Contains(tile))
                tiles.Add(tile);
        }

        private static bool IsConsole(Object obj)
        {
            return AutomationCore.IsConsole(obj);
        }

        public bool HasConsole(GameLocation loc) => ConsoleCache.ContainsKey(loc);

        /*********
        ** 内置箱
        *********/

        public Chest GetInputChestObj(GameLocation loc, Vector2 tile) => GetInternal(loc, tile).fruit;

        public Chest GetOutputChestObj(GameLocation loc, Vector2 tile) => GetInternal(loc, tile).wine;

        private (Chest fruit, Chest wine) GetInternal(GameLocation loc, Vector2 tile)
        {
            string key = ChestKey(loc, tile);
            if (!InternalChests.TryGetValue(key, out (Chest, Chest) chests))
            {
                chests = (new Chest { Name = "ShedConsole_fruit" }, new Chest { Name = "ShedConsole_wine" });
                InternalChests[key] = chests;
            }
            return chests;
        }

        /*********
        ** 联机:内置箱同步(2026-08-03 新增)
        ** 问题:内置箱是每端独立内存。访客放杨桃 → 只在访客内存 → 主机保存时空箱 → 第二天消失。
        ** 方案:主机为权威,把两个箱的内容 JSON 序列化进电脑大件 Object.modData
        ** (Item.modData 是 NetField,反编译 Item.cs:354 确认 —— 主机写入自动同步到所有访客,
        ** 且随存档序列化)。访客操作箱子 → 转发主机 → 主机改内存箱 + 写 modData → 同步回来。
        *********/

        /// <summary>modData key(存两个箱子的 JSON)。</summary>
        private const string SyncKey = "xiepe.ShedConsole.Sync";

        /// <summary>箱子快照的 JSON 序列化配置。
        /// ⚠️ 关键:System.Text.Json 默认只序列化属性不序列化字段 —— SyncSnapshot 若用字段,
        /// 序列化输出永远是 "{}",存档后水果全丢(实测根因!)。必须 IncludeFields 或改属性。</summary>
        private static readonly System.Text.Json.JsonSerializerOptions SnapJson = new()
        {
            IncludeFields = true
        };

        /// <summary>物品的轻量序列化(只够还原:ID/堆叠/星级/名称)。</summary>
        private class SyncItem
        {
            public string Id = "";      // QualifiedItemId
            public int Stack;
            public int Quality;
            public string Name = "";
        }

        /// <summary>箱子快照。</summary>
        private class SyncSnapshot
        {
            public List<SyncItem> Fruit = new();
            public List<SyncItem> Wine = new();
            public List<string> Priority = new();
        }

        /// <summary>主机:把当前内置箱内容写进电脑大件 modData(NetField 同步访客 + 随存档保存)。
        /// 每次箱子内容变化后调用。找不到电脑大件则跳过(无副作用)。</summary>
        public void PersistToModData(GameLocation loc, Vector2 tile)
        {
            if (!Context.IsMainPlayer || loc == null) return;
            if (!loc.objects.TryGetValue(tile, out Object? consoleObj) || !IsConsole(consoleObj))
                return;
            var chests = GetInternal(loc, tile);
            var snap = new SyncSnapshot
            {
                Fruit = chests.fruit.Items.Where(i => i != null).Select(ToSyncItem).ToList(),
                Wine = chests.wine.Items.Where(i => i != null).Select(ToSyncItem).ToList(),
            };
            if (States.TryGetValue(StateKey(loc, tile), out var state))
                snap.Priority = state.InputPriority;
            consoleObj.modData[SyncKey] = System.Text.Json.JsonSerializer.Serialize(snap, SnapJson);
        }

        /// <summary>从电脑大件 modData 读回箱子内容(访客侧应用主机同步来的状态)。
        /// modData 里没有(未初始化/新电脑)→ 保持当前内存箱(空)。</summary>
        public void ApplyFromModData(GameLocation loc, Vector2 tile)
        {
            if (loc == null || !loc.objects.TryGetValue(tile, out Object? consoleObj) || !IsConsole(consoleObj))
                return;
            if (!consoleObj.modData.TryGetValue(SyncKey, out var json))
                return;
            SyncSnapshot? snap;
            try { snap = System.Text.Json.JsonSerializer.Deserialize<SyncSnapshot>(json, SnapJson); }
            catch { return; }
            if (snap == null) return;

            var chests = GetInternal(loc, tile);
            // 防御:JSON 里字段为 null(旧版本 "{}" 存档)→ 当空列表处理,绝不抛异常/丢已加载内容
            chests.fruit.Items.Clear();
            if (snap.Fruit != null)
            {
                foreach (var s in snap.Fruit)
                {
                    var item = ItemRegistry.Create(s.Id, s.Stack);
                    if (item == null) continue;
                    item.Quality = s.Quality;
                    if (item.Name != s.Name && !string.IsNullOrEmpty(s.Name)) item.Name = s.Name;
                    chests.fruit.Items.Add(item);
                }
            }
            chests.wine.Items.Clear();
            if (snap.Wine != null)
            {
                foreach (var s in snap.Wine)
                {
                    var item = ItemRegistry.Create(s.Id, s.Stack);
                    if (item == null) continue;
                    item.Quality = s.Quality;
                    if (item.Name != s.Name && !string.IsNullOrEmpty(s.Name)) item.Name = s.Name;
                    chests.wine.Items.Add(item);
                }
            }
            var state = GetState(loc, tile);
            state.InputPriority.Clear();
            if (snap.Priority != null)
                state.InputPriority.AddRange(snap.Priority);
        }

        private static SyncItem ToSyncItem(Item i)
        {
            return new SyncItem
            {
                Id = i.QualifiedItemId,
                Stack = i.Stack,
                Quality = i is Object o ? o.Quality : 0,
                Name = i.Name ?? ""
            };
        }

        /// <summary>给 ModEntry 联机层用的公共快照(JSON 可序列化)。</summary>
        public class SyncItemPublic
        {
            public string Id = "";
            public int Stack;
            public int Quality;
        }

        public class SyncSnapshotPublic
        {
            public List<SyncItemPublic> Fruit = new();
            public List<SyncItemPublic> Wine = new();
            public List<string> Priority = new();

            public SyncSnapshotPublic() { }
            public SyncSnapshotPublic(Chest fruit, Chest wine, List<string> priority)
            {
                Fruit = fruit.Items.Where(i => i != null).Select(i => new SyncItemPublic
                {
                    Id = i.QualifiedItemId, Stack = i.Stack, Quality = i is Object o ? o.Quality : 0
                }).ToList();
                Wine = wine.Items.Where(i => i != null).Select(i => new SyncItemPublic
                {
                    Id = i.QualifiedItemId, Stack = i.Stack, Quality = i is Object o ? o.Quality : 0
                }).ToList();
                Priority = priority;
            }
        }

        /// <summary>某地点所有电脑的格位(联机同步用)。</summary>
        public List<Vector2> GetConsoleTiles(GameLocation loc)
        {
            return ConsoleCache.TryGetValue(loc, out var tiles) ? tiles : new List<Vector2>();
        }

        /// <summary>投料箱实际用的物品表。现在箱子是原生 ItemGrabMenu 直接打开，关闭时立即写回 Chest，所以直接读 Chest 即可。</summary>
        private IList<Item> GetEffectiveInputItems(GameLocation loc, Vector2 tile)
        {
            return GetInputChestObj(loc, tile).Items;
        }

        /*********
        ** 存档读写
        *********/

        public void LoadFromSave()
        {
            InternalChests.Clear();
            States.Clear();
            ManualCollectBackoffUntil.Clear();

            // 联机:客机不读存档数据(在主机)。客机的内置箱为空,内容由主机权威。
            if (!Context.IsMainPlayer)
                return;

            SaveData data = Helper.Data.ReadSaveData<SaveData>("shed-console-data") ?? new SaveData();
            foreach (KeyValuePair<string, ConsoleSave> pair in data.Consoles)
            {
                Chest fruit = new() { Name = "ShedConsole_fruit" };
                foreach (Item item in pair.Value.FruitItems)
                {
                    if (item != null)
                        fruit.Items.Add(item);
                }

                Chest wine = new() { Name = "ShedConsole_wine" };
                foreach (Item item in pair.Value.WineItems)
                {
                    if (item != null)
                        wine.Items.Add(item);
                }

                InternalChests[pair.Key] = (fruit, wine);
                States[pair.Key] = new ConsoleState
                {
                    InputPriority = pair.Value.InputPriority ?? new List<string>()
                };
            }
            Monitor.Log($"已从存档读出 {data.Consoles.Count} 台电脑的数据。", LogLevel.Trace);
        }

        public void SaveToSave()
        {
            // 联机:只有主机写存档数据(客机 WriteSaveData 抛 "remote host" 异常)
            if (!Context.IsMainPlayer)
                return;
            SaveData data = new();
            foreach (KeyValuePair<string, (Chest, Chest)> pair in InternalChests)
            {
                ConsoleSave save = new();
                foreach (Item item in pair.Value.Item1.Items)
                {
                    if (item != null)
                        save.FruitItems.Add(item);
                }
                foreach (Item item in pair.Value.Item2.Items)
                {
                    if (item != null)
                        save.WineItems.Add(item);
                }
                if (States.TryGetValue(pair.Key, out ConsoleState? state))
                    save.InputPriority = state.InputPriority;
                data.Consoles[pair.Key] = save;
            }
            Helper.Data.WriteSaveData("shed-console-data", data);
            Monitor.Log($"已把 {data.Consoles.Count} 台电脑的数据写进存档。", LogLevel.Trace);
        }

        /*********
        ** 自动化
        *********/

        /// <summary>酒一好立刻收：每次机器状态变化（minutesElapsed）都会调这里，
        /// 命中一台就只处理这一台，然后整栋小屋的机器排队到下一秒扫描——既即时又不卡。</summary>
        /// <summary>机器状态变化时的待处理队列（冻结期内只标记，主线程 UpdateTicked 统一处理）。</summary>
        private readonly List<(GameLocation Loc, Object Machine)> PendingMachines = new();

        /// <summary>冻结期（passTimeForObjects 的 objects.Lock()）内只标记机器待处理，不做实际收/投。</summary>
        public void QueueMachine(GameLocation loc, Object machine)
        {
            PendingMachines.Add((loc, machine));
        }

        /// <summary>主线程（冻结期外）处理队列里的机器：有成品收、有空位投。
        /// <b>功能优先</b>：每 tick 处理 25 台，100 台同时 ready 也只需 4 tick（≈67ms）收完，
        /// 用户察觉不到延迟；同时单帧开销封顶，不会叠加成卡顿尖峰。
        /// 冻结期标记 + 主线程处理 = 既即时又不踩 objects.Lock()。
        /// 菜单打开时不跳过——否则玩家开着背包时酒好了永远不收，队列无限积压。</summary>
        public void ProcessPendingMachines()
        {
            if (PendingMachines.Count == 0)
                return;
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
                return;

            int limit = Math.Min(PendingMachines.Count, 25);
            for (int i = 0; i < limit; i++)
            {
                (GameLocation loc, Object machine) = PendingMachines[i];
                ProcessMachineNow(loc, machine);
            }
            PendingMachines.RemoveRange(0, limit);
        }

        public void ProcessMachineNow(GameLocation loc, Object machine)
        {
            if (!Context.IsMainPlayer || loc == null || machine == null)
                return;
            if (!ConsoleCache.TryGetValue(loc, out List<Vector2>? consoles) || consoles.Count == 0)
                return;
            if (!IsMachine(machine) || machine is Chest)
                return;

            Vector2 tile = consoles[0];
            ConsoleState state = GetState(loc, tile);
            IList<Item> input = GetEffectiveInputItems(loc, tile);
            Inventory output = GetOutputChestObj(loc, tile).Items;

            if (Config.AutoCollect
                && machine.readyForHarvest.Value
                && machine.heldObject.Value != null
                && machine.MinutesUntilReady <= 0)
            {
                if (AutomationCore.TryCollectFromMachine(machine, output))
                    PersistToModData(loc, tile);   // 联机:主机自动化改了成果箱 → 落盘 modData 同步访客
            }

            if (Config.AutoFeed
                && machine.heldObject.Value == null
                && !machine.readyForHarvest.Value
                && !IsInManualCollectBackoff(loc, machine))
            {
                if (AutomationCore.TryFeedMachine(machine, input, state, Game1.player))
                    PersistToModData(loc, tile);   // 联机:主机自动化投了料 → 落盘 modData 同步访客
            }

            if (machine.readyForHarvest.Value && machine.heldObject.Value != null
                && loc.objects.TryGetValue(tile, out Object consoleObj) && IsConsole(consoleObj))
            {
                consoleObj.showNextIndex.Value = true;
            }
        }

        /// <summary>每秒一次：扫描所有有电脑的地点，收成品、投原料、刷新亮灯。
        /// 性能保护：菜单打开时跳过；扫描全程限一台电脑，超 5ms 记日志。</summary>
        public void RunAutomationForAll()
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer || Game1.activeClickableMenu != null)
                return;

            int totalCollected = 0;
            int totalLoaded = 0;
            sawReadyThisTick = false;

            Perf.Measure(Monitor, "RunAutomationForAll", 5.0, () =>
            {
                foreach (KeyValuePair<GameLocation, List<Vector2>> pair in ConsoleCache)
                {
                    foreach (Vector2 tile in pair.Value)
                    {
                        ConsoleState state = GetState(pair.Key, tile);
                        (int collected, int loaded) = RunOneConsole(pair.Key, tile, state);
                        totalCollected += collected;
                        totalLoaded += loaded;
                    }
                }
            });

            if (totalCollected > 0 || totalLoaded > 0)
                Game1.addHUDMessage(new HUDMessage($"小屋电脑：投放 {totalLoaded} 份原料，收取 {totalCollected} 份成品", 2));
        }

        /// <summary>扫描一台电脑所在地点的全部机器，顺带把这台电脑的红点同步成"该地点是否有可收成品"。</summary>
        private (int collected, int loaded) RunOneConsole(GameLocation loc, Vector2 tile, ConsoleState state)
        {
            int collected = 0;
            int loaded = 0;

            Chest inputChest = GetInputChestObj(loc, tile);
            Chest outputChest = GetOutputChestObj(loc, tile);
            IList<Item> input = GetEffectiveInputItems(loc, tile);
            Inventory output = outputChest.Items;

            bool locationHasReady = false;
            bool chestChanged = false;

            foreach (KeyValuePair<Vector2, Object> pair in loc.Objects.Pairs.ToList())
            {
                Object machine = pair.Value;
                if (machine == null || !machine.bigCraftable.Value || !IsMachine(machine) || machine is Chest)
                    continue;

                if (machine.readyForHarvest.Value && machine.heldObject.Value != null && machine.MinutesUntilReady <= 0)
                {
                    locationHasReady = true;
                    if (Config.AutoCollect && AutomationCore.TryCollectFromMachine(machine, output))
                    {
                        collected++;
                        chestChanged = true;
                    }
                }

                if (Config.AutoFeed
                    && machine.heldObject.Value == null
                    && !machine.readyForHarvest.Value
                    && !IsInManualCollectBackoff(loc, machine)
                    && AutomationCore.TryFeedMachine(machine, input, state, Game1.player))
                {
                    loaded++;
                    chestChanged = true;
                }
            }

            // 联机:主机自动化改了内置箱 → 落盘 modData(NetField 同步给访客)
            if (chestChanged)
                PersistToModData(loc, tile);

            if (locationHasReady)
                sawReadyThisTick = true;

            if (loc.objects.TryGetValue(tile, out Object consoleObj) && IsConsole(consoleObj))
                consoleObj.showNextIndex.Value = locationHasReady;

            return (collected, loaded);
        }

        /// <summary>把加工完成的产物收进成果箱；箱满则留在机器里。返回是否真的收走了。
        /// （逻辑已迁到 <see cref="AutomationCore"/>，这里保留一个无依赖入口。）</summary>
        private static bool TryCollectFromMachine(Object machine, Inventory output)
        {
            return AutomationCore.TryCollectFromMachine(machine, output);
        }

        /// <summary>玩家手动收走整堆产物时由补丁调用：记录冷却，短时间内不给这台机器自动投料。</summary>
        public void NotifyManualCollect(GameLocation loc, Object machine)
        {
            if (loc == null || machine == null || !IsMachine(machine))
                return;
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            ManualCollectBackoffUntil[MachineKey(loc, machine)] = now + ManualCollectBackoffSeconds;

            // 玩家开始手动收了，把灯灭掉，下一次扫描会按实际情况重算。
            if (ConsoleCache.TryGetValue(loc, out List<Vector2>? consoles))
            {
                foreach (Vector2 tile in consoles)
                {
                    if (loc.objects.TryGetValue(tile, out Object consoleObj) && IsConsole(consoleObj))
                        consoleObj.showNextIndex.Value = false;
                }
            }
        }

        private bool IsInManualCollectBackoff(GameLocation loc, Object machine)
        {
            if (!ManualCollectBackoffUntil.TryGetValue(MachineKey(loc, machine), out double until))
                return false;
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            if (now < until)
                return true;
            ManualCollectBackoffUntil.Remove(MachineKey(loc, machine));
            return false;
        }

        /*********
        ** 机器判定（逻辑已迁到 AutomationCore，这里保留兼容入口）
        *********/

        private bool IsMachine(Object obj)
        {
            return AutomationCore.IsMachine(obj, Config);
        }

        private bool CanMachineAccept(Object machine, Item item)
        {
            return AutomationCore.CanMachineAccept(machine, item);
        }

        /*********
        ** 界面入口与统计
        *********/

        public void OpenConsoleMenu(GameLocation loc, Vector2 tile, Object consoleObj)
        {
            ConsoleState state = GetState(loc, tile);
            Game1.activeClickableMenu = new ConsoleMenu(Helper, this, loc, tile, state);
        }

        public Summary BuildSummary(GameLocation loc, Vector2 tile, ConsoleState state)
        {
            Summary summary = new()
            {
                InputCount = GetInputChestObj(loc, tile).Items.Count,
                OutputCount = GetOutputChestObj(loc, tile).Items.Count
            };
            foreach (KeyValuePair<Vector2, Object> pair in loc.Objects.Pairs)
            {
                Object value = pair.Value;
                if (value is Chest || !IsMachine(value))
                    continue;
                summary.TotalKegs++;
                if (value.readyForHarvest.Value)
                    summary.Ready++;
                else if (value.heldObject.Value != null)
                    summary.Running++;
                else
                    summary.Idle++;
            }
            return summary;
        }

        /// <summary>本次扫描是否看到任何待收机器（暂未对外使用，预留给更细的灯控）。</summary>
        public bool SawReadyThisTick => sawReadyThisTick;
    }
}
