using System.Collections.Generic;
using StardewValley;

namespace ShedConsoleComputer
{
    /// <summary>一台电脑的运行状态（不进存档的部分是缓存，进存档的部分走 <see cref="ConsoleSave"/>）。</summary>
    public class ConsoleState
    {
        /// <summary>投料优先级：QualifiedItemId 列表，越靠前越先投。</summary>
        public List<string> InputPriority = new();
    }

    /// <summary>单台电脑的存档数据。</summary>
    public class ConsoleSave
    {
        public List<Item> FruitItems { get; set; } = new();

        public List<Item> WineItems { get; set; } = new();

        public List<string> InputPriority { get; set; } = new();
    }

    /// <summary>整个 Mod 的存档数据（shed-console-data）。</summary>
    public class SaveData
    {
        public Dictionary<string, ConsoleSave> Consoles { get; set; } = new();
    }
}
