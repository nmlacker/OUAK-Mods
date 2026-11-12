using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppSteamworks;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Versus modes online (Katamari Ball, Race, Target) use the game's own networking, limited to players who run
    /// OUAK Multiplayer (the user, 2026-11-06). The mod's own UDP sessions are only for the single-player stages.
    /// <para>What the game's online is on PC (traced): rooms are Steam Matchmaking lobbies (NetworkManager:
    /// SteamMatchmaking.CreateLobby(public, 4), RequestLobbyList with no filter, JoinLobby; lobby data "roomName",
    /// "roomParam" (the room as JSON), "BlockList", "IgnoreSteamID") and the match traffic goes through Steam
    /// Networking Messages. A player's id in a match is the low 32 bits of their SteamID (NetworkManager.IsConnectUser).
    /// Joining: the joiner's MultiplayController.IsJoinRoom sends JoinRequest (14) and re-sends it every 2 s, up to
    /// 10 times; the host's MultiplayController.Update accepts unless the room is closed, full, or the transport's
    /// IsUserBlocked(id) says yes, in which case it sends JoinDeny (16).</para>
    /// <para>The gate, all through Steam's lobby servers and the game's own join step:</para>
    /// <list type="bullet">
    /// <item>A lobby created with the mod gets lobby data <see cref="Key"/> = <see cref="Tag"/> (NetworkManager.OnCreatedLobby).</item>
    /// <item>Everyone with the mod marks themselves in a lobby they enter with lobby member data Key = Tag
    /// (OnLobbyEnter), and holds their first JoinRequest <see cref="JoinHold"/> s so the mark reaches the host first.</item>
    /// <item>Room searches ask Steam only for lobbies with Key = Tag (a string filter before the game's RequestLobbyList),
    /// so with the mod you only see and random-match mod lobbies.</item>
    /// <item>A mod host refuses anyone in its lobby without the mark: MultiplayRemoteHost.IsUserBlocked answers yes, so
    /// the game itself sends JoinDeny.</item>
    /// <item>A mod player who ends up in a lobby without the mark (a friend's invite to a game without the mod) fails the
    /// join through the game's own error path (MultiplayController.SetError(joinRoomReasonBlock)).</item>
    /// </list>
    /// Players without the mod can still see mod lobbies in their (unfiltered) room list, but can't get in.
    /// </summary>
    internal static class VersusGate
    {
        public const string Key = "ouak_mod";
        /// <summary>Changes when mod lobbies stop being compatible with older versions of this mod.</summary>
        public const string Tag = "ouak-versus-1";
        public const float JoinHold = 2f;

        public static bool Active { get; private set; }
        public static int Refused { get; private set; }
        public static int Marked { get; private set; }
        public static string Last { get; private set; } = "";

        private static ulong _enteredLobby;
        private static float _enteredAt = -1f;
        private static bool _leftForeign;
        private static NetworkManager _net;
        private static readonly HashSet<ulong> LoggedRefusals = new();

        public static void Init(HarmonyLib.Harmony harmony)
        {
            int ok = 0;
            ok += Patch(harmony, typeof(NetworkManager), nameof(NetworkManager.OnCreatedLobby), null, nameof(AfterCreatedLobby));
            ok += Patch(harmony, typeof(NetworkManager), nameof(NetworkManager.OnLobbyEnter), null, nameof(AfterLobbyEnter));
            ok += Patch(harmony, typeof(NetworkManager), nameof(NetworkManager.SearchRoom), nameof(BeforeSearchRoom), null);
            ok += Patch(harmony, typeof(MultiplayRemoteHost), nameof(MultiplayRemoteHost.IsUserBlocked), nameof(BeforeIsUserBlocked), null);
            ok += Patch(harmony, typeof(MultiplayController), nameof(MultiplayController.IsJoinRoom), nameof(BeforeIsJoinRoom), null);
            Active = ok == 5;
            MelonLogger.Msg($"[Versus] mod-only lobbies {(Active ? "on" : $"PARTLY on ({ok} of 5 hooks)")}: Steam lobby data {Key} = {Tag}.");
        }

        private static int Patch(HarmonyLib.Harmony h, Type type, string method, string prefix, string postfix)
        {
            try
            {
                h.Patch(AccessTools.Method(type, method),
                    prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(VersusGate), prefix)) : null,
                    postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(VersusGate), postfix)) : null);
                return 1;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Versus] couldn't hook {type.Name}.{method}: {e.Message}");
                return 0;
            }
        }

        // ---------------------------------------------------------------- Steam lobby helpers

        private static NetworkManager Net
        {
            get
            {
                try { if (_net == null) _net = UnityEngine.Object.FindObjectOfType<NetworkManager>(); }
                catch { _net = null; }
                return _net;
            }
        }

        /// <summary>The Steam lobby the game is in (0 when none).</summary>
        public static ulong CurrentLobby
        {
            get
            {
                try { return Net?.m_SteamLobbyID.m_SteamID ?? 0UL; }
                catch { return 0UL; }
            }
        }

        private static ulong MySteamId
        {
            get { try { return SteamUser.GetSteamID().m_SteamID; } catch { return 0UL; } }
        }

        private static bool IOwn(ulong lobby)
        {
            try { return lobby != 0 && SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID == MySteamId; }
            catch { return false; }
        }

        public static string LobbyMark(ulong lobby)
        {
            try { return lobby == 0 ? "" : SteamMatchmaking.GetLobbyData(new CSteamID(lobby), Key) ?? ""; }
            catch { return ""; }
        }

        public static string MemberMark(ulong lobby, ulong member)
        {
            try { return SteamMatchmaking.GetLobbyMemberData(new CSteamID(lobby), new CSteamID(member), Key) ?? ""; }
            catch { return ""; }
        }

        private static string NameOf(ulong id)
        {
            try { return SteamFriends.GetFriendPersonaName(new CSteamID(id)); }
            catch { return id.ToString(); }
        }

        /// <summary>Marks you in the lobby (member data), and the lobby itself when it's yours.</summary>
        private static void MarkMe(ulong lobby, bool lobbyToo)
        {
            if (lobby == 0) return;
            var id = new CSteamID(lobby);
            SteamMatchmaking.SetLobbyMemberData(id, Key, Tag);
            if (lobbyToo) SteamMatchmaking.SetLobbyData(id, Key, Tag);
            Marked++;
        }

        // ---------------------------------------------------------------- hooks

        private static void AfterCreatedLobby(NetworkManager __instance)
        {
            try
            {
                _net = __instance;
                ulong lobby = CurrentLobby;
                if (lobby == 0 || !IOwn(lobby)) return;
                MarkMe(lobby, true);
                _enteredLobby = lobby;
                _enteredAt = Time.unscaledTime;
                _leftForeign = false;
                Last = $"created lobby {lobby}: marked {Key}={LobbyMark(lobby)}";
                MelonLogger.Msg($"[Versus] {Last}");
            }
            catch (Exception e) { MelonLogger.Warning($"[Versus] marking your lobby: {e.Message}"); }
        }

        private static void AfterLobbyEnter(NetworkManager __instance)
        {
            try
            {
                _net = __instance;
                ulong lobby = CurrentLobby;
                if (lobby == 0) return;
                bool mine = IOwn(lobby);
                MarkMe(lobby, mine);
                if (lobby != _enteredLobby) { _enteredLobby = lobby; _enteredAt = Time.unscaledTime; _leftForeign = false; }
                string mark = LobbyMark(lobby);
                Last = $"entered lobby {lobby} ({(mine ? "yours" : "owner " + NameOf(SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID))}): lobby mark '{mark}'";
                MelonLogger.Msg($"[Versus] {Last}");
            }
            catch (Exception e) { MelonLogger.Warning($"[Versus] entering a lobby: {e.Message}"); }
        }

        /// <summary>Only mod lobbies (same Tag) come back from Steam; the game's RequestLobbyList runs right after.</summary>
        private static void BeforeSearchRoom()
        {
            try { SteamMatchmaking.AddRequestLobbyListStringFilter(Key, Tag, ELobbyComparison.k_ELobbyComparisonEqual); }
            catch (Exception e) { MelonLogger.Warning($"[Versus] room search filter: {e.Message}"); }
        }

        /// <summary>
        /// The host's join check (MultiplayController.Update asks the transport before accepting a JoinRequest): in
        /// your own lobby, a member without the mod's mark is "blocked", so the game sends them JoinDeny.
        /// </summary>
        private static bool BeforeIsUserBlocked(uint id, ref bool __result)
        {
            try
            {
                ulong lobby = CurrentLobby;
                if (lobby == 0 || !IOwn(lobby)) return true;
                var cl = new CSteamID(lobby);
                int n = SteamMatchmaking.GetNumLobbyMembers(cl);
                for (int i = 0; i < n; i++)
                {
                    ulong member = SteamMatchmaking.GetLobbyMemberByIndex(cl, i).m_SteamID;
                    if ((uint)member != id || member == MySteamId) continue;
                    if (MemberMark(lobby, member) == Tag) return true;   // a mod player: the game decides as usual
                    __result = true;
                    Refused++;
                    if (LoggedRefusals.Add(member))
                    {
                        Last = $"refused {NameOf(member)}: no OUAK Multiplayer ({Key}='{MemberMark(lobby, member)}')";
                        MelonLogger.Msg($"[Versus] {Last}");
                        ModTasks.Toast($"{NameOf(member)} tried to join without OUAK Multiplayer: refused.");
                    }
                    return false;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Versus] join check: {e.Message}"); }
            return true;
        }

        /// <summary>
        /// Joining: hold the first JoinRequest a moment after entering the lobby (so the host already sees your mark),
        /// and refuse to join a lobby that isn't a mod lobby (the game's own failed-join path).
        /// </summary>
        private static bool BeforeIsJoinRoom(MultiplayController __instance, ref bool __result)
        {
            try
            {
                ulong lobby = CurrentLobby;
                if (lobby == 0 || IOwn(lobby)) return true;
                if (LobbyMark(lobby) != Tag)
                {
                    if (!_leftForeign)
                    {
                        _leftForeign = true;
                        Last = $"lobby {lobby} has no OUAK Multiplayer mark: not joining";
                        MelonLogger.Msg($"[Versus] {Last}");
                        ModTasks.Toast("That room isn't running OUAK Multiplayer: versus play is only with other OUAK Multiplayer players.", true);
                        __instance.SetError(NetworkErrorReason.joinRoomReasonBlock);
                    }
                    __result = false;
                    return false;
                }
                if (lobby == _enteredLobby && _enteredAt >= 0f && Time.unscaledTime - _enteredAt < JoinHold)
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Versus] joining: {e.Message}"); }
            return true;
        }

        /// <summary>For the Online tab: the lobby you're in and who in it runs the mod.</summary>
        public static string Describe()
        {
            ulong lobby = CurrentLobby;
            if (lobby == 0) return $"not in a game lobby; {Refused} refused so far";
            try
            {
                var cl = new CSteamID(lobby);
                int n = SteamMatchmaking.GetNumLobbyMembers(cl);
                var parts = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    ulong m = SteamMatchmaking.GetLobbyMemberByIndex(cl, i).m_SteamID;
                    parts.Add($"{NameOf(m)} {(MemberMark(lobby, m) == Tag ? "mod" : "NO MOD")}");
                }
                return $"lobby {(IOwn(lobby) ? "(yours)" : "")} mark '{LobbyMark(lobby)}': {string.Join(", ", parts)}; {Refused} refused";
            }
            catch (Exception e) { return "lobby: " + e.Message; }
        }
    }
}
