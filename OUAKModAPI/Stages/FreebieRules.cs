using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppApp.KatamariSin;

namespace OUAKModAPI
{
    /// <summary>
    /// Which freebies actually do something, and where. Measured in game (2026-11-06) and read from the game's code:
    /// <list type="bullet">
    /// <item><b>Story stages:</b> Drink, Magnet, Pickel, Sonar and Timer, and their Specials (SP_*: the same effect for
    /// twice as long; SP_Magnet also pulls from further, x3 instead of x2.2), plus Random (the game rolls one of those
    /// ten). Every other freebie (the battle items and Sunglasses) is used up the moment you use it, with only a
    /// cosmetic effect: its real code (MultiplayGameManager.MultiplayUseItem) runs only in battles.</item>
    /// <item><b>Battles</b> (Sports Katamari / training matches): the items in the game's battle table
    /// (MultiplaySetting.dicKingBoxItemTypeToRewardType: Tarai, Spider, Bubble, Mushroom, Hand, Shoes, Crown, their
    /// Specials, Magnet and SP_Magnet, Drink and SP_Drink as the Rocket), plus Random.</item>
    /// <item><b>Nowhere in the game itself:</b> Hoop, Smoke, Dice, SP_Dice and Sunglass (Sunglass is only a King Box reward).
    /// The API's <see cref="UnusedFreebies"/> gives Smoke and Hoop an effect in battles and both Dice one everywhere
    /// (registered here); Sunglass stays as it is.</item>
    /// </list>
    /// A mod that gives a freebie an effect where the game has none says so with <see cref="Register"/>; menus,
    /// random picks and <see cref="StageControl.GiveFreebie"/> then treat it as working.
    /// </summary>
    public static class FreebieRules
    {
        private static readonly HashSet<eInstageItemType> Story = new()
        {
            eInstageItemType.Drink, eInstageItemType.Magnet, eInstageItemType.Pickel, eInstageItemType.Sonar, eInstageItemType.Timer,
            eInstageItemType.SP_Drink, eInstageItemType.SP_Magnet, eInstageItemType.SP_Pickel, eInstageItemType.SP_Sonar, eInstageItemType.SP_Timer,
            eInstageItemType.RandomItem,
        };

        // The game's battle table as measured; read live from MultiplaySetting when it's there.
        private static readonly eInstageItemType[] BattleKnown =
        {
            eInstageItemType.Tarai, eInstageItemType.Mushroom, eInstageItemType.Spider, eInstageItemType.Bubble, eInstageItemType.Hand,
            eInstageItemType.Shoes, eInstageItemType.Crown, eInstageItemType.SP_Tarai, eInstageItemType.SP_Spider, eInstageItemType.SP_Bubble,
            eInstageItemType.SP_Mushroom, eInstageItemType.SP_Crown, eInstageItemType.Magnet, eInstageItemType.SP_Magnet,
            eInstageItemType.Drink, eInstageItemType.SP_Drink,
        };

        private static HashSet<eInstageItemType> _battle;
        private static readonly Dictionary<eInstageItemType, string> _addedStory = new(), _addedBattle = new();

        /// <summary>The freebies that do something in battles (the game's battle table, plus Random and registered ones).</summary>
        private static HashSet<eInstageItemType> Battle
        {
            get
            {
                if (_battle != null) return _battle;
                var set = new HashSet<eInstageItemType>();
                try
                {
                    var table = MultiplaySetting.dicKingBoxItemTypeToRewardType;
                    if (table != null) foreach (var kv in table) set.Add(kv.Key);
                }
                catch { }
                if (set.Count == 0) foreach (var i in BattleKnown) set.Add(i);
                set.Add(eInstageItemType.RandomItem);
                return _battle = set;
            }
        }

        /// <summary>
        /// Declares that a mod gives <paramref name="item"/> an effect where the game has none (in story stages, or
        /// in battles with <paramref name="battle"/>). <paramref name="what"/> describes it for menus. Dispose to undo.
        /// </summary>
        public static IDisposable Register(eInstageItemType item, string what, bool battle = false)
        {
            var map = battle ? _addedBattle : _addedStory;
            map[item] = what ?? "";
            return new ModMenu.Remover(() => map.Remove(item));
        }

        /// <summary>True when <paramref name="item"/> does something in story stages (the game's own or a registered effect).</summary>
        public static bool WorksInStory(eInstageItemType item) => Story.Contains(item) || _addedStory.ContainsKey(item);

        /// <summary>True when <paramref name="item"/> does something in battles.</summary>
        public static bool WorksInBattle(eInstageItemType item) => Battle.Contains(item) || _addedBattle.ContainsKey(item);

        /// <summary>True when <paramref name="item"/> does something in the stage being played (story when not in a stage).</summary>
        public static bool WorksHere(eInstageItemType item) => GameRefs.IsMultiplay ? WorksInBattle(item) : WorksInStory(item);

        /// <summary>The freebies that do something in story stages (or battles), in the game's order.</summary>
        public static eInstageItemType[] Usable(bool battle = false) =>
            StageControl.Freebies.Where(i => battle ? WorksInBattle(i) : WorksInStory(i)).ToArray();

        /// <summary>A mod's description of an effect it gives (empty for the game's own).</summary>
        public static string AddedEffect(eInstageItemType item, bool battle = false) =>
            (battle ? _addedBattle : _addedStory).TryGetValue(item, out var s) ? s : "";

        /// <summary>Why <paramref name="item"/> does nothing here, for messages.</summary>
        public static string WhyNot(eInstageItemType item) =>
            item is eInstageItemType.Sunglass
                ? $"{StageControl.Pretty(item)} has no effect in this game (unfinished)."
                : item is eInstageItemType.Hoop or eInstageItemType.Smoke
                ? $"{StageControl.Pretty(item)} acts against other princes, so it only works in battles (here: its look only)."
                : GameRefs.IsMultiplay
                    ? $"{StageControl.Pretty(item)} does nothing in battles."
                    : item is eInstageItemType.Tarai or eInstageItemType.Spider or eInstageItemType.Bubble or eInstageItemType.Hand or eInstageItemType.Smoke
                        or eInstageItemType.SP_Tarai or eInstageItemType.SP_Spider or eInstageItemType.SP_Bubble
                        ? $"{StageControl.Pretty(item)} acts on other princes, so it only works in battles (Sports Katamari)."
                        : $"{StageControl.Pretty(item)} only works in battles (Sports Katamari).";
    }
}
