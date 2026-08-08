using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using ShedConsoleComputer;
using Object = StardewValley.Object;

// ============================================================
// 小屋主控电脑 深度回归测试
// 直接链接主工程源码（ConsoleManager/AutomationCore/ModConfig），
// 用 SimMachine 模拟原版机器行为（规则匹配走真实 MachineDataUtility）。
// 专门覆盖：咖啡/咖啡豆投料、优先级、收成品、空箱、混合物品、异常机器数据。
// 跑法：cd logic_test && dotnet run -c Release
// ============================================================

int fails = 0, pass = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : "  << " + detail));
    if (ok) pass++; else fails++;
}
void CheckEq<T>(string name, T got, T expected)
{
    bool ok = Equals(got, expected);
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : $"  << got={got} expected={expected}"));
    if (ok) pass++; else fails++;
}

var config = new ModConfig { AutoFeed = true, AutoCollect = true, IncludeCasks = true, IncludePreservesJar = true };
var state = new ConsoleState();
// 注意：测试环境不能碰 Game1（静态构造会加载游戏资产导致挂起），who 参数模拟器从不使用。
Farmer? who = null;
// 注入机器数据源：AutomationCore 参数类型是 Object，虚调用不会命中 SimMachine 的
// new 隐藏方法，所以必须走可替换的数据源（游戏里默认原版 content 管线）。
AutomationCore.MachineDataProvider = m => m is SimMachine sm ? sm.Data : null;
// 注入"放入成果箱"模拟实现（默认走原版 Utility，测试环境无 Game1 会原生崩溃）。
// 复刻原版 addItemToThisInventoryList 的语义：先堆叠同类、再找空位（36 格）、放不下返回剩余。
// 注意 addToStack 是"加法语义"（把 held 整个加进 slot，溢出返回剩余量），
// 剩余量要从 held 里扣掉（ConsumeStack），否则 held.Stack 会虚高。
AutomationCore.CollectPutDelegate = (held, output) =>
{
    // 先堆叠同类
    foreach (Item slot in output)
    {
        if (slot.canStackWith(held))
        {
            int leftover = slot.addToStack(held);
            if (leftover == 0)
                return null;
            held.ConsumeStack(held.Stack - leftover); // 扣掉已堆叠的部分，剩 leftover
            break;
        }
    }
    if (output.Count < 36)
    {
        output.Add(held);
        return null;
    }
    return held;
};

// ============================================================
// 第一组：机器判定 CanMachineAccept —— 每种机器吃什么、不吃什么
// ============================================================
{
    SimMachine keg = new("12", MachineSpec.Keg());
    SimMachine cask = new("163", MachineSpec.Cask());

    // 小桶：水果(-79)/蔬菜(-75) 能吃；咖啡(395, 饮品) 不吃；咖啡豆(433) 能吃；小麦(262)/茶叶(304)/啤酒花(304?) 能吃
    Check("keg: 水果能吃", AutomationCore.CanMachineAccept(keg, new SimItem("348", 1, -79)));   // 葡萄(水果)
    Check("keg: 蔬菜能吃", AutomationCore.CanMachineAccept(keg, new SimItem("264", 1, -75)));   // 卷心菜(蔬菜)
    Check("keg: 咖啡豆能吃", AutomationCore.CanMachineAccept(keg, new SimItem("433", 5)));
    Check("keg: 小麦能吃", AutomationCore.CanMachineAccept(keg, new SimItem("262", 1)));
    Check("keg: 啤酒花能吃", AutomationCore.CanMachineAccept(keg, new SimItem("304", 1)));
    Check("keg: 茶叶(815)能吃", AutomationCore.CanMachineAccept(keg, new SimItem("815", 1)));  // 1.5+ 绿茶茶叶
    Check("keg: 咖啡(成品)不能吃", !AutomationCore.CanMachineAccept(keg, new SimItem("395", 1, -27)));   // 咖啡是饮品分类
    Check("keg: 酒不能吃", !AutomationCore.CanMachineAccept(keg, new SimItem("348", 1, -26)));           // 葡萄汁→酒后不能再投
    Check("keg: null 不吃", !AutomationCore.CanMachineAccept(keg, null));
    Check("keg: 工具不吃", !AutomationCore.CanMachineAccept(keg, new SimTool()));

    // 木桶：酒(-26) 能吃；水果不能吃
    Check("cask: 酒能吃", AutomationCore.CanMachineAccept(cask, new SimItem("348", 1, -26)));
    Check("cask: 水果不能吃", !AutomationCore.CanMachineAccept(cask, new SimItem("348", 1, -79)));

    // 罐头瓶：水果/蔬菜能吃；咖啡豆不能吃
    SimMachine jar = new("15", MachineSpec.Keg());
    Check("jar: 水果能吃", AutomationCore.CanMachineAccept(jar, new SimItem("348", 1, -79)));
    Check("jar: 蔬菜能吃", AutomationCore.CanMachineAccept(jar, new SimItem("264", 1, -75)));
    Check("jar: 咖啡豆不能吃", !AutomationCore.CanMachineAccept(jar, new SimItem("433", 5)));

    // 配置开关：木桶/罐头瓶未开启时不算机器
    var cfgOff = new ModConfig { IncludeCasks = false, IncludePreservesJar = false };
    Check("cfg: 关木桶后不算机器", !AutomationCore.IsMachine(cask, cfgOff));
    Check("cfg: 关罐头后不算机器", !AutomationCore.IsMachine(jar, cfgOff));
    Check("cfg: 开木桶后算机器", AutomationCore.IsMachine(cask, config));
    Check("cfg: null 不算机器", !AutomationCore.IsMachine(null, config));
    Check("cfg: 普通大物件不算机器", !AutomationCore.IsMachine(new SimMachine("100"), config));
}

// ============================================================
// 第二组：投料 —— 咖啡豆 5 换 1 咖啡（本次报告的核心场景）
// ============================================================
{
    // 5 颗咖啡豆 → 投料成功，扣 5 颗
    SimMachine keg = new("12", MachineSpec.Keg());
    var chest = new Inventory { new SimItem("433", 5) };
    Check("coffee: 5 颗咖啡豆能投", AutomationCore.TryFeedMachine(keg, chest, state, who));
    CheckEq("coffee: 投完剩 0 颗", chest.Count, 0);
    CheckEq("coffee: 机器进入酿造", keg.LoadedCount, 1);

    // 6 颗咖啡豆 → 成功，剩 1 颗
    SimMachine keg2 = new("12", MachineSpec.Keg());
    var chest2 = new Inventory { new SimItem("433", 6) };
    Check("coffee: 6 颗咖啡豆能投", AutomationCore.TryFeedMachine(keg2, chest2, state, who));
    CheckEq("coffee: 6 投 5 剩 1", chest2[0]!.Stack, 1);

    // 4 颗不够 → 不投，不扣
    SimMachine keg3 = new("12", MachineSpec.Keg());
    var chest3 = new Inventory { new SimItem("433", 4) };
    Check("coffee: 4 颗不够不投", !AutomationCore.TryFeedMachine(keg3, chest3, state, who));
    CheckEq("coffee: 不够时一颗不扣", chest3[0]!.Stack, 4);

    // 咖啡豆不够 + 箱里还有水果 → 跳过咖啡豆，投水果（不卡死）
    SimMachine keg4 = new("12", MachineSpec.Keg());
    var chest4 = new Inventory { new SimItem("433", 3), new SimItem("348", 1, -79) };
    Check("coffee: 豆不够会跳过投水果", AutomationCore.TryFeedMachine(keg4, chest4, state, who));
    // 原版消耗后会压缩列表（RemoveEmptySlots），豆不够没被投 → 剩 3 颗豆
    Check("coffee: 跳过不扣豆", chest4.Count == 1 && chest4[0]!.Stack == 3);

    // 1 颗咖啡豆 → 不投（原来的 bug：getOne 副本永远投不进）
    SimMachine keg5 = new("12", MachineSpec.Keg());
    var chest5 = new Inventory { new SimItem("433", 1) };
    Check("coffee: 1 颗不投(不闪退)", !AutomationCore.TryFeedMachine(keg5, chest5, state, who));
    CheckEq("coffee: 1 颗不扣", chest5[0]!.Stack, 1);

    // 咖啡（成品 395）放进投料箱 → 不投（不是原料）
    SimMachine keg6 = new("12", MachineSpec.Keg());
    var chest6 = new Inventory { new SimItem("395", 3, -27) };
    Check("coffee: 成品咖啡不投", !AutomationCore.TryFeedMachine(keg6, chest6, state, who));
    CheckEq("coffee: 成品不扣", chest6[0]!.Stack, 3);
}

// ============================================================
// 第三组：投料 —— 普通原料 1 换 1（水果/蔬菜/木桶/罐头瓶）
// ============================================================
{
    // 水果 1 个 → 投成功，箱空
    SimMachine keg = new("12", MachineSpec.Keg());
    var chest = new Inventory { new SimItem("348", 1, -79) };
    Check("feed: 1 个水果能投", AutomationCore.TryFeedMachine(keg, chest, state, who));
    CheckEq("feed: 投完箱空", chest.Count, 0);

    // 一叠水果 10 个 → 投 1 次扣 1 个，剩 9（机器每次只吃 1 个）
    SimMachine keg2 = new("12", MachineSpec.Keg());
    var chest2 = new Inventory { new SimItem("348", 10, -79) };
    Check("feed: 一叠水果能投", AutomationCore.TryFeedMachine(keg2, chest2, state, who));
    CheckEq("feed: 投 1 次扣 1 个", chest2[0]!.Stack, 9);

    // 空箱 → 不投
    SimMachine keg3 = new("12", MachineSpec.Keg());
    Check("feed: 空箱不投", !AutomationCore.TryFeedMachine(keg3, new Inventory(), state, who));

    // 机器已满（heldObject 不为 null）→ 不投
    SimMachine keg4 = new("12", MachineSpec.Keg());
    keg4.heldObject.Value = new SimItem("459", 1);
    Check("feed: 机器满不投", !AutomationCore.TryFeedMachine(keg4, new Inventory { new SimItem("348", 1, -79) }, state, who));

    // 箱子里全是不能吃的（酒→水果箱…）→ 不投，不扣
    SimMachine cask = new("163", MachineSpec.Cask());
    var chest5 = new Inventory { new SimItem("348", 1, -79) };
    Check("feed: 木桶不吃水果不投", !AutomationCore.TryFeedMachine(cask, chest5, state, who));
    CheckEq("feed: 不扣", chest5[0]!.Stack, 1);

    // 混合：工具 + 水果 → 跳过工具投水果
    SimMachine keg6 = new("12", MachineSpec.Keg());
    var chest6 = new Inventory { new SimTool(), new SimItem("348", 2, -79) };
    Check("feed: 混工具能投水果", AutomationCore.TryFeedMachine(keg6, chest6, state, who));
    // 原版消耗后压缩列表：工具没被扣、水果剩 1（从 2 减到 1 没被清空所以留在原位）
    Check("feed: 工具没被扣", chest6.Count == 2);
    Check("feed: 水果剩 1", chest6[1]!.Stack == 1);

    // 空输入列表（null 元素）→ 不投不崩
    SimMachine keg7 = new("12", MachineSpec.Keg());
    var chest7 = new Inventory { null! };
    Check("feed: 只有空格不投", !AutomationCore.TryFeedMachine(keg7, chest7, state, who));

    // 机器数据缺失（模拟机器没有 Machines 数据）→ 不投不崩
    SimMachine keg8 = new("12", null);
    var chest8 = new Inventory { new SimItem("348", 1, -79) };
    Check("feed: 机器数据缺失不投", !AutomationCore.TryFeedMachine(keg8, chest8, state, who));
    CheckEq("feed: 数据缺失不扣", chest8[0]!.Stack, 1);
}

// ============================================================
// 第四组：优先级 —— 优先级列表决定投哪个
// ============================================================
{
    // 优先级里有咖啡豆 → 优先投咖啡豆（即使水果在前面）
    SimMachine keg = new("12", MachineSpec.Keg());
    var chest = new Inventory { new SimItem("348", 2, -79), new SimItem("433", 5) };
    var st = new ConsoleState();
    st.InputPriority.Add("(O)433");
    Check("prio: 优先投咖啡豆", AutomationCore.TryFeedMachine(keg, chest, st, who));
    Check("prio: 水果没动", chest.Count == 1 && chest[0]!.Stack == 2);   // 豆被投光后列表压缩，只剩水果
    Check("prio: 咖啡豆被投光", chest.Count == 1);

    // 优先级指向不存在的物品 → 按箱内顺序投
    SimMachine keg2 = new("12", MachineSpec.Keg());
    var chest2 = new Inventory { new SimItem("348", 2, -79), new SimItem("433", 5) };
    var st2 = new ConsoleState();
    st2.InputPriority.Add("(O)999");
    Check("prio: 无效优先级回退", AutomationCore.TryFeedMachine(keg2, chest2, st2, who));
    CheckEq("prio: 回退投了水果", chest2[0]!.Stack, 1);

    // 优先级：咖啡豆不够 5 颗但后面有水果 → 跳过豆投水果
    SimMachine keg3 = new("12", MachineSpec.Keg());
    var chest3 = new Inventory { new SimItem("348", 2, -79), new SimItem("433", 3) };
    var st3 = new ConsoleState();
    st3.InputPriority.Add("(O)433");
    Check("prio: 豆不够跳过投水果", AutomationCore.TryFeedMachine(keg3, chest3, st3, who));
    Check("prio: 豆没扣", chest3.Count == 2 && chest3[1]!.Stack == 3);
    CheckEq("prio: 水果扣了", chest3[0]!.Stack, 1);
}

// ============================================================
// 第五组：收成品 —— 自动收进成果箱、箱满留在机器
// ============================================================
{
    // 机器待收 → 收走，机器回空
    SimMachine keg = new("12", MachineSpec.Keg());
    keg.heldObject.Value = new SimItem("459", 1);
    keg.readyForHarvest.Value = true;
    keg.MinutesUntilReady = 0;
    var output = new Inventory();
    Check("collect: 能收", AutomationCore.TryCollectFromMachine(keg, output));
    CheckEq("collect: 成果箱有 1 个", output.Count, 1);
    Check("collect: 机器回空", keg.IsIdle);

    // 没成品 → 不收
    SimMachine keg2 = new("12", MachineSpec.Keg());
    var output2 = new Inventory();
    Check("collect: 没成品不收", !AutomationCore.TryCollectFromMachine(keg2, output2));

    // 成果箱满 → 不收，机器保持待收
    SimMachine keg3 = new("12", MachineSpec.Keg());
    keg3.heldObject.Value = new SimItem("459", 1);
    keg3.readyForHarvest.Value = true;
    keg3.MinutesUntilReady = 0;
    var output3 = new Inventory();
    for (int i = 0; i < 36; i++) output3.Add(new SimItem("395", 1, -27));
    Check("collect: 箱满不收", !AutomationCore.TryCollectFromMachine(keg3, output3));
    Check("collect: 箱满机器保持待收", keg3.readyForHarvest.Value && keg3.heldObject.Value != null);

    // null 参数 → 不崩
    Check("collect: null 输出不收", !AutomationCore.TryCollectFromMachine(keg, null!));
    Check("collect: null 机器不收", !AutomationCore.TryCollectFromMachine(null!, new Inventory()));
}

// ============================================================
// 第六组：端到端 —— 模拟一整天：投料 → 完成 → 自动收 → 再投
// ============================================================
{
    SimMachine keg = new("12", MachineSpec.Keg());
    var chest = new Inventory { new SimItem("348", 3, -79) };
    var output = new Inventory();

    Check("e2e: 第一次投料", AutomationCore.TryFeedMachine(keg, chest, state, who));
    CheckEq("e2e: 投后剩 2", chest[0]!.Stack, 2);

    // 机器工作中：不能再投
    Check("e2e: 工作中不投", !AutomationCore.TryFeedMachine(keg, chest, state, who));

    // 倒计时归零 → 可收
    keg.TickFinish();
    Check("e2e: 完成后能收", AutomationCore.TryCollectFromMachine(keg, output));
    CheckEq("e2e: 成果进箱", output.Count, 1);

    // 机器空了 → 再投一份
    Check("e2e: 空机再投", AutomationCore.TryFeedMachine(keg, chest, state, who));
    CheckEq("e2e: 第二次投后剩 1", chest[0]!.Stack, 1);
}

// ============================================================
// 第七组：箱子 UI 数据桥 —— 放咖啡/咖啡豆（本次报告的场景）、
// 空箱、放满、堆叠、压缩回写、格子边界
// ============================================================
{
    // 放咖啡（成品饮品）进投料箱：能放进去、位置正确
    var chest = new Inventory();
    var slots = ChestUiCore.ExpandToFixedSlots(chest);
    CheckEq("ui: 空箱展开 36 格", ChestUiCore.CountItems(slots), 0);
    Check("ui: 放咖啡能放下", ChestUiCore.PutIntoFixedSlots(slots, new SimItem("395", 1, -27)) == null);
    CheckEq("ui: 咖啡进了第 0 格", ChestUiCore.CountItems(slots), 1);

    // 放咖啡豆 5 颗：堆叠正确
    Check("ui: 放 5 颗咖啡豆", ChestUiCore.PutIntoFixedSlots(slots, new SimItem("433", 5)) == null);
    CheckEq("ui: 咖啡豆在格 1", slots[1]!.Stack, 5);

    // 同类堆叠：再放 3 颗咖啡豆 → 合并到格 1 成 8
    Check("ui: 再放 3 颗堆叠", ChestUiCore.PutIntoFixedSlots(slots, new SimItem("433", 3)) == null);
    CheckEq("ui: 堆叠成 8", slots[1]!.Stack, 8);

    // 放满 36 格 → 同类可堆叠（堆到已有堆上），不同类放不下（原样返回）
    var full = new Inventory();
    var fullSlots = ChestUiCore.ExpandToFixedSlots(full);
    for (int i = 0; i < 36; i++)
        fullSlots[i] = new SimItem("348", 1, -79);
    Check("ui: 满箱同类堆叠放下", ChestUiCore.PutIntoFixedSlots(fullSlots, new SimItem("348", 1, -79)) == null);
    CheckEq("ui: 堆叠后格 0 成 2", fullSlots[0]!.Stack, 2);
    var overflow = new SimItem("264", 1, -75);  // 卷心菜（不同类）
    Check("ui: 满箱不同类放不下", ChestUiCore.PutIntoFixedSlots(fullSlots, overflow) != null);
    Check("ui: 放不下的原物返回", ReferenceEquals(ChestUiCore.PutIntoFixedSlots(fullSlots, overflow), overflow));

    // 从箱子拿走一格（玩家点箱子里的咖啡）
    var taken = ChestUiCore.GrabFromChest(slots, 0);
    Check("ui: 拿走咖啡", taken != null && taken.QualifiedItemId == "(O)395");
    CheckEq("ui: 拿走那格变空", slots[0], null);

    // 关闭：压缩回写紧凑列表
    var back = new Inventory();
    ChestUiCore.CompressBack(slots, back);
    CheckEq("ui: 压缩回写数量", back.Count, 1);
    Check("ui: 回写的还是咖啡豆", back[0]!.QualifiedItemId == "(O)433" && back[0]!.Stack == 8);

    // 边界：拿越界格 / 压缩 null 数组
    Check("ui: 越界拿返回 null", ChestUiCore.GrabFromChest(slots, 999) == null);
    Check("ui: 越界拿负数返回 null", ChestUiCore.GrabFromChest(slots, -1) == null);
    ChestUiCore.CompressBack(null!, new Inventory());  // 不崩即可
    Check("ui: null 压缩不崩", true);
    Check("ui: null 放入返回原物", ChestUiCore.PutIntoFixedSlots(null!, new SimItem("395", 1, -27)) != null);

    // ======== 翻倍 bug 回归（本次报告）：同一个物品实例不能同时出现在箱格和手上 ========
    // 场景模拟：手上 5 颗咖啡豆 → 点背包格放进箱子 → 回调把物品放进箱格。
    // 翻倍 bug 的机制：回调放完没清空手上的引用，同一实例同时挂在箱格和手上，
    // 再点一次就自我堆叠翻倍（5→10→20→40）。
    {
        // 手上 5 颗豆，箱格空：放进箱子后，手上必须清空（不能再引用同一实例）
        var hand = new SimItem("433", 5);
        var c1 = new Inventory();
        var s1 = ChestUiCore.ExpandToFixedSlots(c1);
        var leftover1 = ChestUiCore.PutIntoFixedSlots(s1, hand);  // 对应回调放完
        Check("dup: 放进后无剩余", leftover1 == null);
        CheckEq("dup: 箱格 0 是那 5 颗豆", s1[0]!.Stack, 5);
        Check("dup: 放完手上必须无引用", hand.Stack == 5 && !ReferenceEquals(s1[0], hand));

        // 再放一次同数量：应该新增一格（或堆叠），绝不能翻倍
        var hand2 = new SimItem("433", 5);
        var s2 = ChestUiCore.ExpandToFixedSlots(c1);
        // 先放 5 颗
        ChestUiCore.PutIntoFixedSlots(s2, new SimItem("433", 5));
        // 再放 5 颗（新实例，模拟玩家点背包另一格）
        var leftover2 = ChestUiCore.PutIntoFixedSlots(s2, hand2);
        Check("dup: 两次放入无剩余", leftover2 == null);
        CheckEq("dup: 堆叠成 10 而非翻倍", s2[0]!.Stack, 10);

        // 拿取方向：从箱子拿 5 颗到手上 → 箱格置空，手上拿到独立引用（原版 leftClick 语义）
        var s3 = ChestUiCore.ExpandToFixedSlots(new Inventory { new SimItem("433", 5) });
        var grabbed = ChestUiCore.GrabFromChest(s3, 0);
        Check("dup: 拿到 5 颗", grabbed != null && grabbed.Stack == 5);
        Check("dup: 箱格已空", s3[0] == null);
        // 手上再放回：箱格恢复，手引用清空，不翻倍
        var leftover3 = ChestUiCore.PutIntoFixedSlots(s3, grabbed!);
        Check("dup: 放回无剩余", leftover3 == null);
        CheckEq("dup: 放回 5 颗不翻倍", s3[0]!.Stack, 5);
        Check("dup: 放回后手引用独立", grabbed!.Stack == 5 && !ReferenceEquals(s3[0], grabbed));
    }
}

// ============================================================
// 第八组：负类物品误投检查 —— 常见"不能投"的类别放进投料箱
// ============================================================
{
    SimMachine keg = new("12", MachineSpec.Keg());
    var chest = new Inventory { new SimItem("434", 1, -27) }; // 蛋黄酱(饮品-27)
    Check("reject: 蛋黄酱不投", !AutomationCore.TryFeedMachine(keg, chest, state, who));
    var chest2 = new Inventory { new SimItem("340", 1, -81) }; // 蜂蜜(原料-81)
    // 蜂蜜在白名单里（小桶酿蜂蜜酒，正确行为）→ 能投、按 1:1 消耗
    Check("accept: 蜂蜜(340)能投(蜂蜜酒)", AutomationCore.TryFeedMachine(keg, chest2, state, who));
    CheckEq("accept: 蜂蜜投掉", chest2.Count, 0);
    var chest3 = new Inventory { new SimItem("188", 1, -20) }; // 鱼
    Check("reject: 鱼不投", !AutomationCore.TryFeedMachine(keg, chest3, state, who));
    var chest4 = new Inventory { new SimItem("176", 1, -6) };  // 蛋
    Check("reject: 蛋不投", !AutomationCore.TryFeedMachine(keg, chest4, state, who));
    var chest5 = new Inventory { new SimItem("184", 1, -18) }; // 牛奶
    Check("reject: 牛奶不投", !AutomationCore.TryFeedMachine(keg, chest5, state, who));
    // 混杂：鱼+水果 → 跳过鱼投水果
    SimMachine keg6 = new("12", MachineSpec.Keg());
    var chest6 = new Inventory { new SimItem("188", 1, -20), new SimItem("348", 1, -79) };
    Check("reject: 鱼+水果跳过鱼", AutomationCore.TryFeedMachine(keg6, chest6, state, who));
}

// ============================================================
// 第九组：数量预检 HasEnoughCountForRule —— 直接测 internal 方法
// ============================================================
{
    SimMachine keg = new("12", MachineSpec.Keg());
    // 5 颗咖啡豆 → 够
    Check("count: 5 颗够", AutomationCore.HasEnoughCountForRule(keg, new SimItem("433", 5), who));
    // 4 颗 → 不够
    Check("count: 4 颗不够", !AutomationCore.HasEnoughCountForRule(keg, new SimItem("433", 4), who));
    // 1 颗 → 不够
    Check("count: 1 颗不够", !AutomationCore.HasEnoughCountForRule(keg, new SimItem("433", 1), who));
    // 普通水果 1 个 → 够（RequiredCount=1）
    Check("count: 水果 1 个够", AutomationCore.HasEnoughCountForRule(keg, new SimItem("348", 1, -79), who));
    // 机器数据缺失 → 不够（安全方向）
    SimMachine kegNull = new("12", null);
    Check("count: 数据缺失不够", !AutomationCore.HasEnoughCountForRule(kegNull, new SimItem("433", 5), who));
    // 蜂蜜：匹配 Default 规则（1 换 1）→ 数量够（模拟器 Default 规则无过滤，真实游戏由规则过滤）
    Check("count: 蜂蜜数量够", AutomationCore.HasEnoughCountForRule(keg, new SimItem("340", 1, -81), who));
}

// ============================================================
// 第十组：收成品部分收取 —— 成果箱同类堆叠接近满时
// ============================================================
{
    // 成果箱已有 34 个啤酒(459, 可堆叠999)：收 1 个没问题
    SimMachine keg = new("12", MachineSpec.Keg());
    keg.heldObject.Value = new SimItem("459", 1);
    keg.readyForHarvest.Value = true;
    keg.MinutesUntilReady = 0;
    var output = new Inventory { new SimItem("459", 34) };
    Check("partial: 空位够能收", AutomationCore.TryCollectFromMachine(keg, output));
    CheckEq("partial: 堆叠成 35", output[0]!.Stack, 35);

    // 成果箱同类堆叠 999 满但还有空位：剩余放新格，全部收走（与原版 addItemToThisInventoryList 一致）
    SimMachine keg2 = new("12", MachineSpec.Keg());
    keg2.heldObject.Value = new SimItem("459", 1);
    keg2.readyForHarvest.Value = true;
    keg2.MinutesUntilReady = 0;
    var output2 = new Inventory { new SimItem("459", 999) };
    Check("partial: 堆叠满有空位仍收走", AutomationCore.TryCollectFromMachine(keg2, output2));
    Check("partial: 剩余进新格", output2.Count == 2 && output2[1]!.Stack == 1);

    // 产物大堆叠 50 个 + 成果箱 36 格全满（同类已 990）：部分收走补满 999，剩 41 留在机器
    SimMachine keg3 = new("12", MachineSpec.Keg());
    keg3.heldObject.Value = new SimItem("459", 50);
    keg3.readyForHarvest.Value = true;
    keg3.MinutesUntilReady = 0;
    var output3 = new Inventory();
    for (int i = 0; i < 36; i++)
        output3.Add(new SimItem("459", 990));
    Check("partial: 满箱部分收取", !AutomationCore.TryCollectFromMachine(keg3, output3));
    CheckEq("partial: 成果箱补满 999", output3[0]!.Stack, 999);
    CheckEq("partial: 机器剩 41", keg3.heldObject.Value!.Stack, 41);
    Check("partial: 机器仍待收", keg3.readyForHarvest.Value);
}

// ============================================================
// 第十一组：端到端循环 —— 木桶（酒→陈酿）、咖啡豆（5 换 1）
// ============================================================
{
    // 木桶：酒 1 换 1 陈酿 → 收 → 再投
    SimMachine cask = new("163", MachineSpec.Cask());
    var chest = new Inventory { new SimItem("348", 2, -26) };
    var output = new Inventory();
    Check("e2e-cask: 投酒", AutomationCore.TryFeedMachine(cask, chest, state, who));
    CheckEq("e2e-cask: 剩 1 瓶", chest[0]!.Stack, 1);
    Check("e2e-cask: 工作中不投", !AutomationCore.TryFeedMachine(cask, chest, state, who));
    cask.TickFinish();
    Check("e2e-cask: 完成能收", AutomationCore.TryCollectFromMachine(cask, output));
    CheckEq("e2e-cask: 成果进箱", output.Count, 1);
    Check("e2e-cask: 空机再投", AutomationCore.TryFeedMachine(cask, chest, state, who));
    CheckEq("e2e-cask: 酒投完", chest.Count, 0);

    // 咖啡豆完整循环：6 颗 → 投 5 剩 1 → 完成收咖啡 → 剩 1 颗不够不投
    SimMachine keg = new("12", MachineSpec.Keg());
    var chest2 = new Inventory { new SimItem("433", 6) };
    var output2 = new Inventory();
    Check("e2e-coffee: 投 5 颗", AutomationCore.TryFeedMachine(keg, chest2, state, who));
    CheckEq("e2e-coffee: 剩 1 颗", chest2[0]!.Stack, 1);
    Check("e2e-coffee: 1 颗不够不投", !AutomationCore.TryFeedMachine(keg, chest2, state, who));
    keg.TickFinish();
    Check("e2e-coffee: 收咖啡", AutomationCore.TryCollectFromMachine(keg, output2));
    Check("e2e-coffee: 产出是咖啡", output2[0]!.QualifiedItemId == "(O)395");
    Check("e2e-coffee: 剩 1 颗仍不投", !AutomationCore.TryFeedMachine(keg, chest2, state, who));
}

// ============================================================
// 第十二组：箱子 UI 异常物品 —— getOne null / 不可堆叠 / 超长列表
// ============================================================
{
    // 注意：原版 Object.getOne() 非虚且语义保证不返回 null（真实游戏物品不可能坏）。
    // 防御性 null 检查已在 ChestUiCore 里（copy != null），这里测不可堆叠物品的副本行为。
    var slots2 = ChestUiCore.ExpandToFixedSlots(new Inventory());
    var chair = new SimNonStackable("424");
    Check("weird: 不可堆叠能放", ChestUiCore.PutIntoFixedSlots(slots2, chair) == null);
    Check("weird: 放的是副本", !ReferenceEquals(slots2[0], chair));
    // 同类不可堆叠第二次 → 新格子（不能堆叠）
    Check("weird: 不可堆叠占新格", ChestUiCore.PutIntoFixedSlots(slots2, new SimNonStackable("424")) == null);
    CheckEq("weird: 两个占两格", ChestUiCore.CountItems(slots2), 2);

    // 超长列表（>36 项）：展开截断到 36
    var longList = new Inventory();
    for (int i = 0; i < 40; i++)
        longList.Add(new SimItem("348", 1, -79));
    var slots3 = ChestUiCore.ExpandToFixedSlots(longList);
    CheckEq("weird: 超长截断 36", ChestUiCore.CountItems(slots3), 36);

    // null 输入：Expand null 不崩
    Check("weird: null 展开不崩", ChestUiCore.CountItems(ChestUiCore.ExpandToFixedSlots(null!)) == 0);
}

// ============================================================
// 第十三组：投料失败还原 —— 候选顺序还原 / 兜底路径 autoLoadFrom
// ============================================================
{
    // 候选在箱子中间、投料失败（机器满）→ 箱子顺序还原
    SimMachine keg = new("12", MachineSpec.Keg());
    keg.heldObject.Value = new SimItem("459", 1); // 机器满
    var chest = new Inventory { new SimItem("348", 1, -79), new SimItem("264", 1, -75), new SimItem("262", 1) };
    Check("restore: 满机不投", !AutomationCore.TryFeedMachine(keg, chest, state, who));
    Check("restore: 箱子顺序还原", chest[0]!.QualifiedItemId == "(O)348" && chest[1]!.QualifiedItemId == "(O)264" && chest[2]!.QualifiedItemId == "(O)262");

    // 候选成功投料：顺序不还原（投的就是候选，顺序变更无影响）
    SimMachine keg2 = new("12", MachineSpec.Keg());
    var chest2 = new Inventory { new SimItem("348", 1, -79), new SimItem("433", 5) };
    var st2 = new ConsoleState();
    st2.InputPriority.Add("(O)433");
    Check("restore: 优先投豆成功", AutomationCore.TryFeedMachine(keg2, chest2, st2, who));
    // 豆被投光后压缩：剩水果
    Check("restore: 成功后顺序正常", chest2.Count == 1 && chest2[0]!.Stack == 1);
}

// ============================================================
// 第十四组：机器判定矩阵 —— IsMachine/IsConsole/CanMachineAccept 全组合
// ============================================================
{
    // IsMachine：小桶恒是；木桶/罐头瓶由配置开关控制
    SimMachine keg = new("12", MachineSpec.Keg());
    SimMachine cask = new("163", MachineSpec.Cask());
    SimMachine jar = new("15", MachineSpec.Keg());
    SimMachine other = new("100");
    Check("mat: 小桶恒算", AutomationCore.IsMachine(keg, config));
    Check("mat: 木桶开算", AutomationCore.IsMachine(cask, config));
    Check("mat: 罐头开算", AutomationCore.IsMachine(jar, config));
    Check("mat: 其他不算", !AutomationCore.IsMachine(other, config));
    Check("mat: 全关不算木桶", !AutomationCore.IsMachine(cask, new ModConfig { IncludeCasks = false, IncludePreservesJar = false }));
    Check("mat: 全关不算罐头", !AutomationCore.IsMachine(jar, new ModConfig { IncludeCasks = false, IncludePreservesJar = false }));
    Check("mat: 关木桶开罐头", AutomationCore.IsMachine(jar, new ModConfig { IncludeCasks = false, IncludePreservesJar = true }));

    // IsConsole：识别电脑 ID、不误判机器
    var consoleObj = new SimMachine(AutomationCore.ConsoleUnqualified);
    Check("mat: 电脑识别", AutomationCore.IsConsole(consoleObj));
    Check("mat: 小桶不是电脑", !AutomationCore.IsConsole(keg));
    Check("mat: null 不是电脑", !AutomationCore.IsConsole(null));
    Check("mat: null 不是机器", !AutomationCore.IsMachine(null, config));

    // CanMachineAccept：罐头瓶严格限定水果/蔬菜（咖啡豆不适用，与规则匹配一致）
    Check("mat: 罐头吃水果", AutomationCore.CanMachineAccept(jar, new SimItem("348", 1, -79)));
    Check("mat: 罐头不吃咖啡豆", !AutomationCore.CanMachineAccept(jar, new SimItem("433", 5)));
    Check("mat: 罐头不吃蜂蜜", !AutomationCore.CanMachineAccept(jar, new SimItem("340", 1, -81)));
    // 小桶吃蜂蜜（酿蜂蜜酒）但不吃成品酒/饮品
    Check("mat: 小桶吃蜂蜜", AutomationCore.CanMachineAccept(keg, new SimItem("340", 1, -81)));
    Check("mat: 小桶不吃蛋黄酱", !AutomationCore.CanMachineAccept(keg, new SimItem("434", 1, -27)));
    // 工具/非 Object → false
    Check("mat: 工具不吃", !AutomationCore.CanMachineAccept(keg, new SimTool()));
    Check("mat: null 物品不吃", !AutomationCore.CanMachineAccept(keg, null));
    // 未知机器（fallthrough 分支）
    Check("mat: 未知机器按负类别", AutomationCore.CanMachineAccept(other, new SimItem("348", 1, -79)));
    Check("mat: 未知机器正类别不吃", !AutomationCore.CanMachineAccept(other, new SimItem("348", 1, 0)));
}

// ============================================================
// 第十五组：优先级 —— 空优先级/全不匹配/多个匹配去重
// ============================================================
{
    // 空优先级列表：与 null 相同 → 按箱内顺序投（第一项水果）
    SimMachine keg = new("12", MachineSpec.Keg());
    var chest = new Inventory { new SimItem("348", 1, -79), new SimItem("433", 5) };
    Check("prio2: 空列表按箱内顺序", AutomationCore.TryFeedMachine(keg, chest, new ConsoleState(), who));
    Check("prio2: 投了水果(第一项)", chest.Count == 1 && chest[0]!.QualifiedItemId == "(O)433");

    // 优先级多目标匹配：命中同一物品只出现一次（不会重复投同一个）
    SimMachine keg2 = new("12", MachineSpec.Keg());
    var chest2 = new Inventory { new SimItem("348", 2, -79), new SimItem("433", 5) };
    var st2 = new ConsoleState();
    st2.InputPriority.AddRange(new[] { "(O)348", "(O)348", "(O)433" }); // 重复条目
    Check("prio2: 重复优先级只投一次", AutomationCore.TryFeedMachine(keg2, chest2, st2, who));
    CheckEq("prio2: 水果扣 1", chest2[0]!.Stack, 1);
    Check("prio2: 豆未动", chest2.Count == 2 && chest2[1]!.Stack == 5);

    // 优先级首项不可投（数量不足）→ 回退到下一项
    SimMachine keg3 = new("12", MachineSpec.Keg());
    var chest3 = new Inventory { new SimItem("433", 3), new SimItem("348", 1, -79) };
    var st3 = new ConsoleState();
    st3.InputPriority.Add("(O)433");
    Check("prio2: 优先不可投回退", AutomationCore.TryFeedMachine(keg3, chest3, st3, who));
    CheckEq("prio2: 回退投了水果", chest3[0]!.Stack, 3); // 豆没动(3)，水果被投走后列表压缩
}

// ============================================================
// 第十六组：收成品 —— 未就绪边界 / 原版 addItemToThisInventoryList 语义
// ============================================================
{
    // 产物堆叠、ready 但 MinutesUntilReady > 0 → 不收（倒计时未到）
    SimMachine keg = new("12", MachineSpec.Keg());
    keg.heldObject.Value = new SimItem("459", 10);
    keg.readyForHarvest.Value = true;
    keg.MinutesUntilReady = 1; // 还没到 0
    var output = new Inventory();
    Check("collect2: 倒计时未到不收", !AutomationCore.TryCollectFromMachine(keg, output));
    Check("collect2: 机器保持就绪", keg.readyForHarvest.Value && keg.heldObject.Value != null);

    // 原版 addItemToThisInventoryList 语义：output 同类近满(998) + 收 5 → 补满 999、机器清空。
    // （原版会把 998+5 直接覆盖为 999 丢 4 个——物品丢失 bug；模拟器复刻该语义以对齐游戏内行为。）
    SimMachine keg2 = new("12", MachineSpec.Keg());
    keg2.heldObject.Value = new SimItem("459", 5);
    keg2.readyForHarvest.Value = true;
    keg2.MinutesUntilReady = 0;
    var output2 = new Inventory { new SimItem("459", 998) };
    Check("collect2: 近满堆叠覆盖为 999", AutomationCore.TryCollectFromMachine(keg2, output2));
    CheckEq("collect2: 成果箱补满 999", output2[0]!.Stack, 999);
    Check("collect2: 机器清空", keg2.heldObject.Value == null && !keg2.readyForHarvest.Value);

    // 同类满堆叠(999) + 收 3：原版开新槽放下全部 → 机器清空、成果箱两格
    SimMachine keg3 = new("12", MachineSpec.Keg());
    keg3.heldObject.Value = new SimItem("459", 3);
    keg3.readyForHarvest.Value = true;
    keg3.MinutesUntilReady = 0;
    var output3 = new Inventory { new SimItem("459", 999) };
    Check("collect2: 满堆叠开新槽全收", AutomationCore.TryCollectFromMachine(keg3, output3));
    Check("collect2: 新槽放了 3", output3.Count == 2 && output3[1]!.Stack == 3);
    Check("collect2: 机器清空2", keg3.heldObject.Value == null);
}

// ============================================================
// 第十七组：箱子 UI 数据桥 —— 部分堆叠溢出返回剩余、getOne null 防御
// ============================================================
{
    // 同类堆叠到 999 上限：原版 addToStack 在满堆叠时返回全部（保持 999）——放箱子的
    // PutIntoFixedSlots 遇到满堆叠会尝试新空位（getOne 副本），所以 3 个能放下不返回剩余。
    var slots = ChestUiCore.ExpandToFixedSlots(new Inventory());
    slots[0] = new SimItem("433", 999);
    var extra = new SimItem("433", 3);
    var leftover = ChestUiCore.PutIntoFixedSlots(slots, extra);
    Check("ui2: 满堆叠放新格全放下", leftover == null);
    CheckEq("ui2: 原格保持 999", slots[0]!.Stack, 999);
    CheckEq("ui2: 新格 3 个", slots[1]!.Stack, 3);
    Check("ui2: 放的是副本", !ReferenceEquals(slots[1], extra));

    // getOne 抛异常的物品（mod 物品/损坏数据）：按放不下处理，不崩箱子 UI
    var slots2 = ChestUiCore.ExpandToFixedSlots(new Inventory());
    var broken = new SimBrokenOne("348", 2);
    var leftover2 = ChestUiCore.PutIntoFixedSlots(slots2, broken);
    Check("ui2: getOne 异常返回原物不崩", ReferenceEquals(leftover2, broken));
    CheckEq("ui2: 箱子保持空", ChestUiCore.CountItems(slots2), 0);

    // 堆叠优先级：先堆叠同类（即使后面有空位）
    var slots3 = ChestUiCore.ExpandToFixedSlots(new Inventory());
    slots3[0] = new SimItem("433", 5);
    slots3[2] = new SimItem("433", 5);
    slots3[5] = new SimItem("348", 1, -79);
    Check("ui2: 堆叠到第一个同类", ChestUiCore.PutIntoFixedSlots(slots3, new SimItem("433", 2)) == null);
    CheckEq("ui2: 格0 成 7", slots3[0]!.Stack, 7);
    CheckEq("ui2: 格2 未动", slots3[2]!.Stack, 5);
    CheckEq("ui2: 空位未占用", slots3[1], null);
}

// ============================================================
// 第十八组：存档模型 —— 字段形状与默认值（真实序列化往返由无头 SMAPI
// 集成测试覆盖：SMAPI 的 SaveDataToolkit 在无 SMAPI 环境加载不了）
// ============================================================
{
    var sd = new SaveData();
    Check("save: 空存档默认字典", sd.Consoles != null && sd.Consoles.Count == 0);

    var cs = new ConsoleSave();
    Check("save: 新电脑空列表", cs.FruitItems.Count == 0 && cs.WineItems.Count == 0 && cs.InputPriority.Count == 0);

    var st = new ConsoleState();
    st.InputPriority.Add("(O)433");
    Check("save: 优先级可加", st.InputPriority.Count == 1);
    Check("save: 状态默认空优先级", new ConsoleState().InputPriority.Count == 0);
}

Console.WriteLine($"\n总计: PASS={pass} FAIL={fails}");
return fails == 0 ? 0 : 1;
