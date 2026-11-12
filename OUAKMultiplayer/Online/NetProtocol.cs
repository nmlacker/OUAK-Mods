using System;
using System.IO;
using System.Text;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    internal enum Msg : byte
    {
        Hello = 1,     // client -> host: name, password, mod version, identity (PlayerIdentity: kind, SteamID or install id), Steam session ticket
        Welcome = 2,   // host -> client: your player id
        Reject = 3,    // host -> client: reason
        Roster = 4,    // host -> all: (id, name, identity, verified) for everyone in the session
        State = 5,     // anyone -> host -> others: map key, katamari position/radius/spin, prince pose and animation
        Pickup = 6,    // anyone -> host -> others: map key, object key, where it sits on the ball
        StartMap = 7,  // host -> clients: map kind and stage
        Ping = 8,
        Pong = 9,
        Bye = 10,
        Look = 11,     // anyone -> host -> others: the game's own NetworkUserInfo bytes (cousin, colours, presents)
        Ready = 12,    // client -> host: my stage is loaded (map key, object count)
        Go = 13,       // host -> all: run id, map key, start time on the host's clock
        Result = 14,   // player -> host: run id, map key, RunResult (size, times, flags, stage rank, Size Time, after-action stats); resent until acknowledged
        Standings = 15,// host -> all: everyone's RunResult with the host's verdicts
        VoteAsk = 16,  // client -> host: please start a vote (kind, argument, second argument: where in a plaza it was picked)
        VoteStart = 17,// host -> all: vote id, kind, argument, deadline (host clock), proposer, second argument
        VoteCast = 18, // anyone -> host: vote id, yes/no
        VoteResult = 19,// host -> all: vote id, passed, yes, no, total
        Hurry = 20,    // host -> all: Sudden Death: run id, fastest finish (game / real time), the players everyone's waiting on, seconds per growth
        Settings = 21, // host -> all: the session's settings (flags: 1 = picked-up objects are shared, 2 = Sudden Death; then the lobby settings)
        Hub = 22,      // anyone -> host -> others: where you walk in a hub (plaza / ship): HubState
        VoteDenied = 23,// host -> proposer: your last vote didn't pass; seconds until you can start another
        Gather = 24,   // host -> all: a stage picked with a fan is on: id, map key, PlazaSpot, deadline (host clock, 0 = still arriving), go, everyone's state
        GatherState = 25,// client -> host: id, my state (0 on the way, 1 on the stage screen, 2 ready, 3 not coming)
        Chat = 26,     // client -> host: text; host -> others: sender id (254 = the host's notice), text
        Voice = 27,    // anyone -> host -> others: seq (u16), flags (1 = last of a talk), 40 ms of 16 kHz IMA ADPCM (Online/Voice)
        RunCancel = 28,// host -> all: run id: the host left before that run started, so nobody waits for it
        HostPlaza = 29,// host -> all: the host sends everyone back to the plaza (plaza index); players in a stage leave it for that plaza
        ModData = 30,  // anyone -> host -> others: target id (255 = all), channel, data: other mods' messages (OUAKModAPI.Multiplayer)
    }

    /// <summary>
    /// Who a player is, beyond the name they typed: their Steam account (checked by the host with a Steam session
    /// ticket), or without Steam a random id made once per install. The host keys players by it, so the same person
    /// can never be in the session twice (a rejoin takes their old place), and nobody can take a verified player's place.
    /// </summary>
    internal struct PlayerIdentity : IEquatable<PlayerIdentity>
    {
        public const byte None = 0, SteamAccount = 1, InstallId = 2, Bot = 3;
        public byte Kind;
        public ulong Value;

        public bool IsSet => Kind != None && Value != 0;
        public bool IsSteam => Kind == SteamAccount && Value != 0;

        public PacketWriter Write(PacketWriter w) => w.U8(Kind).U64(Value);
        public static PlayerIdentity Read(PacketReader r) => new() { Kind = r.U8(), Value = r.U64() };

        public bool Equals(PlayerIdentity o) => Kind == o.Kind && Value == o.Value;
        public override bool Equals(object obj) => obj is PlayerIdentity o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Kind, Value);
        public override string ToString() => Kind switch
        {
            SteamAccount => "Steam",
            InstallId => "no Steam",
            Bot => "test bot",
            _ => "unknown",
        };
    }

    /// <summary>
    /// Packet layout: magic (u16) · protocol version (u8) · message (u8) · sender id (u8) · payload.
    /// Strings are u8-length-prefixed UTF-8. Everything is read with bounds checks; a short or malformed
    /// packet just fails to parse and is dropped.
    /// </summary>
    internal static class Proto
    {
        public const ushort Magic = 0x4B4F;
        public const byte Version = 16;
        /// <summary>Look packets carry the game's NetworkUserInfo bytes, whose size depends on game tables.</summary>
        public const int MaxPacket = 4096;
        public const int MaxName = 16;

        public static PacketWriter Begin(Msg type, byte sender)
        {
            var w = new PacketWriter();
            w.U16(Magic);
            w.U8(Version);
            w.U8((byte)type);
            w.U8(sender);
            return w;
        }

        /// <summary>Parses the header. False when the packet isn't ours (or is from another protocol version).</summary>
        public static bool TryHeader(PacketReader r, out Msg type, out byte sender)
        {
            type = 0;
            sender = 0;
            if (r.U16() != Magic || r.U8() != Version) return false;
            type = (Msg)r.U8();
            sender = r.U8();
            return Enum.IsDefined(typeof(Msg), type);
        }

        public static string CleanName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
                if (!char.IsControl(c) && sb.Length < MaxName) sb.Append(c);
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "Player" : s;
        }
    }

    /// <summary>
    /// One player's katamari and prince, as sent in every State packet. The core of it is the game's own
    /// Katamari Ball sync packet: NetworkCoreInfo bytes (position, heading, CoreMesh rotation, prince move
    /// number and speed, action, size, prince height), built and read by the game's own code (CoreInfoCodec).
    /// The rest is what that packet doesn't carry: map, radius, prince placement and the stage clock.
    /// </summary>
    internal struct KatamariState
    {
        /// <summary>NetworkCoreInfo.GetByteData() of the sender's katamari.</summary>
        public byte[] CoreInfo;
        public int Map;
        public float Radius;
        public Vector3 PrincePos;
        public float PrinceYaw;
        public float PrinceScale;
        /// <summary>MainGameManager.CheckIsTimerRule(): 1 = time attack (counts up), 0 = time limit, -1 = no clock.</summary>
        public sbyte TimerRule;
        public float TimeLeft;
        public float Elapsed;

        // Katamari Ball's display values (MainGameCore), so a ghost is drawn the way the game draws a remote core:
        /// <summary>MainGameCore.KatamariScale: the scale ApplyCoreScale puts on the core (and so its CoreMesh).</summary>
        public float CoreScale;
        /// <summary>World rotation and scale of the core's MonoAttacher, the transform its rolled-up objects hang on.</summary>
        public Quaternion AttachRot;
        public float AttachScale;
        /// <summary>How fast its objects sink into the ball: MonoAttacher.ConvergeDelta per frame, as a rate per second.</summary>
        public float Converge;

        /// <summary>What the sender is doing (Whereabouts): the menus, the ship, a plaza, getting ready, playing, spectating, results.</summary>
        public Activity Activity;

        /// <summary>1 = their stage clock isn't moving (paused, the King talking...): show it as it is, don't run it on.</summary>
        public byte Flags;
        public const byte FlagClockStopped = 1;
        public bool ClockStopped => (Flags & FlagClockStopped) != 0;

        /// <summary>Their prince's Animator: state, time, speed, face (PoseSync).</summary>
        public PoseSync Pose;

        // Their run's timers (RunClocks): IGT so far, and IGT / RTA / stage clock when they reached the win condition (-1 not yet).
        public float Igt;
        public float ClearIgt, ClearRta, ClearStage;
        /// <summary>The rank badge their stage HUD shows (GetNowClearRateIcon), 255 = unknown.</summary>
        public byte HudRank;
        public bool ReachedWin => ClearIgt >= 0f;
        /// <summary>The stage's rating right now (CheckClearRate: 0 = top badge … 4, -1 unknown), and its own score (StageScore: kind, number, -1 none yet).</summary>
        public sbyte StageRate;
        public byte ScoreKind;
        public int Score;

        // Decoded from CoreInfo by the receiver (CoreInfoCodec.Decode); not sent separately.
        public Vector3 CorePos;
        public Quaternion CoreRot;
        public int AnimNo;
        public float AnimSpeed;
        /// <summary>What their own size counter shows: MainGameManager.KatamariSize (tenths of a mm), sent as NetworkCoreInfo.coreSize.</summary>
        public int Size;

        public PacketWriter Write(PacketWriter w) =>
            w.Bytes(CoreInfo).I32(Map).F32(Radius).Vec(PrincePos).F32(PrinceYaw).F32(PrinceScale)
             .U8((byte)TimerRule).F32(TimeLeft).F32(Elapsed)
             .F32(CoreScale).Quat(AttachRot).F32(AttachScale).F32(Converge).U8((byte)Activity).U8(Flags)
             .Then(Pose.Write)
             .F32(Igt).F32(ClearIgt).F32(ClearRta).F32(ClearStage).U8(HudRank)
             .U8((byte)StageRate).U8(ScoreKind).I32(Score);

        /// <summary>Reads the packet fields; CoreInfo still has to be decoded by the game's NetworkCoreInfo.</summary>
        public static KatamariState Read(PacketReader r) => new()
        {
            CoreInfo = r.Bytes(), Map = r.I32(), Radius = r.F32(), PrincePos = r.Vec(), PrinceYaw = r.F32(), PrinceScale = r.F32(),
            TimerRule = (sbyte)r.U8(), TimeLeft = r.F32(), Elapsed = r.F32(), CoreRot = new Quaternion(0f, 0f, 0f, 1f),
            CoreScale = r.F32(), AttachRot = r.Quat(), AttachScale = r.F32(), Converge = r.F32(), Activity = (Activity)r.U8(),
            Flags = r.U8(), Pose = PoseSync.Read(r),
            Igt = r.F32(), ClearIgt = r.F32(), ClearRta = r.F32(), ClearStage = r.F32(), HudRank = r.U8(),
            StageRate = (sbyte)r.U8(), ScoreKind = r.U8(), Score = r.I32(),
        };

        /// <summary>
        /// The game's size text, as its size counter shows it ("12cm3mm", "1m29cm9mm"). Sizes in the game
        /// (MainGameManager.KatamariSize, stage targets, GetMonoRequireSize, camera steps) count tenths of a
        /// millimetre: 12994 is 1299.4 mm, shown "1m29cm9mm" (ResultUtilityFunction.SizeToString's split).
        /// </summary>
        public static string SizeText(int size)
        {
            if (size <= 0) return "";
            int mm = size / 10;
            return mm < 1000 ? $"{mm / 10}cm{mm % 10}mm" : $"{mm / 1000}m{mm % 1000 / 10:D2}cm{mm % 10}mm";
        }

        public string TimeText() => TimeText(TimerRule, TimeLeft, Elapsed);

        public static string TimeText(sbyte rule, float left, float elapsed)
        {
            switch (rule)
            {
                case 1: return ClockText(elapsed);
                case 0:
                {
                    float t = Mathf.Max(0f, left);
                    return t > 90000f ? "" : $"{(int)t / 60}:{(int)t % 60:00} left";
                }
                default:
                    return "";
            }
        }

        public static string ClockText(float seconds)
        {
            float t = Mathf.Max(0f, seconds);
            return $"{(int)t / 60}:{(int)t % 60:00}.{(int)(t * 100f) % 100:00}";
        }
    }

    internal sealed class PacketWriter
    {
        private readonly MemoryStream _ms = new(64);
        private readonly BinaryWriter _w;

        public PacketWriter() { _w = new BinaryWriter(_ms, Encoding.UTF8); }

        public PacketWriter U8(byte v) { _w.Write(v); return this; }
        public PacketWriter U16(ushort v) { _w.Write(v); return this; }
        public PacketWriter I32(int v) { _w.Write(v); return this; }
        public PacketWriter U32(uint v) { _w.Write(v); return this; }
        public PacketWriter U64(ulong v) { _w.Write(v); return this; }
        public PacketWriter F32(float v) { _w.Write(v); return this; }
        public PacketWriter Vec(Vector3 v) { _w.Write(v.x); _w.Write(v.y); _w.Write(v.z); return this; }
        public PacketWriter Quat(Quaternion q) { _w.Write(q.x); _w.Write(q.y); _w.Write(q.z); _w.Write(q.w); return this; }

        public PacketWriter Bytes(byte[] data)
        {
            int n = Math.Min(data?.Length ?? 0, ushort.MaxValue);
            _w.Write((ushort)n);
            if (n > 0) _w.Write(data, 0, n);
            return this;
        }

        /// <summary>For chaining a struct's own Write in the middle of a packet.</summary>
        public PacketWriter Then(Func<PacketWriter, PacketWriter> write) => write(this);

        public PacketWriter Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            int n = Math.Min(bytes.Length, 255);
            _w.Write((byte)n);
            _w.Write(bytes, 0, n);
            return this;
        }

        public byte[] ToArray() { _w.Flush(); return _ms.ToArray(); }
    }

    internal sealed class PacketReader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        private int _pos;

        public PacketReader(byte[] buf, int length) { _buf = buf; _end = length; }

        private int Take(int n)
        {
            if (_pos + n > _end) throw new EndOfStreamException();
            int at = _pos;
            _pos += n;
            return at;
        }

        public byte U8() => _buf[Take(1)];
        public ushort U16() => BitConverter.ToUInt16(_buf, Take(2));
        public int I32() => BitConverter.ToInt32(_buf, Take(4));
        public uint U32() => BitConverter.ToUInt32(_buf, Take(4));
        public ulong U64() => BitConverter.ToUInt64(_buf, Take(8));
        /// <summary>Bytes left to read (optional fields at the end of a packet).</summary>
        public int Left => _end - _pos;

        public float F32()
        {
            float f = BitConverter.ToSingle(_buf, Take(4));
            return float.IsFinite(f) ? f : 0f;
        }

        public Vector3 Vec() => new(F32(), F32(), F32());

        /// <summary>A rotation; anything that isn't a unit quaternion comes back as identity.</summary>
        public Quaternion Quat()
        {
            float x = F32(), y = F32(), z = F32(), w = F32();
            float len = x * x + y * y + z * z + w * w;
            return len > 0.5f && len < 1.5f ? new Quaternion(x, y, z, w) : new Quaternion(0f, 0f, 0f, 1f);
        }

        public byte[] Bytes()
        {
            int n = U16();
            var data = new byte[n];
            Array.Copy(_buf, Take(n), data, 0, n);
            return data;
        }

        public string Str()
        {
            int n = U8();
            return Encoding.UTF8.GetString(_buf, Take(n), n);
        }
    }
}
