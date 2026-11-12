using System;

namespace OUAKMultiplayer.Leaderboards
{
    /// <summary>
    /// Cookies for a session run, Beautiful Katamari's way as the user set it (2026-11-12):
    /// <list type="bullet">
    /// <item>only runs played in a Multiplayer session against at least one other real player
    /// (<see cref="LeaderboardRules.EarnsCookies"/>); a player who left the race (DQ) gets nothing;</item>
    /// <item>20 for taking part;</item>
    /// <item>1st place +30, 2nd +20, 3rd +10, 4th to 8th +0;</item>
    /// <item>a special bonus of +10 for 1st to 3rd place;</item>
    /// <item>a random bonus of 0 to 50, worked out from the run's id, so the same run always gets the same bonus.</item>
    /// </list>
    /// A shared place (a full tie) earns that place's cookies for everyone in it.
    /// So: 1st 60, 2nd 50, 3rd 40, 4th to 8th 20, each plus 0-50.
    /// </summary>
    public sealed class BeautifulKatamariCookies : ICookieRule
    {
        public const int Participation = 20, SpecialBonus = 10, RandomMax = 50;

        public string Id => "beautiful-katamari-1";

        public static int PlaceBonus(int place) => place switch { 1 => 30, 2 => 20, 3 => 10, _ => 0 };

        /// <summary>Everything but the random bonus.</summary>
        public static int Fixed(RunRecord run)
        {
            if (!LeaderboardRules.EarnsCookies(run) || run.Disqualified) return 0;
            int c = Participation + PlaceBonus(run.Place);
            if (run.Place >= 1 && run.Place <= 3) c += SpecialBonus;
            return c;
        }

        public int CookiesFor(RunRecord run)
        {
            int c = Fixed(run);
            return c == 0 ? 0 : c + LocalRandomBonus(run);
        }

        /// <summary>The random bonus: 0-50 from the run's id, the same every time for one run.</summary>
        public static int LocalRandomBonus(RunRecord run)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in run.EventId ?? "") h = (h ^ ch) * 16777619;
                return (int)(h % (RandomMax + 1));
            }
        }
    }
}
