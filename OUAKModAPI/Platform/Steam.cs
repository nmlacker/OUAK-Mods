using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OUAKModAPI
{
    /// <summary>A Steam session ticket (<see cref="Steam.CreateTicket"/>): send <see cref="Data"/> to whoever verifies you.</summary>
    public sealed class SteamTicket
    {
        public byte[] Data;
        public uint Handle;
        /// <summary>When it was made (Time.unscaledTime); Steam needs a moment before a new ticket verifies.</summary>
        public float CreatedAt;
    }

    /// <summary>What Steam said about a ticket (ISteamUser::BeginAuthSession's result, plus Unavailable).</summary>
    public enum SteamAuthResult
    {
        OK = 0,
        InvalidTicket = 1,
        DuplicateRequest = 2,
        InvalidVersion = 3,
        GameMismatch = 4,
        ExpiredTicket = 5,
        /// <summary>Steam isn't running here (or the API couldn't be reached), so nothing was checked.</summary>
        Unavailable = 100,
    }

    /// <summary>
    /// Your Steam account, through the game's own Steam library (steam_api64.dll, already loaded and started by
    /// the game; nothing here starts Steam or talks to the game's online service):
    /// <list type="bullet">
    /// <item><see cref="MySteamId"/> / <see cref="MyName"/>: who is playing.</item>
    /// <item><see cref="CreateTicket"/> / <see cref="Verify"/>: Steam session tickets, the standard way for one player's
    /// game to check that another player really is the Steam account they claim (Steam signs the ticket).</item>
    /// <item><see cref="NameOf"/>: a Steam friend's (or a recently seen player's) persona name.</item>
    /// </list>
    /// Everything returns empty values or <see cref="SteamAuthResult.Unavailable"/> when Steam isn't there.
    /// </summary>
    public static unsafe class Steam
    {
        private static bool _tried;
        private static IntPtr _user, _friends;
        private static delegate* unmanaged[Cdecl]<IntPtr, ulong> _getSteamId;
        private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, uint*, void*, uint> _getTicket;
        private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, ulong, int> _beginAuth;
        private static delegate* unmanaged[Cdecl]<IntPtr, ulong, void> _endAuth;
        private static delegate* unmanaged[Cdecl]<IntPtr, uint, void> _cancelTicket;
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _personaName;
        private static delegate* unmanaged[Cdecl]<IntPtr, ulong, IntPtr> _friendName;
        private static delegate* unmanaged[Cdecl]<IntPtr, ulong, byte, byte> _requestInfo;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>True when the game's Steam library is loaded and you're logged in.</summary>
        public static bool Available
        {
            get
            {
                Bind();
                return _user != IntPtr.Zero && _getSteamId != null && MySteamId != 0;
            }
        }

        /// <summary>Your 64-bit SteamID, or 0.</summary>
        public static ulong MySteamId
        {
            get
            {
                Bind();
                if (_user == IntPtr.Zero || _getSteamId == null) return 0;
                try { return _getSteamId(_user); } catch { return 0; }
            }
        }

        /// <summary>Your Steam persona name, or "".</summary>
        public static string MyName
        {
            get
            {
                Bind();
                if (_friends == IntPtr.Zero || _personaName == null) return "";
                try { return Utf8(_personaName(_friends)); } catch { return ""; }
            }
        }

        /// <summary>
        /// The persona name Steam knows for <paramref name="steamId"/> (friends, and players seen recently), or "".
        /// For someone Steam doesn't know yet it asks Steam (call again a moment later).
        /// </summary>
        public static string NameOf(ulong steamId)
        {
            Bind();
            if (steamId == 0 || _friends == IntPtr.Zero || _friendName == null) return "";
            try
            {
                string n = Utf8(_friendName(_friends, steamId));
                if (string.IsNullOrEmpty(n) || n == "[unknown]")
                {
                    if (_requestInfo != null) _requestInfo(_friends, steamId, 1);
                    return "";
                }
                return n;
            }
            catch { return ""; }
        }

        /// <summary>
        /// A new session ticket for this game (ISteamUser::GetAuthSessionTicket), or null without Steam. Cancel it
        /// with <see cref="CancelTicket"/> when you leave. Steam needs about a second before a new ticket verifies.
        /// </summary>
        public static SteamTicket CreateTicket()
        {
            Bind();
            if (_user == IntPtr.Zero || _getTicket == null) return null;
            try
            {
                var buf = new byte[2048];
                uint len = 0;
                // An empty identity: a ticket any player's game can check (not tied to one recipient).
                byte* identity = stackalloc byte[160];
                for (int i = 0; i < 160; i++) identity[i] = 0;
                uint handle;
                fixed (byte* p = buf) handle = _getTicket(_user, p, buf.Length, &len, identity);
                // Older Steam libraries (or a refusal of the empty identity): no identity at all.
                if (handle == 0) fixed (byte* p = buf) handle = _getTicket(_user, p, buf.Length, &len, null);
                if (handle == 0 || len == 0 || len > buf.Length) return null;
                var data = new byte[len];
                Buffer.BlockCopy(buf, 0, data, 0, (int)len);
                return new SteamTicket { Data = data, Handle = handle, CreatedAt = UnityEngine.Time.unscaledTime };
            }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"Steam ticket: {e.Message}");
                return null;
            }
        }

        /// <summary>Tells Steam a ticket you made is no longer in use.</summary>
        public static void CancelTicket(SteamTicket ticket)
        {
            if (ticket == null || ticket.Handle == 0) return;
            Bind();
            try { if (_user != IntPtr.Zero && _cancelTicket != null) _cancelTicket(_user, ticket.Handle); } catch { }
            ticket.Handle = 0;
        }

        /// <summary>
        /// Checks another player's ticket (ISteamUser::BeginAuthSession): OK means Steam signed it for this game
        /// and for <paramref name="claimedSteamId"/>. Call <see cref="EndVerify"/> when that player leaves.
        /// A player who reconnects sends a new ticket; the old check is ended and replaced.
        /// </summary>
        public static SteamAuthResult Verify(byte[] ticket, ulong claimedSteamId)
        {
            Bind();
            if (_user == IntPtr.Zero || _beginAuth == null) return SteamAuthResult.Unavailable;
            if (ticket == null || ticket.Length == 0 || claimedSteamId == 0) return SteamAuthResult.InvalidTicket;
            try
            {
                int r;
                fixed (byte* p = ticket) r = _beginAuth(_user, p, ticket.Length, claimedSteamId);
                if (r == (int)SteamAuthResult.DuplicateRequest)
                {
                    // Already checked once (a rejoin): start over with the new ticket.
                    if (_endAuth != null) _endAuth(_user, claimedSteamId);
                    fixed (byte* p = ticket) r = _beginAuth(_user, p, ticket.Length, claimedSteamId);
                }
                return Enum.IsDefined(typeof(SteamAuthResult), r) ? (SteamAuthResult)r : SteamAuthResult.InvalidTicket;
            }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"Steam verify: {e.Message}");
                return SteamAuthResult.Unavailable;
            }
        }

        /// <summary>Ends Steam's check of a player (ISteamUser::EndAuthSession), when they leave.</summary>
        public static void EndVerify(ulong steamId)
        {
            Bind();
            if (steamId == 0 || _user == IntPtr.Zero || _endAuth == null) return;
            try { _endAuth(_user, steamId); } catch { }
        }

        /// <summary>A readable reason for a verification result.</summary>
        public static string Describe(SteamAuthResult r) => r switch
        {
            SteamAuthResult.OK => "verified by Steam",
            SteamAuthResult.InvalidTicket => "Steam ticket not valid",
            SteamAuthResult.DuplicateRequest => "Steam ticket already used",
            SteamAuthResult.InvalidVersion => "Steam ticket from another Steam version",
            SteamAuthResult.GameMismatch => "Steam ticket for another game",
            SteamAuthResult.ExpiredTicket => "Steam ticket expired",
            _ => "Steam not available",
        };

        private static void Bind()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                var mod = GetModuleHandleW("steam_api64.dll");
                if (mod == IntPtr.Zero)
                {
                    ApiCore.Log.Info("Steam: steam_api64.dll isn't loaded; SteamIDs are unavailable.");
                    return;
                }
                IntPtr F(string n) => GetProcAddress(mod, n);
                IntPtr user = IntPtr.Zero;
                foreach (string v in new[] { "SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v020" })
                {
                    var acc = F(v);
                    if (acc == IntPtr.Zero) continue;
                    user = ((delegate* unmanaged[Cdecl]<IntPtr>)acc)();
                    if (user != IntPtr.Zero) break;
                }
                var friendsAcc = F("SteamAPI_SteamFriends_v017");
                _friends = friendsAcc != IntPtr.Zero ? ((delegate* unmanaged[Cdecl]<IntPtr>)friendsAcc)() : IntPtr.Zero;
                _user = user;
                _getSteamId = (delegate* unmanaged[Cdecl]<IntPtr, ulong>)F("SteamAPI_ISteamUser_GetSteamID");
                _getTicket = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, uint*, void*, uint>)F("SteamAPI_ISteamUser_GetAuthSessionTicket");
                _beginAuth = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, ulong, int>)F("SteamAPI_ISteamUser_BeginAuthSession");
                _endAuth = (delegate* unmanaged[Cdecl]<IntPtr, ulong, void>)F("SteamAPI_ISteamUser_EndAuthSession");
                _cancelTicket = (delegate* unmanaged[Cdecl]<IntPtr, uint, void>)F("SteamAPI_ISteamUser_CancelAuthTicket");
                _personaName = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)F("SteamAPI_ISteamFriends_GetPersonaName");
                _friendName = (delegate* unmanaged[Cdecl]<IntPtr, ulong, IntPtr>)F("SteamAPI_ISteamFriends_GetFriendPersonaName");
                _requestInfo = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte, byte>)F("SteamAPI_ISteamFriends_RequestUserInformation");
                ApiCore.Log.Info(_user != IntPtr.Zero
                    ? "Steam: connected to the game's Steam library."
                    : "Steam: the game's Steam library isn't started (no SteamUser); SteamIDs are unavailable.");
            }
            catch (Exception e)
            {
                _user = IntPtr.Zero;
                ApiCore.Log.Warn($"Steam: {e.Message}");
            }
        }

        private static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int n = 0;
            while (n < 1024 && Marshal.ReadByte(p, n) != 0) n++;
            var bytes = new byte[n];
            Marshal.Copy(p, bytes, 0, n);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
