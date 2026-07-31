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

Console.WriteLine($"\n总计: PASS={pass} FAIL={fails}");
return fails == 0 ? 0 : 1;
