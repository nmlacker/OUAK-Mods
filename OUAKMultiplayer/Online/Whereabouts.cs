using System;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>What a player is doing, as their own game reports it (one byte in every State packet).</summary>
    internal enum Activity : byte
    {
        Unknown = 0,
        /// <summary>The title or save screens (no save loaded).</summary>
        Menus = 1,
        Loading = 2,
        Ship = 3,
        Plaza = 4,
        /// <summary>At a fan's stage screen while everyone gets ready for a stage (PlazaFlow).</summary>
        GettingReady = 5,
        Playing = 6,
        /// <summary>Finished their run while others are still playing it.</summary>
        Spectating = 7,
        /// <summary>The run is over: results.</summary>
        Results = 8,
    }

    /// <summary>
    /// Where everyone is: the lobby (the Prince's Ship or a plaza), getting ready for a stage, playing one,
    /// spectating it (finished, the others still going), its results, loading, or the menus. Yours is worked out
    /// here and sent with your State packets; everyone else's comes from theirs (Peer.Activity).
    /// </summary>
    internal static class Whereabouts
    {
        public static Activity Local()
        {
            try
            {
                if (GameRefs.Main != null)
                {
                    int map = Ghosts.CurrentMapKey;
                    if (map == 0) return Activity.Loading;
                    if (SyncRun.RunMap == map && SyncRun.Phase != RunPhase.None)
                    {
                        if (SyncRun.StandingsFinal) return Activity.Results;
                        if (SyncRun.MyResult != null) return Activity.Spectating;
                    }
                    return Activity.Playing;
                }
                if (PlazaFlow.Now != PlazaFlow.Phase.Idle && PlazaFlow.MyState is PlazaFlow.OnScreen or PlazaFlow.IsReady) return Activity.GettingReady;
                int hub = HubPresence.HubKey;
                if (hub == HubPresence.ShipKey) return Activity.Ship;
                if (hub != 0) return Activity.Plaza;
                string scene = SceneLoader.ActiveScene;
                if (scene == "SelectData" || scene == "Title" || !GameRefs.InGameSession) return Activity.Menus;
                return Activity.Loading;
            }
            catch { return Activity.Unknown; }
        }

        /// <summary>"On the Prince's Ship", "In the Edo plaza", "Playing As Large As Possible 1", "Spectating …", …</summary>
        public static string Text(Activity a, int mapKey, int hubKey)
        {
            switch (a)
            {
                case Activity.Menus: return "In the menus";
                case Activity.Loading: return "Loading…";
                case Activity.Ship: return Cap(HubPresence.PlaceName(HubPresence.ShipKey));
                case Activity.Plaza: return hubKey != 0 ? Cap(HubPresence.PlaceName(hubKey)) : "In a plaza";
                case Activity.GettingReady: return PlazaFlow.Now != PlazaFlow.Phase.Idle ? $"Getting ready for {PlazaFlow.StageNow}" : "Getting ready for a stage";
                case Activity.Playing: return mapKey != 0 ? $"Playing {Votes.StageName(mapKey)}" : "Playing";
                case Activity.Spectating: return mapKey != 0 ? $"Spectating {Votes.StageName(mapKey)}" : "Spectating";
                case Activity.Results: return mapKey != 0 ? $"Results of {Votes.StageName(mapKey)}" : "Results";
                default: return hubKey != 0 ? Cap(HubPresence.PlaceName(hubKey)) : mapKey != 0 ? $"In {Votes.StageName(mapKey)}" : "";
            }
        }

        /// <summary>Shorter, for the player bar's second line.</summary>
        public static string Short(Activity a, int mapKey, int hubKey) => a switch
        {
            Activity.Menus => "in the menus",
            Activity.Loading => "loading…",
            Activity.Ship => "on the ship",
            Activity.Plaza => hubKey != 0 ? HubPresence.PlaceName(hubKey).Replace("in the ", "") : "in a plaza",
            Activity.GettingReady => "getting ready",
            Activity.Playing => mapKey != 0 && mapKey != Ghosts.CurrentMapKey ? "in " + Votes.StageName(mapKey) : "playing",
            Activity.Spectating => "spectating",
            Activity.Results => "results",
            _ => hubKey != 0 ? HubPresence.PlaceName(hubKey) : "",
        };

        public static string Of(Peer p)
        {
            float now = Time.unscaledTime;
            bool fresh = p.HasState && now - p.StateAt < 3f;
            bool inHub = p.HubKey != 0 && p.HubAt > 0f && now - p.HubAt < 3f;
            if (!fresh && !inHub) return p.LastHeard > 0f ? "No news" : "Connecting…";
            return Text(fresh ? p.Activity : Activity.Unknown, fresh ? p.MapKey : 0, inHub ? p.HubKey : 0);
        }

        public static string Mine() => Text(Local(), Ghosts.CurrentMapKey, HubPresence.HubKey);

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
