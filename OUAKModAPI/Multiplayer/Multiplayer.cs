using System;
using System.Collections.Generic;

namespace OUAKModAPI
{
    /// <summary>One player in an online session.</summary>
    public sealed class SessionPlayer
    {
        /// <summary>The session's player number (0 = the host).</summary>
        public byte Id;
        public string Name;
        public bool IsHost, IsYou;
        /// <summary>Their SteamID (0 when they play without Steam).</summary>
        public ulong SteamId;
        /// <summary>The host checked their Steam ticket.</summary>
        public bool Verified;
    }

    /// <summary>What an online session mod offers other mods (OUAK Multiplayer implements it).</summary>
    public interface IMultiplayer
    {
        bool InSession { get; }
        bool IsHost { get; }
        byte MyId { get; }
        IReadOnlyList<SessionPlayer> Players { get; }
        /// <summary>Sends your mod's data to the other players (through the host). <paramref name="to"/> 255 = everyone.</summary>
        void SendModMessage(string channel, byte[] data, byte to = 255);
    }

    /// <summary>
    /// Online play for other mods, when an online mod (OUAK Multiplayer) is installed: who's in the session, and a
    /// message channel per mod to keep your own feature in sync between players.
    /// <code>
    /// Multiplayer.Received += (from, channel, data) =&gt; { if (channel == "mymod") ... };
    /// if (Multiplayer.InSession) Multiplayer.Send("mymod", bytes);
    /// </code>
    /// Messages are small UDP packets (keep them under ~1000 bytes) and may be lost: resend what matters.
    /// </summary>
    public static class Multiplayer
    {
        /// <summary>The online mod, or null when none is installed.</summary>
        public static IMultiplayer Current { get; set; }

        public static bool InSession => Current?.InSession == true;
        public static bool IsHost => Current?.IsHost == true;
        public static IReadOnlyList<SessionPlayer> Players => Current?.Players ?? Array.Empty<SessionPlayer>();

        /// <summary>Another player's mod sent you data: (their player id, channel, data).</summary>
        public static event Action<byte, string, byte[]> Received;

        /// <summary>Sends data on your channel; false when not in a session.</summary>
        public static bool Send(string channel, byte[] data, byte to = 255)
        {
            if (!InSession || string.IsNullOrEmpty(channel) || data == null) return false;
            Current.SendModMessage(channel, data, to);
            return true;
        }

        /// <summary>The online mod calls this when mod data arrives.</summary>
        public static void Deliver(byte from, string channel, byte[] data)
        {
            var e = Received;
            if (e == null) return;
            foreach (var d in e.GetInvocationList())
                try { ((Action<byte, string, byte[]>)d)(from, channel, data); }
                catch (Exception ex) { GameEvents.Report(d, ex); }
        }
    }
}
