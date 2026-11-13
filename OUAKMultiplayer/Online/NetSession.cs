using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    internal sealed class Peer
    {
        public byte Id;
        public string Name = "";
        /// <summary>Host side only: the name they asked for (Name gets a number when someone already has it).</summary>
        public string AskedName = "";
        /// <summary>Who they are (their Steam account, or an install id without Steam); the host keys players by it.</summary>
        public PlayerIdentity Identity;
        /// <summary>The host checked their Steam session ticket (Steam signed it for this account and this game).</summary>
        public bool Verified;
        /// <summary>What the check said ("verified by Steam", "no Steam", "Steam ticket not valid"...).</summary>
        public string VerifyNote = "";
        /// <summary>Host side only: the address this player's packets come from.</summary>
        public IPEndPoint EndPoint;
        public float LastHeard;
        public bool HasState;
        public float StateAt;
        public int MapKey;
        public Vector3 Position;
        public float Radius;
        public KatamariState State;
        /// <summary>Their NetworkUserInfo bytes (which cousin, colours, presents), or null until it arrives.</summary>
        public byte[] Look;
        public ushort Seq;
        public float PingMs = -1f;

        // Free roam: where they walk in a hub (HubPresence), 0 when not in one.
        public int HubKey;
        public Vector3 HubPos;
        public float HubYaw, HubScale, HubAt;
        public int HubAnim = -1;
        public PoseSync HubPose = PoseSync.None;
        public ushort HubSeq;

        /// <summary>What they're doing, from their State packets (Whereabouts).</summary>
        public Activity Activity;
    }

    /// <summary>
    /// The online prototype's own netcode: a small UDP star between games that run this mod (the host relays
    /// everything). It never touches the game's online service, matchmaking or MultiplayGameManager's online
    /// states; everyone just plays their own offline game and shares their katamari, pickups and map.
    /// Runs on the main thread from Core.OnUpdate (non-blocking socket, polled once a frame).
    /// </summary>
    internal static class NetSession
    {
        public enum Role { Off, Host, Client }

        public const int DefaultPort = 27886;
        /// <summary>Our own netcode isn't bound by Katamari Ball's four; the player bar has room for eight.</summary>
        public const int MaxPlayers = 8;
        private const float StateInterval = 0.05f, IdleStateInterval = 0.5f, PingInterval = 2f, LookInterval = 3f, Timeout = 8f, JoinTimeout = 10f;
        private const int MaxLook = 4000;
        private const byte HostId = 0, NoId = 255;
        // Windows reports ICMP "port unreachable" as a reset on the next receive; turn that off.
        private const int SioUdpConnReset = -1744830452;

        public static Role Mode { get; private set; }
        public static byte MyId { get; private set; } = NoId;
        public static bool Connected => Mode == Role.Host || (Mode == Role.Client && MyId != NoId);
        public static string Info { get; private set; } = "Not connected.";
        public static bool InfoIsError { get; private set; }
        public static float HostPingMs { get; private set; } = -1f;
        public static int LocalPort => _sock?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;
        public static string MyName => _myName;
        /// <summary>Your look as sent to the others (NetworkUserInfo bytes), or null until it's been read from the save.</summary>
        public static byte[] MyLook => _myLook;
        /// <summary>Times your look changed while in the session (self-test).</summary>
        public static int LookChanges { get; private set; }
        public static bool BotRunning => _bot != null;
        public static readonly SortedDictionary<byte, Peer> Peers = new();

        /// <summary>Your preference for hosting: share picked-up objects. The session uses the host's (<see cref="PickupsShared"/>).</summary>
        public static bool SharePickups = true, FollowHost = true, BringToMyMap = true;

        /// <summary>
        /// Whether picked-up objects are shared in this session: the host's setting, sent to everyone (Settings).
        /// Off, nobody's game sends, relays or applies pickups, and objects hidden earlier come back.
        /// </summary>
        public static bool PickupsShared => Mode == Role.Host ? SharePickups : Mode == Role.Client && _hostShares;

        /// <summary>Self-test: the loopback bot votes no.</summary>
        public static bool BotVotesNo;

        /// <summary>The loopback bot says back what it hears on voice chat (a second after the talk ends).</summary>
        public static bool BotEcho = true;
        /// <summary>Self-test: how many talks the bot has said back.</summary>
        public static int BotEchoes { get; private set; }

        /// <summary>Self-test: the loopback bot talks for a while with a made-up voice (no microphone anywhere).</summary>
        public static string BotTalk(float seconds) => _bot == null ? "no test bot" : _bot.Talk(seconds);

        /// <summary>The loopback bot's player id (255 until it has joined).</summary>
        public static byte BotId => _bot?.Id ?? NoId;

        /// <summary>Self-test: the bot leaves the race it's in (its result is a DQ, as the pause menu's "Only me" back to the Plaza sends).</summary>
        public static string BotLeaveRace() => _bot == null ? "no test bot" : _bot.LeaveRace();

        /// <summary>Self-test: the bot asks the host for a vote.</summary>
        public static string BotPropose(VoteKind kind, int arg) => _bot == null ? "no test bot" : _bot.Propose(kind, arg);

        /// <summary>Self-test: the bot's prince pose: 0 none (its move number), 1 a copy of yours, 2 your state held still at 0.35.</summary>
        public static int BotMirrorPose;

        /// <summary>
        /// Self-test, outside a session: the client side of joining. A roster that arrives before our Welcome
        /// mustn't list us as another player, and a late packet from someone not on the roster mustn't add them.
        /// </summary>
        public static string SelfTestRoster(out bool ok)
        {
            ok = false;
            if (Mode != Role.Off) return "skipped (in a session)";
            string info = Info, myName = _myName;
            bool infoErr = InfoIsError;
            try
            {
                Mode = Role.Client;
                MyId = NoId;
                Peers.Clear();
                float now = Time.unscaledTime;
                var hostId = new PlayerIdentity { Kind = PlayerIdentity.InstallId, Value = 0x1234 };
                var roster = Proto.Begin(Msg.Roster, HostId).U8(3).U8(HostId).Str("Host").Then(hostId.Write).U8(0)
                    .U8(1).Str("Me").Then(MyIdentity.Write).U8(0)
                    // A stale copy of us under another id (the duplicate the first playtest showed): never listed.
                    .U8(5).Str("Me (old)").Then(MyIdentity.Write).U8(0).ToArray();
                Feed(roster, now);
                Feed(Proto.Begin(Msg.Welcome, HostId).U8(1).ToArray(), now);
                bool afterWelcome = !Peers.ContainsKey(1);
                Feed(roster, now);
                // Someone who isn't on the roster (left a moment ago): their late state mustn't bring them back.
                var late = new KatamariState { Map = 0, CoreInfo = Array.Empty<byte>(), ClearIgt = -1f, ClearRta = -1f, ClearStage = -1f, HudRank = 255, StageRate = -1, Score = -1 }.Write(Proto.Begin(Msg.State, 3).U16(1)).ToArray();
                Feed(late, now);
                // The roster calls us "Me" (as if the host had numbered a name already taken): we go by it.
                bool renamed = _myName == "Me";
                ok = afterWelcome && renamed && MyId == 1 && Peers.Count == 1 && Peers.ContainsKey(HostId) && !Peers.ContainsKey(1) && !Peers.ContainsKey(3) && !Peers.ContainsKey(5);
                return $"roster before Welcome, Welcome, roster again, a stray state: my id {MyId}, players [{string.Join(", ", Peers.Values.Select(p => $"{p.Id} {p.Name}"))}], " +
                       $"going by the roster's name {renamed}";
            }
            catch (Exception e) { return "failed: " + e.Message; }
            finally
            {
                Mode = Role.Off;
                MyId = NoId;
                Peers.Clear();
                Info = info;
                InfoIsError = infoErr;
                _myName = myName;
            }
        }

        private static void Feed(byte[] pkt, float now)
        {
            var r = new PacketReader(pkt, pkt.Length);
            if (Proto.TryHeader(r, out var type, out byte sender)) ClientHandle(type, sender, r, now);
        }

        private static bool _hostShares, _sharedNow;
        private static float _nextSettings;

        private static Socket _sock;
        private static IPEndPoint _hostEp;
        private static string _myName = "Player", _password = "";
        private static float _nextState, _nextPing, _nextHello, _nextLook, _joinDeadline, _hostHeard, _followUntil, _nextRoster;
        private static byte[] _myLook;
        private static ushort _seq;
        private static int _lastMapSent = int.MinValue, _followKey;
        private static readonly byte[] Buf = new byte[8192];
        private static bool _oversizeLogged, _lookLogged, _noLookLogged;
        private static LoopbackBot _bot;

        private static MelonPreferences_Entry<string> _namePref, _addressPref;
        private static MelonPreferences_Entry<int> _portPref;
        private static MelonPreferences_Entry<bool> _sharePref, _followPref, _bringPref, _barPref, _racePref, _steamOnlyPref;
        private static MelonPreferences_Entry<string> _installIdPref;
        private static SteamTicket _myTicket;

        /// <summary>Hosting: only let in players whose Steam account the host could verify (test bots always join).</summary>
        public static bool SteamOnly;

        /// <summary>
        /// Who you are to the others: your Steam account when Steam is running, otherwise an id made once for this
        /// install (saved in MelonPreferences), so you're still the same player when you rejoin.
        /// </summary>
        public static PlayerIdentity MyIdentity
        {
            get
            {
                ulong steam = OUAKModAPI.Steam.MySteamId;
                if (steam != 0) return new PlayerIdentity { Kind = PlayerIdentity.SteamAccount, Value = steam };
                if (_installIdPref == null) return default;
                if (!ulong.TryParse(_installIdPref.Value, out ulong id) || id == 0)
                {
                    var bytes = new byte[8];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
                    id = BitConverter.ToUInt64(bytes, 0) | 1;
                    _installIdPref.Value = id.ToString();
                    MelonPreferences.Save();
                }
                return new PlayerIdentity { Kind = PlayerIdentity.InstallId, Value = id };
            }
        }

        /// <summary>The players in the session, for other mods (OUAKModAPI.Multiplayer.Players).</summary>
        public static List<SessionPlayer> SessionPlayers()
        {
            var list = new List<SessionPlayer>();
            if (!Connected) return list;
            var me = MyIdentity;
            list.Add(new SessionPlayer { Id = MyId, Name = _myName, IsYou = true, IsHost = Mode == Role.Host, SteamId = me.IsSteam ? me.Value : 0, Verified = me.IsSteam });
            foreach (var p in Peers.Values)
                list.Add(new SessionPlayer { Id = p.Id, Name = p.Name, IsHost = p.Id == HostId, SteamId = p.Identity.IsSteam ? p.Identity.Value : 0, Verified = p.Verified });
            return list;
        }

        /// <summary>Other mods' data (OUAKModAPI.Multiplayer.Send): to the host, who passes it on.</summary>
        public static void SendModData(string channel, byte[] data, byte to)
        {
            if (!Connected || string.IsNullOrEmpty(channel) || data == null) return;
            var pkt = Proto.Begin(Msg.ModData, MyId).U8(to).Str(channel).Bytes(data).ToArray();
            if (Mode == Role.Client) { SendTo(pkt, _hostEp); return; }
            foreach (var p in Peers.Values)
                if (to == 255 || p.Id == to) SendTo(pkt, p.EndPoint);
        }

        /// <summary>A line in the Online tab's status (and the log).</summary>
        public static void Note(string msg, bool error = false) => SetInfo(msg, error);

        public static string SavedName => _namePref.Value;
        public static string SavedAddress => _addressPref.Value;
        public static int SavedPort => _portPref.Value;

        public static void Init(MelonPreferences_Category cat)
        {
            _namePref = cat.CreateEntry("Name", "Player", "Name", "Your name in online sessions (16 characters max).");
            _addressPref = cat.CreateEntry("Address", "", "Host address", "IP address (or host name) of the session to join.");
            _portPref = cat.CreateEntry("Port", DefaultPort, "Port", "UDP port to host on / join.");
            _sharePref = cat.CreateEntry("SharePickups", true, "Share picked-up objects", "Objects someone rolls up disappear for everyone on that map.");
            _followPref = cat.CreateEntry("FollowHost", true, "Follow the host's map", "When the host changes map, load the same one.");
            _bringPref = cat.CreateEntry("BringToMyMap", true, "Bring everyone to my map", "When hosting, players follow you from map to map.");
            _barPref = cat.CreateEntry("BarTwoRows", false, "Player bar in two rows", "false = one row (two groups of four), true = two rows of four.");
            _racePref = cat.CreateEntry("RaceStart", true, "Race start", "Hosting: true = everyone starts when their own game gives control; false = strict 3-2-1-GO start.");
            _steamOnlyPref = cat.CreateEntry("SteamVerifiedOnly", false, "Steam-verified players only",
                "Hosting: only let in players whose Steam account your game could verify with Steam.");
            _installIdPref = cat.CreateEntry("InstallId", "", "Install id",
                "Identifies you in sessions when Steam isn't running (made once, random).");
            SteamOnly = _steamOnlyPref.Value;
            SharePickups = _sharePref.Value;
            FollowHost = _followPref.Value;
            BringToMyMap = _bringPref.Value;
            PlayerBar.TwoRows = _barPref.Value;
            SyncRun.RaceStart = _racePref.Value;
            GhostSettings.Init(cat);
            Lobby.Init(cat);
            Ranking.Init(cat);
            Leaderboards.LeaderboardRecorder.Init(cat);
            Voice.Init(cat);
            QuickChat.Init(cat);
            OnlineSelfTest.CheckFlag();
        }

        public static void SavePrefs(string name = null, string address = null, int port = 0)
        {
            if (name != null) _namePref.Value = Proto.CleanName(name);
            if (address != null) _addressPref.Value = address.Trim();
            if (port > 0) _portPref.Value = port;
            _sharePref.Value = SharePickups;
            _followPref.Value = FollowHost;
            _bringPref.Value = BringToMyMap;
            _barPref.Value = PlayerBar.TwoRows;
            if (Mode != Role.Client) _racePref.Value = SyncRun.RaceStart;
            _steamOnlyPref.Value = SteamOnly;
            MelonPreferences.Save();
        }

        // ---------------------------------------------------------------- start / stop

        /// <param name="remember">Save the name and port as the Online tab's defaults (the self-test doesn't).</param>
        public static string Host(int port, string name, string password, bool thisPcOnly, bool remember = true)
        {
            Leave(null);
            _sock = OpenSocket(new IPEndPoint(thisPcOnly ? IPAddress.Loopback : IPAddress.Any, port));
            _myName = Proto.CleanName(name);
            _password = password ?? "";
            Mode = Role.Host;
            MyId = HostId;
            _lastMapSent = int.MinValue;
            if (remember) SavePrefs(name, null, port);
            SetInfo(thisPcOnly
                ? $"Hosting on UDP port {port}, this PC only (for the loopback test)."
                : $"Hosting on UDP port {port}. Players join with your IP address and this port.");
            return Info;
        }

        public static string Join(string address, int port, string name, string password, bool remember = true)
        {
            if (!_rejoining) _rejoinUntil = 0f;
            _rejoinAddress = address;
            _rejoinPort = port;
            _rejoinName = name;
            _rejoinPassword = password;
            Leave(null, false, keepRejoin: true);
            var ip = Resolve(address);
            _sock = OpenSocket(new IPEndPoint(IPAddress.Any, 0));
            _hostEp = new IPEndPoint(ip, port);
            _myName = Proto.CleanName(name);
            _password = password ?? "";
            Mode = Role.Client;
            MyId = NoId;
            float now = Time.unscaledTime;
            // With Steam, the host checks a session ticket; Steam needs a moment before a new ticket checks out.
            _myTicket = MyIdentity.IsSteam ? OUAKModAPI.Steam.CreateTicket() : null;
            _nextHello = now + (_myTicket != null ? 1.2f : 0f);
            _joinDeadline = now + JoinTimeout + (_myTicket != null ? 1.2f : 0f);
            _hostHeard = now;
            if (remember) SavePrefs(name, address, port);
            SetInfo($"Connecting to {ip}:{port}...");
            return Info;
        }

        /// <summary>Closes the session (telling the others) and shows <paramref name="reason"/> if given.</summary>
        public static void Leave(string reason, bool error = false) => Leave(reason, error, false);

        private static void Leave(string reason, bool error, bool keepRejoin)
        {
            if (!keepRejoin) _rejoinUntil = 0f;
            StopBot();
            if (_sock != null)
            {
                try
                {
                    var bye = Proto.Begin(Msg.Bye, MyId).ToArray();
                    if (Mode == Role.Host)
                    {
                        foreach (var p in Peers.Values) SendTo(bye, p.EndPoint);
                    }
                    else if (_hostEp != null)
                    {
                        SendTo(bye, _hostEp);
                    }
                }
                catch { }
                try { _sock.Close(); } catch { }
                _sock = null;
            }
            foreach (var p in Peers.Values) ForgetIdentity(p);
            OUAKModAPI.Steam.CancelTicket(_myTicket);
            _myTicket = null;
            Mode = Role.Off;
            MyId = NoId;
            _hostEp = null;
            HostPingMs = -1f;
            Peers.Clear();
            HostAuthority.Reset();
            SyncRun.Reset();
            PlazaFlow.Reset("you left the session");
            Chat.Clear();
            Voice.Reset();
            _hostShares = false;
            _sharingChanged = false;
            if (_sharedNow) { _sharedNow = false; Ghosts.RestoreHidden(); }
            if (reason != null) SetInfo(reason, error);
        }

        /// <summary>Host: the session settings everyone follows (flags: 1 shared pickups, 2 Sudden Death; then the lobby settings).</summary>
        private static PacketWriter SettingsPacket() =>
            Lobby.Write(Proto.Begin(Msg.Settings, HostId).U8((byte)((SharePickups ? 1 : 0) | Lobby.Flags)));

        /// <summary>Host: a lobby setting changed; send the settings now rather than at the next 2 s tick.</summary>
        public static void SettingsChanged() => _nextSettings = 0f;

        /// <summary>Sharing switched on or off (the host's toggle, or its Settings arriving): put back what others' pickups hid.</summary>
        private static void CheckSharing(float now)
        {
            bool shared = PickupsShared;
            if (shared == _sharedNow) return;
            _sharedNow = shared;
            int restored = shared ? 0 : Ghosts.RestoreHidden();
            string msg = shared ? "Picked-up objects are shared: what anyone rolls up disappears for everyone on the map."
                                : "Picked-up objects aren't shared: everyone keeps their own stage" + (restored > 0 ? $" ({restored} objects put back)." : ".");
            // Hosting with sharing on is the normal start, not news; a change during the session is.
            if (Mode == Role.Host && shared && !_sharingChanged) MelonLogger.Msg("[Online] " + msg);
            else SetInfo(msg);
            _sharingChanged = true;
            if (Mode == Role.Host) _nextSettings = 0f;
        }

        private static bool _sharingChanged;

        public static string StartBot()
        {
            if (Mode != Role.Host) throw new InvalidOperationException("Host a session first: the bot joins it over 127.0.0.1.");
            if (_bot != null) return "The loopback bot is already running.";
            _bot = new LoopbackBot(LocalPort);
            return "Loopback bot started. In a stage, a ghost katamari circles you and rolls up nearby objects.";
        }

        public static void StopBot()
        {
            _bot?.Stop();
            _bot = null;
        }

        private static Socket OpenSocket(IPEndPoint bind)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            try { s.IOControl(SioUdpConnReset, new byte[] { 0 }, null); } catch { }
            try
            {
                s.Bind(bind);
            }
            catch (SocketException e)
            {
                s.Dispose();
                throw new InvalidOperationException(e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"UDP port {bind.Port} is already in use. Pick another port."
                    : $"Couldn't open UDP port {bind.Port}: {e.Message}");
            }
            return s;
        }

        private static IPAddress Resolve(string address)
        {
            address = (address ?? "").Trim();
            if (address.Length == 0) throw new InvalidOperationException("Enter the host's IP address.");
            if (IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) return ip;
            try
            {
                var found = Dns.GetHostAddresses(address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (found != null) return found;
            }
            catch (SocketException) { }
            throw new InvalidOperationException($"Couldn't find \"{address}\". Use the host's IPv4 address, e.g. 192.168.1.20.");
        }

        // ---------------------------------------------------------------- per frame

        public static void Tick()
        {
            OnlineSelfTest.Tick();
            float now = Time.unscaledTime;
            float gap = now - _lastTick;
            _lastTick = now;
            if (_sock == null)
            {
                TryRejoin(now);
                return;
            }
            // This game stalled (loading, or frozen): the others' packets waited, and nobody should be timed out
            // for our own pause. Logged, so a dropped player can be told from a stalled game (the user's Super Stream
            // playtest, 2026-11-12: the host stopped hearing a player ~20 s into a stage, no error on either side).
            if (gap > 2f && _lastTick > 0f)
            {
                MelonLogger.Msg($"[Online] This game didn't update for {gap:0.0} s (loading, or it froze): the others get that long extra before timing out.");
                foreach (var p in Peers.Values) p.LastHeard = Mathf.Max(p.LastHeard, now - 1f);
                _hostHeard = Mathf.Max(_hostHeard, now - 1f);
            }
            Receive(now);
            LogSilence(now);
            if (_sock == null) return;
            CheckSharing(now);
            if (Mode == Role.Host) HostTick(now);
            else ClientTick(now);
            if (_sock == null) return;
            SyncRun.Tick();
            Votes.Tick();
            _bot?.Tick(now);
        }

        private static float _lastTick = -1f, _rejoinUntil, _nextRejoin;
        private static string _rejoinAddress, _rejoinPassword, _rejoinName;
        private static int _rejoinPort, _rejoinTries;
        private static bool _rejoining, _hostSilentLogged;
        private static readonly HashSet<byte> _silentLogged = new();

        /// <summary>Times a lost host was joined again by itself (self-test).</summary>
        public static int Rejoins { get; private set; }

        /// <summary>Whether this game is trying to get back into a session it lost.</summary>
        public static bool Rejoining => _rejoinUntil > 0f && Time.unscaledTime < _rejoinUntil;

        /// <summary>A player (or the host) not heard from for 3 s: logged once, with how long, before any time-out.</summary>
        private static void LogSilence(float now)
        {
            if (Mode == Role.Host)
            {
                foreach (var p in Peers.Values)
                {
                    bool silent = now - p.LastHeard > 3f;
                    if (silent && _silentLogged.Add(p.Id))
                        MelonLogger.Msg($"[Online] Nothing from {p.Name} for {now - p.LastHeard:0.0} s (their last packet from {p.EndPoint}).");
                    else if (!silent && _silentLogged.Remove(p.Id))
                        MelonLogger.Msg($"[Online] {p.Name} is back.");
                }
            }
            else if (Mode == Role.Client && MyId != NoId)
            {
                bool silent = now - _hostHeard > 3f;
                if (silent && !_hostSilentLogged) { _hostSilentLogged = true; MelonLogger.Msg($"[Online] Nothing from the host for {now - _hostHeard:0.0} s."); }
                else if (!silent && _hostSilentLogged) { _hostSilentLogged = false; MelonLogger.Msg("[Online] The host is back."); }
            }
        }

        /// <summary>
        /// After losing the host (not after leaving or being refused), this game tries to join the same session again by
        /// itself for a minute: a short stall or network hiccup costs a moment instead of the session.
        /// </summary>
        private static void TryRejoin(float now)
        {
            if (_rejoinUntil <= 0f) return;
            if (now > _rejoinUntil)
            {
                _rejoinUntil = 0f;
                MelonLogger.Msg($"[Online] Couldn't get back into the session ({_rejoinTries} tries in a minute).");
                SetInfo("Couldn't get back into the session. Join again from the Online tab.", true);
                return;
            }
            if (now < _nextRejoin) return;
            _nextRejoin = now + 12f;
            _rejoinTries++;
            MelonLogger.Msg($"[Online] Trying to get back into the session (try {_rejoinTries}).");
            _rejoining = true;
            try { Join(_rejoinAddress, _rejoinPort, _rejoinName, _rejoinPassword, remember: false); }
            finally { _rejoining = false; }
        }

        private static void Receive(float now)
        {
            for (int n = 0; n < 256 && _sock != null; n++)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int len;
                try
                {
                    if (_sock.Available <= 0) break;
                    len = _sock.ReceiveFrom(Buf, ref from);
                }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { break; }
                if (len <= 0) continue;
                if (len > Proto.MaxPacket)
                {
                    if (!_oversizeLogged) { _oversizeLogged = true; MelonLogger.Warning($"[Online] Dropped a {len}-byte packet (limit {Proto.MaxPacket})."); }
                    continue;
                }
                try
                {
                    var r = new PacketReader(Buf, len);
                    if (!Proto.TryHeader(r, out var type, out byte sender)) continue;
                    var ep = (IPEndPoint)from;
                    if (Mode == Role.Host) HostHandle(type, r, len, ep, now);
                    else if (ep.Equals(_hostEp)) ClientHandle(type, sender, r, now);
                }
                catch (EndOfStreamException) { }
                catch (Exception e) { MelonLogger.Warning($"[Online] Bad packet from {from}: {e.Message}"); }
            }
        }

        private static void HostHandle(Msg type, PacketReader r, int len, IPEndPoint from, float now)
        {
            Peer peer = null;
            foreach (var p in Peers.Values)
                if (from.Equals(p.EndPoint)) { peer = p; break; }

            if (type == Msg.Hello)
            {
                string name = Proto.CleanName(r.Str()), pass = r.Str(), version = r.Str();
                var ident = r.Left >= 9 ? PlayerIdentity.Read(r) : default;
                byte[] ticket = r.Left >= 2 ? r.Bytes() : Array.Empty<byte>();
                bool joined = false;
                if (peer == null)
                {
                    if (_password.Length > 0 && pass != _password)
                    {
                        Send(Proto.Begin(Msg.Reject, HostId).Str("Wrong password."), from);
                        return;
                    }
                    var me = MyIdentity;
                    if (ident.IsSet && ident.Equals(me))
                    {
                        Send(Proto.Begin(Msg.Reject, HostId).Str("That's the host's own account: you can't join yourself."), from);
                        return;
                    }
                    // Steam checks the ticket: Steam signed it for this account and this game.
                    bool verified = false;
                    string note = ident.ToString();
                    if (ident.IsSteam)
                    {
                        var res = OUAKModAPI.Steam.Verify(ticket, ident.Value);
                        verified = res == SteamAuthResult.OK;
                        note = OUAKModAPI.Steam.Describe(res);
                    }
                    if (SteamOnly && !verified && ident.Kind != PlayerIdentity.Bot)
                    {
                        Send(Proto.Begin(Msg.Reject, HostId).Str($"This session only lets in players Steam has verified ({note})."), from);
                        if (verified) OUAKModAPI.Steam.EndVerify(ident.Value);
                        return;
                    }
                    // The same person again (they rejoined, their router moved them, or a second copy of their game):
                    // they take their old place, never a second one.
                    var same = ident.IsSet ? Peers.Values.FirstOrDefault(p => p.Identity.Equals(ident)) : null;
                    if (same != null && same.Verified && !verified)
                    {
                        // Someone claiming a verified player's account without Steam's word for it.
                        Send(Proto.Begin(Msg.Reject, HostId).Str("Someone with that Steam account is already in the session."), from);
                        return;
                    }
                    if (same != null)
                    {
                        if (same.EndPoint != null && !same.EndPoint.Equals(from) && now - same.LastHeard < 2f)
                            Send(Proto.Begin(Msg.Reject, HostId).Str("You joined this session again from somewhere else."), same.EndPoint);
                        peer = same;
                        peer.EndPoint = from;
                        peer.Verified = verified;
                        peer.VerifyNote = note;
                        SetInfo($"{peer.Name} reconnected ({note}).");
                    }
                    else if (!ident.IsSet)
                    {
                        // No identity (shouldn't happen with this version): the old guess, same name from the same address once it went quiet.
                        peer = Peers.Values.FirstOrDefault(p => !p.Identity.IsSet && p.AskedName == name && p.EndPoint != null && p.EndPoint.Address.Equals(from.Address) && now - p.LastHeard > 2f);
                        if (peer != null)
                        {
                            peer.EndPoint = from;
                            SetInfo($"{name} reconnected.");
                        }
                    }
                    if (peer == null)
                    {
                        byte id = FreeId();
                        if (id == NoId)
                        {
                            Send(Proto.Begin(Msg.Reject, HostId).Str($"The session is full ({MaxPlayers} players)."), from);
                            return;
                        }
                        // Two players called the same (both left at "Player"): the newcomer gets a number.
                        peer = new Peer { Id = id, Name = UniqueName(name), AskedName = name, EndPoint = from, Identity = ident, Verified = verified, VerifyNote = note };
                        Peers[id] = peer;
                        joined = true;
                        name = peer.Name;
                    }
                }
                peer.LastHeard = now;
                // Welcome first: a client that hears the roster before its own id would list itself as another player.
                Send(Proto.Begin(Msg.Welcome, HostId).U8(peer.Id), from);
                Send(SettingsPacket(), from);
                BroadcastRoster();
                if (joined)
                {
                    string how = peer.Verified ? "verified by Steam" : peer.VerifyNote;
                    SetInfo(version == MultiplayerCore.Version ? $"{name} joined ({how})." : $"{name} joined ({how}; mod {version}, yours is {MultiplayerCore.Version}).");
                    Chat.Notice($"{name} joined.");
                    SendMapTo(peer);
                }
                return;
            }

            if (peer == null) return;
            peer.LastHeard = now;
            switch (type)
            {
                case Msg.State:
                    ReadState(peer, r, now);
                    Relay(len, peer);
                    break;
                case Msg.Hub:
                    ReadHub(peer, r, now);
                    Relay(len, peer);
                    break;
                case Msg.Pickup:
                {
                    // Not sharing: drop it here, so no one's stage changes.
                    if (!PickupsShared) break;
                    int map = r.I32();
                    uint key = r.U32();
                    var pos = r.Vec();
                    var rot = r.Quat();
                    Ghosts.ApplyRemotePickup(peer.Id, map, key, pos, rot);
                    Relay(len, peer);
                    break;
                }
                case Msg.Look:
                {
                    var look = r.Bytes();
                    if (look.Length == 0 || look.Length > MaxLook) break;
                    if (peer.Look == null) MelonLogger.Msg($"[Online] Got {peer.Name}'s look ({look.Length} bytes, {Looks.Describe(look)}).");
                    peer.Look = look;
                    Relay(len, peer);
                    break;
                }
                case Msg.Ping:
                    // Pong carries our clock too: clients line their run clock up with the host's from it.
                    Send(Proto.Begin(Msg.Pong, HostId).F32(r.F32()).F32(now), from);
                    break;
                case Msg.Pong:
                    peer.PingMs = (now - r.F32()) * 1000f;
                    break;
                case Msg.Ready:
                {
                    int map = r.I32();
                    int objects = r.I32();
                    SyncRun.HostOnReady(peer, map, objects, now);
                    break;
                }
                case Msg.Result:
                {
                    byte runId = r.U8();
                    int map = r.I32();
                    SyncRun.HostOnResult(peer, runId, map, RunResult.Read(r));
                    break;
                }
                case Msg.VoteAsk:
                {
                    var kind = (VoteKind)r.U8();
                    int arg = r.I32();
                    int arg2 = r.I32();
                    Votes.Start(kind, arg, peer.Id, arg2: arg2);
                    break;
                }
                case Msg.GatherState:
                {
                    byte gatherId = r.U8();
                    byte state = r.U8();
                    PlazaFlow.HostOnState(peer.Id, gatherId, state);
                    break;
                }
                case Msg.Chat:
                    Chat.HostOnChat(peer, r.Str());
                    break;
                case Msg.Voice:
                    // Played here and passed on (with the sender stamped), unless it's malformed or too fast.
                    if (Voice.HostOnVoice(peer, r)) Relay(len, peer);
                    break;
                case Msg.VoteCast:
                {
                    byte voteId = r.U8();
                    bool yes = r.U8() != 0;
                    Votes.HostOnCast(peer.Id, voteId, yes);
                    break;
                }
                case Msg.ModData:
                {
                    byte to = r.U8();
                    string channel = r.Str();
                    var data = r.Bytes();
                    if (to == 255 || to == HostId) OUAKModAPI.Multiplayer.Deliver(peer.Id, channel, data);
                    if (to != HostId) RelayTo(len, peer, to);
                    break;
                }
                case Msg.Bye:
                    ForgetIdentity(peer);
                    Peers.Remove(peer.Id);
                    SetInfo($"{peer.Name} left.");
                    Chat.Notice($"{peer.Name} left.");
                    BroadcastRoster();
                    break;
            }
        }

        private static void ClientHandle(Msg type, byte sender, PacketReader r, float now)
        {
            _hostHeard = now;
            switch (type)
            {
                case Msg.Welcome:
                {
                    byte id = r.U8();
                    if (MyId == NoId) SetInfo($"Connected as player {id + 1}.");
                    if (MyId == NoId && _rejoinUntil > 0f)
                    {
                        _rejoinUntil = 0f;
                        Rejoins++;
                        MelonLogger.Msg($"[Online] Back in the session (rejoined after losing the host, try {_rejoinTries}).");
                    }
                    MyId = id;
                    // A roster that came before our id may have listed us as someone else.
                    Peers.Remove(id);
                    break;
                }
                case Msg.Reject:
                    Leave($"The host refused: {r.Str()}", true);
                    break;
                case Msg.Roster:
                {
                    // Not before we know which entry is us (the host sends it again every few seconds).
                    if (MyId == NoId) break;
                    int n = r.U8();
                    var listed = new HashSet<byte>();
                    var seen = new HashSet<PlayerIdentity>();
                    var me = MyIdentity;
                    for (int i = 0; i < n && i < MaxPlayers; i++)
                    {
                        byte id = r.U8();
                        string name = Proto.CleanName(r.Str());
                        var ident = PlayerIdentity.Read(r);
                        bool verified = r.U8() != 0;
                        // Never list yourself as someone else (by id, or by your own Steam account / install id),
                        // and never the same person twice.
                        if (id != MyId && ident.IsSet && (ident.Equals(me) || !seen.Add(ident))) continue;
                        if (id == MyId)
                        {
                            // The host numbers a name someone else already has: go by the one others see.
                            if (name != _myName)
                            {
                                SetInfo($"Someone here is already called {_myName}, so you're {name} in this session.");
                                _myName = name;
                            }
                            continue;
                        }
                        listed.Add(id);
                        if (!Peers.TryGetValue(id, out var p)) Peers[id] = p = new Peer { Id = id };
                        p.Name = name;
                        p.Identity = ident;
                        p.Verified = verified;
                        p.VerifyNote = id == HostId ? "the host" : verified ? "verified by Steam" : ident.ToString();
                        p.LastHeard = now;
                    }
                    foreach (byte gone in Peers.Keys.Where(k => !listed.Contains(k)).ToList()) Peers.Remove(gone);
                    break;
                }
                case Msg.State:
                {
                    // Players come from the roster only (a late packet from someone who left mustn't bring them back).
                    if (sender == MyId || !Peers.TryGetValue(sender, out var p)) break;
                    p.LastHeard = now;
                    ReadState(p, r, now);
                    break;
                }
                case Msg.Pickup:
                {
                    int map = r.I32();
                    uint key = r.U32();
                    var pos = r.Vec();
                    var rot = r.Quat();
                    if (sender != MyId) Ghosts.ApplyRemotePickup(sender, map, key, pos, rot);
                    break;
                }
                case Msg.Look:
                {
                    var look = r.Bytes();
                    if (sender == MyId || look.Length == 0 || look.Length > MaxLook || !Peers.TryGetValue(sender, out var p)) break;
                    if (p.Look == null) MelonLogger.Msg($"[Online] Got {p.Name}'s look ({look.Length} bytes, {Looks.Describe(look)}).");
                    p.Look = look;
                    break;
                }
                case Msg.StartMap:
                {
                    var kind = (MapKind)r.U8();
                    int stage = r.I32();
                    byte flags = r.U8();
                    SyncRun.RaceStart = (flags & 2) == 0;
                    if ((flags & 4) != 0) OnHostRestart(kind, stage, now);
                    else OnStartMap(kind, stage, (flags & 1) != 0, (flags & 8) != 0, now);
                    break;
                }
                case Msg.Ping:
                    Send(Proto.Begin(Msg.Pong, MyId).F32(r.F32()).F32(now), _hostEp);
                    break;
                case Msg.Pong:
                {
                    float rtt = now - r.F32();
                    float hostTime = r.F32();
                    HostPingMs = rtt * 1000f;
                    SyncRun.ClockSample(rtt, hostTime, now);
                    break;
                }
                case Msg.Go:
                {
                    byte runId = r.U8();
                    int map = r.I32();
                    float startAt = r.F32();
                    bool strict = r.U8() != 0;
                    SyncRun.ClientOnGo(runId, map, startAt, strict);
                    break;
                }
                case Msg.VoteStart:
                {
                    byte voteId = r.U8();
                    var kind = (VoteKind)r.U8();
                    int arg = r.I32();
                    float deadline = r.F32();
                    byte by = r.U8();
                    int arg2 = r.I32();
                    Votes.ClientOnStart(voteId, kind, arg, deadline, by, arg2);
                    break;
                }
                case Msg.Gather:
                {
                    byte gatherId = r.U8();
                    int key = r.I32();
                    int spot = r.I32();
                    float deadline = r.F32();
                    bool go = r.U8() != 0;
                    int n = Math.Min((int)r.U8(), MaxPlayers);
                    var states = new List<(byte, byte)>(n);
                    for (int i = 0; i < n; i++) states.Add((r.U8(), r.U8()));
                    PlazaFlow.ClientOnGather(gatherId, key, spot, deadline, go, states);
                    break;
                }
                case Msg.VoteResult:
                {
                    byte voteId = r.U8();
                    bool passed = r.U8() != 0;
                    int yes = r.U8(), no = r.U8(), total = r.U8();
                    Votes.ClientOnResult(voteId, passed, yes, no, total);
                    break;
                }
                case Msg.Standings:
                {
                    byte runId = r.U8();
                    int map = r.I32();
                    bool final = r.U8() != 0;
                    int n = Math.Min((int)r.U8(), MaxPlayers);
                    var list = new List<RunResult>(n);
                    for (int i = 0; i < n; i++)
                    {
                        byte id = r.U8();
                        var res = RunResult.Read(r);
                        res.Id = id;
                        res.Verdict = r.U8();
                        list.Add(res);
                    }
                    SyncRun.OnStandings(runId, map, list, final);
                    break;
                }
                case Msg.Hurry:
                {
                    byte runId = r.U8();
                    float best = r.F32(), bestReal = r.F32();
                    int n = Math.Min((int)r.U8(), MaxPlayers);
                    var ids = new List<byte>(n);
                    for (int i = 0; i < n; i++) ids.Add(r.U8());
                    float window = Mathf.Clamp(r.U8(), 5, 255);
                    SyncRun.OnHurry(runId, best, bestReal, ids, window);
                    break;
                }
                case Msg.Settings:
                {
                    byte flags = r.U8();
                    _hostShares = (flags & 1) != 0;
                    Lobby.Read(flags, r);
                    break;
                }
                case Msg.Hub:
                {
                    if (sender == MyId || !Peers.TryGetValue(sender, out var p)) break;
                    p.LastHeard = now;
                    ReadHub(p, r, now);
                    break;
                }
                case Msg.VoteDenied:
                {
                    byte seconds = r.U8();
                    Votes.OnDenied(seconds, r.Left > 0 ? r.Str() : "");
                    break;
                }
                case Msg.HostPlaza:
                    HostAuthority.ClientOnHostPlaza(r.I32());
                    break;
                case Msg.ModData:
                {
                    r.U8();
                    string channel = r.Str();
                    var data = r.Bytes();
                    if (sender != MyId) OUAKModAPI.Multiplayer.Deliver(sender, channel, data);
                    break;
                }
                case Msg.Chat:
                {
                    byte from = r.U8();
                    Chat.ClientOnChat(from, r.Str());
                    break;
                }
                case Msg.Voice:
                    Voice.ClientOnVoice(sender, r);
                    break;
                case Msg.RunCancel:
                    SyncRun.ClientOnCancel(r.U8());
                    break;
                case Msg.Bye:
                    Leave("The host closed the session.", true);
                    break;
            }
        }

        private static void HostTick(float now)
        {
            foreach (var p in Peers.Values.Where(p => now - p.LastHeard > Timeout).ToList())
            {
                ForgetIdentity(p);
                Peers.Remove(p.Id);
                SetInfo($"{p.Name} timed out.");
                Chat.Notice($"{p.Name} lost the connection.");
                BroadcastRoster();
            }

            // Everyone's list of who's here, now and then too: a lost roster packet can't leave anyone out of step.
            if (now >= _nextRoster)
            {
                _nextRoster = now + 3f;
                if (Peers.Count > 0) BroadcastRoster();
            }

            if (BringToMyMap)
            {
                var entry = Ghosts.CurrentEntry();
                int key = entry != null ? Ghosts.KeyFor(entry) : 0;
                // Back in a plaza or on the ship: the next stage is news even if it's the same one again.
                if (entry == null && HubPresence.HubKey != 0) _lastMapSent = int.MinValue;
                if (entry != null && key != _lastMapSent)
                {
                    _lastMapSent = key;
                    var pkt = MapPacket(entry);
                    foreach (var p in Peers.Values) SendTo(pkt, p.EndPoint);
                }
            }

            if (now >= _nextState)
            {
                var pkt = StatePacket(now);
                foreach (var p in Peers.Values) SendTo(pkt, p.EndPoint);
            }
            var hub = HubPacket(now);
            if (hub != null) foreach (var p in Peers.Values) SendTo(hub, p.EndPoint);
            if (now >= _nextPing)
            {
                _nextPing = now + PingInterval;
                var ping = Proto.Begin(Msg.Ping, HostId).F32(now).ToArray();
                foreach (var p in Peers.Values) SendTo(ping, p.EndPoint);
            }
            // The session settings, every 2 s (and right after a change), so a dropped packet can't leave anyone out of step.
            if (now >= _nextSettings)
            {
                _nextSettings = now + 2f;
                var settings = SettingsPacket().ToArray();
                foreach (var p in Peers.Values) SendTo(settings, p.EndPoint);
            }
            if (now >= _nextLook && Peers.Count > 0)
            {
                var look = LookPacket(now);
                if (look != null) foreach (var p in Peers.Values) SendTo(look, p.EndPoint);
            }
        }

        /// <summary>Your look, re-read from the save every few seconds (it changes when you load a save or recolour).</summary>
        private static byte[] LookPacket(float now)
        {
            _nextLook = now + LookInterval;
            var before = _myLook;
            _myLook = Looks.LocalLook(_myName);
            LogMyLook();
            if (before != null && _myLook != null && !Looks.SameLook(before, _myLook))
            {
                LookChanges++;
                MelonLogger.Msg($"[Online] Your look changed ({Looks.Describe(before)} -> {Looks.Describe(_myLook)}): sent to everyone.");
            }
            return _myLook == null ? null : Proto.Begin(Msg.Look, MyId).Bytes(_myLook).ToArray();
        }

        private static void LogMyLook()
        {
            if (_myLook == null)
            {
                if (_noLookLogged) return;
                _noLookLogged = true;
                MelonLogger.Warning("[Online] Couldn't read your cousin's look from the save (is a save loaded?); others see only your ball.");
                return;
            }
            if (_lookLogged) return;
            _lookLogged = true;
            MelonLogger.Msg($"[Online] Your look: {_myLook.Length} bytes ({Looks.Describe(_myLook)}).");
        }

        private static void ClientTick(float now)
        {
            if (MyId == NoId)
            {
                if (now > _joinDeadline)
                {
                    Leave(Rejoining ? "No answer from the host yet. Trying again..." : "No answer from the host. Check the address and port, and that the host's UDP port is reachable.", true, keepRejoin: Rejoining);
                    return;
                }
                if (now >= _nextHello)
                {
                    _nextHello = now + 1f;
                    Send(Proto.Begin(Msg.Hello, NoId).Str(_myName).Str(_password).Str(MultiplayerCore.Version)
                        .Then(MyIdentity.Write).Bytes(_myTicket?.Data ?? Array.Empty<byte>()), _hostEp);
                }
                return;
            }
            if (now - _hostHeard > Timeout)
            {
                Leave("Lost the connection to the host. Trying to get back in...", true, keepRejoin: true);
                _rejoinUntil = Time.unscaledTime + 60f;
                _nextRejoin = Time.unscaledTime + 1f;
                _rejoinTries = 0;
                return;
            }
            if (now >= _nextState) SendTo(StatePacket(now), _hostEp);
            var hub = HubPacket(now);
            if (hub != null) SendTo(hub, _hostEp);
            if (now >= _nextPing)
            {
                _nextPing = now + PingInterval;
                Send(Proto.Begin(Msg.Ping, MyId).F32(now), _hostEp);
            }
            if (now >= _nextLook)
            {
                var look = LookPacket(now);
                if (look != null) SendTo(look, _hostEp);
            }
        }

        // ---------------------------------------------------------------- messages

        private const float HubInterval = 0.1f;
        private static float _nextHub;
        private static ushort _hubSeq;

        /// <summary>Free roam: where you walk in a hub, ten times a second (null when you're not in one).</summary>
        private static byte[] HubPacket(float now)
        {
            if (now < _nextHub) return null;
            _nextHub = now + HubInterval;
            if (!HubPresence.TryLocal(out var hub, out _)) return null;
            return hub.Write(Proto.Begin(Msg.Hub, MyId).U16(++_hubSeq)).ToArray();
        }

        private static void ReadHub(Peer p, PacketReader r, float now)
        {
            ushort seq = r.U16();
            var s = HubState.Read(r);
            if (p.HubAt > 0f && now - p.HubAt < 2f && (short)(seq - p.HubSeq) <= 0) return;
            if (!float.IsFinite(s.Pos.x) || !float.IsFinite(s.Pos.y) || !float.IsFinite(s.Pos.z) || s.Scale <= 0f || s.Scale > 1e4f) return;
            p.HubSeq = seq;
            p.HubKey = s.Key;
            p.HubPos = s.Pos;
            p.HubYaw = s.Yaw;
            p.HubScale = s.Scale;
            p.HubAnim = s.Anim;
            p.HubPose = s.Pose;
            p.HubAt = now;
        }

        private static byte[] StatePacket(float now)
        {
            bool inMap = Ghosts.TryLocalState(out var s);
            if (!inMap) s.Map = 0;
            s.Activity = Whereabouts.Local();
            _nextState = now + (inMap ? StateInterval : IdleStateInterval);
            return s.Write(Proto.Begin(Msg.State, MyId).U16(++_seq)).ToArray();
        }

        private static void ReadState(Peer p, PacketReader r, float now)
        {
            ushort seq = r.U16();
            var s = KatamariState.Read(r);
            // Drop stale or duplicate packets (wraparound-safe comparison).
            if (p.HasState && (short)(seq - p.Seq) <= 0) return;
            // In a stage the katamari comes as the game's own core packet; anything that doesn't decode is dropped.
            if (s.Map != 0 && !CoreInfoCodec.Decode(ref s)) return;
            if (s.Map != 0) SyncRun.TrackStreamedSize(p.Id, s.Size);
            if (!float.IsFinite(s.Radius) || !float.IsFinite(s.PrinceYaw) || !float.IsFinite(s.TimeLeft) || !float.IsFinite(s.Elapsed)) return;
            s.Radius = Mathf.Clamp(s.Radius, 0f, 10000f);
            s.PrinceScale = Mathf.Clamp(s.PrinceScale, 0f, 1000f);
            p.Seq = seq;
            p.State = s;
            p.Activity = s.Activity;
            p.MapKey = s.Map;
            p.Position = s.CorePos;
            p.Radius = s.Radius;
            p.HasState = true;
            p.StateAt = now;
        }

        /// <summary>
        /// Sends a local pickup with the object's pose in the ball's attach frame. Sent twice: UDP can drop
        /// packets, and receivers ignore repeats.
        /// </summary>
        public static void SendPickup(int map, uint key, Vector3 localPos, Quaternion localRot)
        {
            if (!Connected || !PickupsShared) return;
            var pkt = Proto.Begin(Msg.Pickup, MyId).I32(map).U32(key).Vec(localPos).Quat(localRot).ToArray();
            for (int i = 0; i < 2; i++)
            {
                if (Mode == Role.Host)
                {
                    foreach (var p in Peers.Values) SendTo(pkt, p.EndPoint);
                }
                else
                {
                    SendTo(pkt, _hostEp);
                }
            }
        }

        /// <summary>Host: forwards the packet just received from <paramref name="from"/> to everyone else, stamped with its id.</summary>
        private static void Relay(int len, Peer from)
        {
            if (Peers.Count < 2) return;
            var pkt = new byte[len];
            Array.Copy(Buf, pkt, len);
            pkt[4] = from.Id;
            foreach (var p in Peers.Values)
                if (p != from) SendTo(pkt, p.EndPoint);
        }

        private static void BroadcastRoster()
        {
            var me = MyIdentity;
            var w = Proto.Begin(Msg.Roster, HostId).U8((byte)(Peers.Count + 1)).U8(HostId).Str(_myName).Then(me.Write).U8((byte)(me.IsSteam ? 1 : 0));
            foreach (var p in Peers.Values) w.U8(p.Id).Str(p.Name).Then(p.Identity.Write).U8((byte)(p.Verified ? 1 : 0));
            var pkt = w.ToArray();
            foreach (var p in Peers.Values) SendTo(pkt, p.EndPoint);
        }

        /// <summary>
        /// StartMap flags: 1 = start together, 2 = strict start (held until 3-2-1-GO) instead of race, 4 = the host
        /// restarted, 8 = a new run starts on it now (players already on that stage join it).
        /// </summary>
        private static byte[] MapPacket(MapEntry entry, bool newRun = false) =>
            Proto.Begin(Msg.StartMap, HostId).U8((byte)entry.Kind).I32((int)entry.Stage)
                .U8((byte)((SyncRun.StartTogether ? 1 : 0) | (SyncRun.RaceStart ? 0 : 2) | (newRun ? 8 : 0))).ToArray();

        /// <summary>
        /// Host: a run begins on your stage (a fresh start). Everyone hears it even when it's the same stage as the
        /// last one (a Retry from the results), so they come along instead of being waited for.
        /// </summary>
        public static void AnnounceRun()
        {
            var entry = Ghosts.CurrentEntry();
            if (Mode != Role.Host || entry == null) return;
            _lastMapSent = Ghosts.KeyFor(entry);
            var pkt = MapPacket(entry, newRun: true);
            foreach (var p in Peers.Values) SendTo(pkt, p.EndPoint);
        }

        /// <summary>Host: your stage restarted; players on the same stage restart too, for a fresh run together.</summary>
        public static void BroadcastRestart()
        {
            var entry = Ghosts.CurrentEntry();
            if (Mode != Role.Host || entry == null) return;
            var pkt = Proto.Begin(Msg.StartMap, HostId).U8((byte)entry.Kind).I32((int)entry.Stage)
                .U8((byte)(1 | (SyncRun.RaceStart ? 0 : 2) | 4)).ToArray();
            foreach (var p in Peers.Values) SendTo(pkt, p.EndPoint);
        }

        private static void OnHostRestart(MapKind kind, int stage, float now)
        {
            var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == kind && (int)e.Stage == stage);
            if (entry == null || !FollowHost) return;
            int key = Ghosts.KeyFor(entry);
            SyncRun.ExpectRun(key);
            if (GameRefs.IsMultiplay || IntroSync.BeforeStart(key)) return;
            // Still playing it: restart in place. On its results (or the end demo), or somewhere else: load it fresh,
            // since the game won't restart a stage that's ending.
            var gm = GameRefs.Main;
            bool playing = key == Ghosts.CurrentMapKey && gm != null && !gm._isInClearDemo && !gm._isInGameOver && !gm._isInRestartWait;
            if (playing)
            {
                ModTasks.Run(() => "The host restarted the stage: " + HostAuthority.RestartHere(), toast: true);
                return;
            }
            Follow(entry, key, now, "The host restarted the stage: ");
        }

        private static void Follow(MapEntry entry, int key, float now, string what)
        {
            if (key == _followKey && now < _followUntil) return;
            if (!GameRefs.InGameSession)
            {
                SetInfo($"The host is on {entry.Name}. Load your save to follow.", true);
                return;
            }
            if (entry.NeedsAA && !GameRefs.OwnsAA)
            {
                SetInfo($"The host is on {entry.Name}, which needs DLC you don't have (or have locked).", true);
                return;
            }
            _followKey = key;
            _followUntil = now + 20f;
            ModTasks.Run(() => what + StageGate.Approved(() => SceneLoader.Load(entry)), toast: true);
        }

        /// <summary>Host: removes a player (a passed kick vote). They're told why and can't rejoin automatically.</summary>
        public static void Kick(byte id)
        {
            if (Mode != Role.Host || !Peers.TryGetValue(id, out var p)) return;
            Send(Proto.Begin(Msg.Reject, HostId).Str("You were removed from the session by a vote."), p.EndPoint);
            ForgetIdentity(p);
            Peers.Remove(id);
            SetInfo($"{p.Name} was removed by a vote.");
            Chat.Notice($"{p.Name} was removed by a vote.");
            BroadcastRoster();
        }

        // ---------------------------------------------------------------- for SyncRun

        public static void SendToHost(PacketWriter w)
        {
            if (Mode == Role.Client && _hostEp != null) SendTo(w.ToArray(), _hostEp);
        }

        public static void Broadcast(PacketWriter w)
        {
            if (Mode != Role.Host) return;
            var pkt = w.ToArray();
            foreach (var p in Peers.Values) SendTo(pkt, p.EndPoint);
        }

        public static void SendToPeer(Peer p, PacketWriter w)
        {
            if (Mode == Role.Host && p?.EndPoint != null) SendTo(w.ToArray(), p.EndPoint);
        }

        private static void SendMapTo(Peer peer)
        {
            if (!BringToMyMap) return;
            var entry = Ghosts.CurrentEntry();
            if (entry != null) SendTo(MapPacket(entry), peer.EndPoint);
        }

        /// <summary>Client: the host is on another map. Follow it if allowed (once per map change).</summary>
        private static void OnStartMap(MapKind kind, int stage, bool together, bool newRun, float now)
        {
            var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == kind && (int)e.Stage == stage);
            if (entry == null)
            {
                SetInfo($"The host is on a map this game doesn't have ({kind} {stage}).", true);
                return;
            }
            int key = Ghosts.KeyFor(entry);
            // Starting together: hold at the start of that map until the host's Go.
            if (together && FollowHost) SyncRun.ExpectRun(key);
            if (key == Ghosts.CurrentMapKey)
            {
                // A new run on the stage you're on, but you're past its start (playing, or on its results): load it
                // again to join (a Retry from the host's results). At its start already: you'll be held there.
                if (newRun && together && FollowHost && !GameRefs.IsMultiplay && !IntroSync.BeforeStart(key))
                    Follow(entry, key, now, "The host started it again: ");
                return;
            }
            // Already on the way there from a fan's stage screen (the King's stage start is loading it).
            if (key == PlazaFlow.StartingKey) return;
            if (!FollowHost)
            {
                SetInfo($"The host went to {entry.Name}.");
                return;
            }
            Follow(entry, key, now, "Following the host: ");
        }

        /// <summary>Host: <paramref name="name"/>, or with a number after it when you or another player already has it.</summary>
        private static string UniqueName(string name)
        {
            bool Taken(string n) => n == _myName || Peers.Values.Any(p => p.Name == n);
            if (!Taken(name)) return name;
            string stem = name.Length > Proto.MaxName - 2 ? name.Substring(0, Proto.MaxName - 2).TrimEnd() : name;
            for (int i = 2; i <= MaxPlayers + 1; i++)
            {
                string n = $"{stem} {i}";
                if (!Taken(n)) return n;
            }
            return name;
        }

        private static byte FreeId()
        {
            for (byte i = 1; i < MaxPlayers; i++)
                if (!Peers.ContainsKey(i)) return i;
            return NoId;
        }

        private static void Send(PacketWriter w, IPEndPoint to) => SendTo(w.ToArray(), to);

        /// <summary>Host: a player left; Steam stops checking their ticket.</summary>
        private static void ForgetIdentity(Peer p)
        {
            if (Mode == Role.Host && p != null && p.Verified && p.Identity.IsSteam) OUAKModAPI.Steam.EndVerify(p.Identity.Value);
        }

        /// <summary>Host: passes a packet on to one player (or everyone but the sender when <paramref name="to"/> is 255).</summary>
        private static void RelayTo(int len, Peer from, byte to)
        {
            var pkt = new byte[len];
            Array.Copy(Buf, pkt, len);
            pkt[4] = from.Id;
            foreach (var p in Peers.Values)
                if (p != from && (to == 255 || p.Id == to)) SendTo(pkt, p.EndPoint);
        }

        private static void SendTo(byte[] pkt, IPEndPoint to)
        {
            if (_sock == null || to == null) return;
            try { _sock.SendTo(pkt, to); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private static void SetInfo(string msg, bool error = false)
        {
            Info = msg;
            InfoIsError = error;
            if (error) MelonLogger.Warning("[Online] " + msg);
            else MelonLogger.Msg("[Online] " + msg);
        }

        // ---------------------------------------------------------------- loopback test

        /// <summary>
        /// A fake second player for testing on one PC: a real UDP client on 127.0.0.1 that joins the local host,
        /// circles the local katamari and "rolls up" (sends a pickup for) one nearby object a second.
        /// Everything it does goes through the same packets a real player's game sends.
        /// </summary>
        private sealed class LoopbackBot
        {
            private readonly Socket _s;
            private readonly IPEndPoint _host;
            private readonly byte[] _buf = new byte[8192];
            private float _grow = 1f;
            private byte _id = NoId;
            private float _nextHello, _nextSend, _nextBite, _nextLook, _nextReady, _angle;
            private Quaternion _rot = Quaternion.identity;
            private Vector3 _lastPos;
            private ushort _seq;
            private byte _goRun;
            private int _goMap, _resultsSent, _picks;
            private float _goStartAt;
            /// <summary>The bot finishes this long after the start (or when you do), so a test shows the hurry cut-off.</summary>
            private const float BotFinishAfter = 90f;
            private float _nextHubSend, _hubAngle;
            private ushort _hubSeq;
            private byte _gatherId;
            private bool _gatherGo;
            private float _gatherSeen, _nextGatherSend, _chatReplyAt;
            private string _chatHeard = "";
            // Voice: what it heard (to say back), and its own made-up voice.
            private readonly List<byte[]> _echo = new();
            private bool _echoEnded;
            private float _echoHeardAt, _echoAt, _talkStart = -1f, _talkSeconds;
            private int _echoSent, _talkSent;
            private ushort _voiceSeq;
            private Adpcm.State _talkEnc;
            private double _talkPhase;

            public byte Id => _id;
            private readonly ulong _botIdentity = (ulong)DateTime.UtcNow.Ticks | 1;
            private bool _leave;
            private int _dqSent;

            public string LeaveRace()
            {
                if (_id == NoId) return "the bot hasn't joined yet";
                _leave = true;
                _dqSent = 0;
                return "the bot leaves the race";
            }

            public string Propose(VoteKind kind, int arg)
            {
                if (_id == NoId) return "the bot hasn't joined yet";
                Send(Proto.Begin(Msg.VoteAsk, _id).U8((byte)kind).I32(arg).I32(0));
                return $"the bot asked for a vote to {Votes.Describe(kind, arg)}";
            }

            public string Talk(float seconds)
            {
                if (_id == NoId) return "the bot hasn't joined yet";
                _talkStart = Time.unscaledTime;
                _talkSeconds = Mathf.Clamp(seconds, 0.2f, 10f);
                _talkSent = 0;
                _talkEnc = default;
                _talkPhase = 0;
                return $"the bot talks for {_talkSeconds:0.#} s (a made-up voice)";
            }
            /// <summary>Gathering: how long the bot "picks its music" before it's ready.</summary>
            private const float BotReadyAfter = 4f;

            public LoopbackBot(int port)
            {
                _s = OpenSocket(new IPEndPoint(IPAddress.Loopback, 0));
                _host = new IPEndPoint(IPAddress.Loopback, port);
            }

            public void Tick(float now)
            {
                for (int n = 0; n < 64; n++)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int len;
                    try
                    {
                        if (_s.Available <= 0) break;
                        len = _s.ReceiveFrom(_buf, ref from);
                    }
                    catch (SocketException) { continue; }
                    try
                    {
                        var r = new PacketReader(_buf, len);
                        if (!Proto.TryHeader(r, out var type, out _)) continue;
                        if (type == Msg.Welcome) _id = r.U8();
                        else if (type == Msg.Ping) Send(Proto.Begin(Msg.Pong, _id).F32(r.F32()).F32(now));
                        else if (type == Msg.Go)
                        {
                            byte run = r.U8();
                            int map = r.I32();
                            float startAt = r.F32();
                            if (run != _goRun || map != _goMap) { _goRun = run; _goMap = map; _goStartAt = startAt; _resultsSent = 0; _picks = 0; _leave = false; _dqSent = 0; }
                        }
                        // A test player: it agrees to every vote (unless told not to), so votes can be tried alone.
                        else if (type == Msg.VoteStart) Send(Proto.Begin(Msg.VoteCast, _id).U8(r.U8()).U8((byte)(BotVotesNo ? 0 : 1)));
                        // Gathering at a fan's stage screen: it "picks its music" for a few seconds, then is ready.
                        else if (type == Msg.Gather)
                        {
                            byte gid = r.U8();
                            r.I32();
                            r.I32();
                            r.F32();
                            bool go = r.U8() != 0;
                            if (gid != _gatherId) { _gatherId = gid; _gatherSeen = now; }
                            _gatherGo = go;
                        }
                        // Voice: it keeps what it hears, to say it back once the talk ends.
                        else if (type == Msg.Voice)
                        {
                            r.U16();
                            bool end = (r.U8() & Voice.FlagEnd) != 0;
                            var data = r.Bytes();
                            if (BotEcho && _echoAt <= 0f && _echo.Count < 250)
                            {
                                if (data.Length > 3) _echo.Add(data);
                                _echoHeardAt = now;
                                if (end) _echoEnded = true;
                            }
                        }
                        // Chat: it answers what someone says, so both directions can be tried alone.
                        else if (type == Msg.Chat)
                        {
                            byte speaker = r.U8();
                            string said = r.Str();
                            if (speaker != _id && speaker != Chat.SystemId && said.Length > 0)
                            {
                                _chatHeard = said.Length > 40 ? said.Substring(0, 40) + "..." : said;
                                _chatReplyAt = now + 0.8f;
                            }
                        }
                    }
                    catch (EndOfStreamException) { }
                }
                if (_id != NoId && _chatReplyAt > 0f && now >= _chatReplyAt)
                {
                    _chatReplyAt = 0f;
                    Send(Proto.Begin(Msg.Chat, _id).Str($"Got it: \"{_chatHeard}\""));
                }
                if (_id != NoId) TickVoice(now);
                if (_id != NoId && _gatherId != 0 && !_gatherGo && now >= _nextGatherSend && now - _gatherSeen < 60f)
                {
                    _nextGatherSend = now + 1f;
                    byte gatherState = now - _gatherSeen >= BotReadyAfter ? PlazaFlow.IsReady : now - _gatherSeen >= 1f ? PlazaFlow.OnScreen : PlazaFlow.OnTheWay;
                    Send(Proto.Begin(Msg.GatherState, _id).U8(_gatherId).U8(gatherState));
                }

                if (_id == NoId)
                {
                    if (now >= _nextHello)
                    {
                        _nextHello = now + 1f;
                        Send(Proto.Begin(Msg.Hello, NoId).Str("Loopback bot").Str(_password).Str(MultiplayerCore.Version)
                            .Then(new PlayerIdentity { Kind = PlayerIdentity.Bot, Value = _botIdentity }.Write).Bytes(Array.Empty<byte>()));
                    }
                    return;
                }
                if (now >= _nextLook)
                {
                    // It looks like you: your own cousin, through the same Look packet a friend's game sends.
                    _nextLook = now + LookInterval;
                    // Your look as it is now (the test bot copies you, a change of yours included).
                    var look = Looks.LocalLook(_myName) ?? _myLook;
                    if (_myLook == null)
                    {
                        _myLook = look;
                        LogMyLook();
                    }
                    if (look != null) Send(Proto.Begin(Msg.Look, _id).Bytes(look));
                }
                // In a hub: walk circles around you, through the same Hub packets a real player's game sends.
                if (now >= _nextHubSend && HubPresence.TryLocal(out var hub, out float height))
                {
                    _nextHubSend = now + HubInterval;
                    float ring = Mathf.Max(0.2f, height * 2.5f);
                    _hubAngle += HubInterval * 0.6f;
                    var off = new Vector3(Mathf.Cos(_hubAngle), 0f, Mathf.Sin(_hubAngle)) * ring;
                    var walk = new Vector3(-Mathf.Sin(_hubAngle), 0f, Mathf.Cos(_hubAngle));
                    var bot = new HubState
                    {
                        Key = hub.Key, Pos = hub.Pos + off, Yaw = Quaternion.LookRotation(walk).eulerAngles.y,
                        Scale = hub.Scale, Anim = HubPresence.WalkAnim,
                    };
                    Send(bot.Write(Proto.Begin(Msg.Hub, _id).U16(++_hubSeq)));
                }
                if (now < _nextSend) return;
                _nextSend = now + StateInterval;

                bool inMap = Ghosts.TryLocalState(out var me);
                _angle += StateInterval * 0.9f;
                float orbit = Mathf.Max(1.2f, me.Radius * 4f);
                var botPos = me.CorePos + new Vector3(Mathf.Cos(_angle) * orbit, 0f, Mathf.Sin(_angle) * orbit);
                // Starts a bit smaller than you and grows with every object it rolls up.
                float botRadius = Mathf.Max(0.05f, me.Radius * 0.8f * _grow);

                // Roll the ball along its path, and put the prince behind it the way yours stands behind yours.
                var move = botPos - _lastPos;
                move.y = 0f;
                var dir = move.sqrMagnitude > 1e-6f ? move.normalized : Vector3.forward;
                if (move.sqrMagnitude > 1e-6f && move.sqrMagnitude < orbit * orbit)
                    _rot = Quaternion.AngleAxis(move.magnitude / botRadius * Mathf.Rad2Deg, Vector3.Cross(Vector3.up, dir)) * _rot;
                _lastPos = botPos;
                var flat = me.PrincePos - me.CorePos;
                flat.y = 0f;
                float behind = me.Radius > 0f ? flat.magnitude / me.Radius * botRadius : botRadius * 1.4f;
                int size = (int)(me.Size * 0.8f * _grow);
                var state = new KatamariState
                {
                    Map = inMap ? me.Map : 0,
                    // The same Katamari Ball core packet a real player's game sends. Its prince pushes forward as it
                    // rolls round (OUJI_MOT_WALK_F at normal speed): copying your move made it slide when you stood still.
                    CoreInfo = inMap ? CoreInfoCodec.Encode(botPos, 0f, _rot, (int)Il2CppApp.KatamariSin.OUJI_MOT.OUJI_MOT_WALK_F, 1f, 0, size, 0f) : null,
                    Radius = botRadius,
                    PrincePos = botPos - dir * behind + Vector3.up * (me.PrincePos.y - me.CorePos.y),
                    PrinceYaw = Quaternion.LookRotation(dir).eulerAngles.y,
                    PrinceScale = me.PrinceScale,
                    TimerRule = me.TimerRule,
                    TimeLeft = me.TimeLeft,
                    Elapsed = me.Elapsed,
                    // Drawn like a real core: your core's scale for its radius, its objects hung in its spinning frame.
                    CoreScale = me.Radius > 0f ? me.CoreScale * botRadius / me.Radius : me.CoreScale,
                    AttachRot = _rot,
                    AttachScale = 1f,
                    Converge = me.Converge,
                    // Wherever you are; finished once it has sent its result while the run goes on.
                    Activity = BotActivity(inMap),
                    // Your clock, so it stops when yours does.
                    Flags = me.Flags,
                    Pose = BotMirrorPose == 1 ? me.Pose
                         : BotMirrorPose == 2 ? new PoseSync { Hash = me.Pose.Hash, Time = 0.35f, Speed = 0f, Face = -1 }
                         : PoseSync.None,
                    // Your timers, a second behind: it reaches the win condition a second after you do.
                    Igt = Math.Max(0f, me.Igt - 1f),
                    ClearIgt = me.ClearIgt >= 0f ? me.ClearIgt + 1f : -1f,
                    ClearRta = me.ClearRta >= 0f ? me.ClearRta + 1f : -1f,
                    ClearStage = me.ClearStage >= 0f ? me.ClearStage + 1f : -1f,
                    HudRank = me.HudRank,
                    // Its stage score: on size stages its own size and the rating that size earns (as a real player's
                    // would be); on other stages yours, a little behind.
                    StageRate = (ScoreKind)me.ScoreKind == ScoreKind.Size ? StageScore.RateForSize(GameRefs.Main, size) : me.StageRate,
                    ScoreKind = me.ScoreKind,
                    Score = (ScoreKind)me.ScoreKind == ScoreKind.Size ? size
                          : me.Score < 0 ? -1 : StageScore.LowerIsBetter((ScoreKind)me.ScoreKind) ? me.Score + 1 : Math.Max(0, me.Score - 1),
                };
                Send(state.Write(Proto.Begin(Msg.State, _id).U16(++_seq)));

                // Start together: Ready while on the map without a start signal; when your run ends, submit ours.
                if (inMap && (_goMap != me.Map || _goRun != SyncRun.RunId) && now >= _nextReady)
                {
                    _nextReady = now + 1f;
                    Send(Proto.Begin(Msg.Ready, _id).I32(me.Map).I32(Ghosts.IndexedObjects));
                }
                var mine = SyncRun.MyResult;
                float botTime = SyncRun.HostNow - _goStartAt;
                // Left the race: a DQ result (what "Only me" back to the Plaza sends), instead of finishing.
                if (_leave && _goRun == SyncRun.RunId && _dqSent < 3 && now >= _nextReady)
                {
                    _dqSent++;
                    _nextReady = now + 0.5f;
                    var dq = new RunResult { Size = size, Time = botTime, RealTime = botTime, TimeAttack = me.TimerRule == 1, Disqualified = true };
                    Send(dq.Write(Proto.Begin(Msg.Result, _id).U8(_goRun).I32(_goMap)));
                }
                bool botDone = !_leave && (mine != null || (SyncRun.Phase == RunPhase.Running && _goStartAt > 0f && botTime >= BotFinishAfter));
                if (botDone && inMap && _goMap == me.Map && _goRun == SyncRun.RunId && _resultsSent < 3)
                {
                    if (_resultsSent++ == 0) MelonLogger.Msg($"[Online] Test bot finished ({KatamariState.SizeText(size)}).");
                    // Its stats: what it did, plus your growth times a little slower, so the report has rows to show.
                    var r = new RunResult
                    {
                        Size = size,
                        Time = mine?.Time ?? botTime,
                        RealTime = mine?.RealTime ?? botTime,
                        Cleared = mine?.Cleared ?? true,
                        TimeAttack = me.TimerRule == 1,
                        Objects = _picks,
                        Dashes = _picks / 3,
                        Turns = _picks / 5,
                        Freebies = mine?.Freebies ?? 0,
                        Distance = _angle * Mathf.Max(1.2f, me.Radius * 4f),
                        // Its timers a second behind yours (what it streamed), and its stage score by its own size.
                        Igt = (mine != null && mine.Igt >= 0f ? mine.Igt : me.Igt) + 1f,
                        ClearIgt = me.ClearIgt >= 0f ? me.ClearIgt + 1f : -1f,
                        ClearRta = me.ClearRta >= 0f ? me.ClearRta + 1f : -1f,
                        ScoreKind = mine?.ScoreKind ?? me.ScoreKind,
                    };
                    r.Score = (ScoreKind)r.ScoreKind == ScoreKind.Size ? size : mine?.Score ?? me.Score;
                    if ((ScoreKind)r.ScoreKind == ScoreKind.Size) r.Rank = StageScore.RateForSize(GameRefs.Main, size);
                    foreach (var st in (mine?.Steps ?? RunStats.Steps).Take(16)) r.Steps.Add((st.size, st.time * 1.1f));
                    // Rank and Size Time: yours (a little slower), or what your game rates right now.
                    if (mine != null)
                    {
                        r.Rank = mine.Rank;
                        r.RankIcon = mine.RankIcon;
                        r.SizeTime = mine.SizeTime >= 0f ? mine.SizeTime * 1.1f : -1f;
                    }
                    else
                    {
                        RunStats.ReadRank(r);
                        if (r.SizeTime >= 0f) r.SizeTime *= 1.1f;
                    }
                    Send(r.Write(Proto.Begin(Msg.Result, _id).U8(_goRun).I32(_goMap)));
                }

                if (inMap && PickupsShared && now >= _nextBite)
                {
                    _nextBite = now + 1f;
                    // Only what a katamari its size could roll up (the game's own MainGameManager.GetMonoRequireSize).
                    uint key = Ghosts.FindObjectNear(botPos, botRadius + 0.4f, size);
                    if (key != 0 && Ghosts.TryObjectPose(key, out var pos, out var rot))
                    {
                        // Stuck where it touched the ball, as a local pose in its attach frame (Katamari Ball's NetworkMonoGetInfo).
                        var inv = Quaternion.Inverse(_rot);
                        var onBall = (pos - botPos).sqrMagnitude > 1e-6f ? (pos - botPos).normalized * botRadius : Vector3.up * botRadius;
                        Send(Proto.Begin(Msg.Pickup, _id).I32(me.Map).U32(key).Vec(inv * onBall).Quat(inv * rot));
                        _grow = Mathf.Min(_grow * 1.04f, 3f);
                        _picks++;
                    }
                }
            }

            /// <summary>
            /// Voice: says back what it heard 0.8 s after the talk ends (an echo, so voice chat can be tried alone),
            /// and talks with a made-up voice when asked (the self-test). Both at the real pace, 40 ms a packet.
            /// </summary>
            private void TickVoice(float now)
            {
                if (_echo.Count > 0 && _echoAt <= 0f && (_echoEnded || now - _echoHeardAt > 0.6f))
                {
                    _echoAt = now + 0.8f;
                    _echoSent = 0;
                }
                if (_echoAt > 0f && now >= _echoAt)
                {
                    int due = Math.Min(_echo.Count, (int)((now - _echoAt) / 0.04f) + 1);
                    while (_echoSent < due)
                    {
                        var data = _echo[_echoSent++];
                        bool last = _echoSent >= _echo.Count;
                        Send(Proto.Begin(Msg.Voice, _id).U16(++_voiceSeq).U8(last ? Voice.FlagEnd : (byte)0).Bytes(data));
                    }
                    if (_echoSent >= _echo.Count)
                    {
                        BotEchoes++;
                        _echo.Clear();
                        _echoEnded = false;
                        _echoAt = 0f;
                    }
                }
                if (_talkStart >= 0f)
                {
                    int total = Mathf.Max(1, (int)(_talkSeconds * Voice.Rate / Voice.Frame));
                    int due = Math.Min(total, (int)((now - _talkStart) * Voice.Rate / Voice.Frame) + 1);
                    while (_talkSent < due)
                    {
                        var samples = new float[Voice.Frame];
                        Voice.TestSignal(samples, _talkSent * Voice.Frame, ref _talkPhase);
                        var data = Adpcm.Encode(samples, samples.Length, ref _talkEnc);
                        _talkSent++;
                        Send(Proto.Begin(Msg.Voice, _id).U16(++_voiceSeq).U8(_talkSent >= total ? Voice.FlagEnd : (byte)0).Bytes(data));
                    }
                    if (_talkSent >= total) _talkStart = -1f;
                }
            }

            private Activity BotActivity(bool inMap)
            {
                var mine = Whereabouts.Local();
                if (!inMap) return mine == Activity.GettingReady ? Activity.Plaza : mine;
                if (SyncRun.StandingsFinal) return Activity.Results;
                return _resultsSent > 0 && _goRun == SyncRun.RunId ? Activity.Spectating : Activity.Playing;
            }

            private void Send(PacketWriter w)
            {
                try { _s.SendTo(w.ToArray(), _host); }
                catch (SocketException) { }
            }

            public void Stop()
            {
                if (_id != NoId) Send(Proto.Begin(Msg.Bye, _id));
                try { _s.Close(); } catch { }
            }
        }
    }
}
