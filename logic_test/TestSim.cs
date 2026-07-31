using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Logging;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using StardewValley.Objects;
using Object = StardewValley.Object;

// ============================================================
// 原版机器行为模拟器
// 规则匹配（TryGetMachineOutputRule/CanApplyOutput/RequiredCount 检查）
// 走真实 MachineDataUtility 代码；只模拟"物品生产"（OutputMachine 造物）。
// 严格按反编译的原版实现（Object.cs 2026 版）复刻自动化投料链路：
//   AttemptAutoLoad → performObjectDropInAction → PlaceInMachine
//     → HasAdditionalRequirements / TryGetMachineOutputRule / OutputMachine
//     → ConsumeInventoryItem(按 RequiredCount 扣 inputItem.Stack)
//   OutputMachine 内部会调 minutesElapsed(0)  ← mod 补丁挂在这里
// ============================================================

/// <summary>模拟原版 Object 里"自动化投料"相关的机器行为（反编译逐行对齐）。</summary>
public class SimMachine : Object
{
    public MachineData? Data;
    public Inventory Inventory = new();
    public int LoadedCount;           // 统计：被成功投了多少次料
    public int FinishedCount;         // 统计：完成（可收取）了多少次
    public int MinutesElapsedCalls;   // 统计：minutesElapsed 被调了多少次

    public SimMachine(string unqualifiedId, MachineData? data = null)
    {
        this.ItemId = unqualifiedId; // 原版 Object 设 ItemId 后 QualifiedItemId 自动派生
        this.Data = data;
        this.bigCraftable.Value = true;
        this.readyForHarvest.Value = false;
        this.heldObject.Value = null;
        this.MinutesUntilReady = 0;
    }

    public bool IsIdle => heldObject.Value == null && !readyForHarvest.Value;

    /// <summary>GetMachineData 原版非虚且走 content 管线，测试里用 new 隐藏并返回测试数据（null 模拟"机器数据缺失"）。</summary>
    public new MachineData GetMachineData() => Data;

    /// <summary>原版 AttemptAutoLoad(IInventory, Farmer) 的核心循环。</summary>
    public override bool AttemptAutoLoad(IInventory inventory, Farmer who)
    {
        if (heldObject.Value != null)
            return false;
        autoLoadFrom = inventory;
        foreach (Item item in inventory)
        {
            if (performObjectDropInAction(item, probe: false, who))
            {
                autoLoadFrom = null;
                return true;
            }
        }
        autoLoadFrom = null;
        return false;
    }

    /// <summary>原版 performObjectDropInAction 里机器分支（非机器分支一律 false，本模拟只关心机器）。</summary>
    public override bool performObjectDropInAction(Item dropInItem, bool probe, Farmer who, bool returnFalseIfItemConsumed = false)
    {
        if (!(dropInItem is Object obj))
            return false;

        MachineData machineData = GetMachineData();
        if (machineData != null)
        {
            if (heldObject.Value != null && !machineData.AllowLoadWhenFull)
                return false;
            if (probe && MinutesUntilReady > 0)
                return false;
            return PlaceInMachine(machineData, dropInItem, probe, who);
        }
        return false;
    }

    /// <summary>原版 PlaceInMachine 的自动化相关部分（消耗/输出/回调），完全按反编译顺序。
    /// 规则匹配走真实 MachineDataUtility（RequiredCount 数量检查是真逻辑），只模拟 OutputMachine 的造物。</summary>
    public bool PlaceInMachine(MachineData machineData, Item inputItem, bool probe, Farmer who, bool showMessages = true, bool playSounds = true)
    {
        if (machineData == null || inputItem == null)
            return false;
        if (heldObject.Value != null)
        {
            if (!machineData.AllowLoadWhenFull)
                return false;
            if (inputItem.QualifiedItemId == lastInputItem.Value?.QualifiedItemId)
                return false;
        }
        if (!MachineDataUtility.HasAdditionalRequirements(autoLoadFrom ?? who.Items, machineData.AdditionalConsumedItems, out var _))
            return false;

        if (!MachineDataUtility.TryGetMachineOutputRule(this, machineData, MachineOutputTrigger.ItemPlacedInMachine, inputItem, who, Location, out var rule, out var triggerRule, out var _, out var _))
            return false;

        if (probe)
            return true;

        if (!SimOutputMachine(machineData, rule, inputItem, who, probe))
            return false;

        if (machineData.AdditionalConsumedItems != null)
        {
            IInventory inventory = autoLoadFrom ?? who.Items;
            foreach (var additionalConsumedItem in machineData.AdditionalConsumedItems)
                inventory.ReduceId(additionalConsumedItem.ItemId, additionalConsumedItem.RequiredCount);
        }
        if (triggerRule.RequiredCount > 0)
            ConsumeInventoryItem(who, inputItem, triggerRule.RequiredCount);

        LoadedCount++;
        return true;
    }

    /// <summary>模拟原版 OutputMachine：造出产出物、设倒计时、**调 minutesElapsed(0)**（mod 补丁触发点）。</summary>
    private bool SimOutputMachine(MachineData machine, MachineOutputRule outputRule, Item inputItem, Farmer who, bool probe)
    {
        if (machine == null || (heldObject.Value != null && !machine.AllowLoadWhenFull))
            return false;
        if (outputRule == null)
            return false;

        string outputId = outputRule.OutputItem?.FirstOrDefault()?.ItemId ?? "395";
        Object outputItem = new SimItem(outputId, 1);
        if (probe)
            return true;

        heldObject.Value = outputItem;
        MinutesUntilReady = outputRule.MinutesUntilReady >= 0 ? outputRule.MinutesUntilReady : 120;
        if (MinutesUntilReady == 0)
            readyForHarvest.Value = true;
        lastOutputRuleId.Value = outputRule.Id;
        lastInputItem.Value = inputItem.getOne();
        if (machine.WobbleWhileWorking)
            scale.X = 5f;
        // 关键：原版投料成功后会立刻触发一次 minutesElapsed(0)。
        minutesElapsed(0);
        return true;
    }

    /// <summary>模拟机器倒计时归零：readyForHarvest 置位（会触发 mod 的补丁）。</summary>
    public void TickFinish()
    {
        MinutesUntilReady = 0;
        readyForHarvest.Value = true;
        FinishedCount++;
        minutesElapsed(0);
    }

    /// <summary>模拟原版 CheckForActionOnMachine 的玩家手动收取：整堆收走，机器回到空。</summary>
    public bool PlayerHarvestAll()
    {
        if (!readyForHarvest.Value || heldObject.Value == null)
            return false;
        heldObject.Value = null;
        readyForHarvest.Value = false;
        showNextIndex.Value = false;
        MinutesUntilReady = 0;
        return true;
    }
}

// ============================================================
// 最小假物品：复刻 Object 的 getOne/ConsumeStack/Stack 行为
// ============================================================

public class SimItem : Object
{
    public SimItem(string unqualifiedId, int stack, int category = 0)
    {
        // 原版 Object 设 ItemId 后 QualifiedItemId 自动派生（"348" → "(O)348"）
        this.ItemId = unqualifiedId;
        this.Stack = stack;
        this.Category = category;
    }
    public override int maximumStackSize() => 999;
    protected override Item GetOneNew() => new SimItem(this.ItemId, 1) { Category = this.Category };
    public override bool canBeGivenAsGift() => true;
}

/// <summary>非 Object 物品（模拟工具/鱼竿等混进箱子的情况）。</summary>
public class SimTool : Item
{
    public SimTool() { this.Stack = 1; }
    public override string TypeDefinitionId => "Test";
    public override string DisplayName => "test tool";
    public override int maximumStackSize() => 1;
    public override void drawInMenu(SpriteBatch spriteBatch, Vector2 location, float scaleSize, float transparency, float layerDepth, StackDrawType drawStackNumber, Color color, bool drawShadow) { }
    public override string getDescription() => "";
    public override bool isPlaceable() => false;
    protected override Item GetOneNew() => new SimTool();
}

// ============================================================
// 测试数据构造器：按原版机器数据格式造规则
// ============================================================

public static class MachineSpec
{
    /// <summary>小桶规则数据：咖啡豆 5 换 1 咖啡（CoffeeBean 规则在前——否则数量不足的豆会被 Default 按 1:1 吃掉）、其他原料 1 换 1。</summary>
    public static MachineData Keg()
    {
        return new MachineData
        {
            HasInput = true,
            HasOutput = true,
            WobbleWhileWorking = true,
            OutputRules = new List<MachineOutputRule>
            {
                new()
                {
                    Id = "CoffeeBean",
                    MinutesUntilReady = 120,
                    OutputItem = new List<MachineItemOutput> { new() { ItemId = "395" } }, // 咖啡豆→咖啡
                    Triggers = new List<MachineOutputTriggerRule>
                    {
                        new()
                        {
                            Trigger = MachineOutputTrigger.ItemPlacedInMachine,
                            RequiredItemId = "(O)433", // 咖啡豆
                            RequiredCount = 5          // 5 颗换 1 杯
                        }
                    }
                },
                new()
                {
                    Id = "Default",
                    MinutesUntilReady = 120,
                    OutputItem = new List<MachineItemOutput> { new() { ItemId = "459" } }, // 啤酒花→淡啤酒
                    Triggers = new List<MachineOutputTriggerRule>
                    {
                        new() { Trigger = MachineOutputTrigger.ItemPlacedInMachine, RequiredCount = 1 }
                    }
                }
            }
        };
    }

    /// <summary>木桶规则数据：酒 1 换 1 陈酿（14 天）。</summary>
    public static MachineData Cask()
    {
        return new MachineData
        {
            HasInput = true,
            HasOutput = true,
            WobbleWhileWorking = true,
            OutputRules = new List<MachineOutputRule>
            {
                new()
                {
                    Id = "Default",
                    DaysUntilReady = 14,
                    OutputItem = new List<MachineItemOutput> { new() { ItemId = "348" } },
                    Triggers = new List<MachineOutputTriggerRule>
                    {
                        new() { Trigger = MachineOutputTrigger.ItemPlacedInMachine, RequiredCount = 1 }
                    }
                }
            }
        };
    }
}

namespace ShedConsoleComputer
{
    /// <summary>UI 层 stub（真实 ConsoleMenu 是界面，测试不编译它；只让 ConsoleManager 能编过）。</summary>
    public class ConsoleMenu : StardewValley.Menus.IClickableMenu
    {
        public ConsoleMenu(IModHelper helper, ConsoleManager manager, GameLocation loc, Vector2 tile, ConsoleState state)
            : base(0, 0, 100, 100) { }
    }
}

// ============================================================
// 静态 stub：让链接进来的 mod 源码能编译（不涉及机器行为）
// ============================================================

public static class Stub
{
    public static readonly IMonitor Monitor = new StubMonitor();
    public static readonly IModHelper Helper = new StubHelper();
}

public class StubMonitor : IMonitor
{
    public bool IsExiting => false;
    public bool IsVerbose => false;
    public void Log(string message, LogLevel level = LogLevel.Trace) { }
    public void LogOnce(string message, LogLevel level = LogLevel.Trace) { }
    public void VerboseLog(string message) { }
    public void VerboseLog(ref VerboseLogStringHandler message) { }
}

public class StubHelper : IModHelper
{
    public string DirectoryPath => "";
    public IModEvents Events => throw new NotImplementedException();
    public ICommandHelper ConsoleCommands => throw new NotImplementedException();
    public IGameContentHelper GameContent => throw new NotImplementedException();
    public IModContentHelper ModContent => throw new NotImplementedException();
    public IContentPackHelper ContentPacks => throw new NotImplementedException();
    public IDataHelper Data => throw new NotImplementedException();
    public IInputHelper Input => throw new NotImplementedException();
    public IReflectionHelper Reflection => throw new NotImplementedException();
    public IModRegistry ModRegistry => throw new NotImplementedException();
    public IMultiplayerHelper Multiplayer => throw new NotImplementedException();
    public ITranslationHelper Translation => throw new NotImplementedException();
    public void WriteConfig<TConfig>(TConfig config) where TConfig : class, new() { }
    public TConfig ReadConfig<TConfig>() where TConfig : class, new() => new TConfig();
}
