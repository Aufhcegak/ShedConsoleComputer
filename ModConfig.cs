namespace ShedConsoleComputer
{
    /// <summary>Mod 配置，可在游戏目录 <c>Mods/ShedConsoleComputer/config.json</c> 或 GMCM 中修改。</summary>
    public class ModConfig
    {
        /// <summary>小桶空时，按优先级从投料箱自动投放原料。</summary>
        public bool AutoFeed { get; set; } = true;

        /// <summary>机器加工完成时，自动把产物收进成果箱。</summary>
        public bool AutoCollect { get; set; } = true;

        /// <summary>是否把木桶（陈酿）纳入自动化。</summary>
        public bool IncludeCasks { get; set; } = true;

        /// <summary>是否把罐头瓶纳入自动化。</summary>
        public bool IncludePreservesJar { get; set; } = false;
    }
}
