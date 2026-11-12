using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Teleport through the player bar: click another player's slot (or hover it and press the confirm key) and
    /// choose Teleport. In the same hub (the Prince's Ship or a plaza) your walking character is put on their spot
    /// (CharacterMove.SetCharacterPosition = Rigidbody.position, the move the plaza gather uses for fans). In
    /// another hub you go there first (SceneLoader.LoadPlaza / the ship, like the plaza gather) and are put
    /// on their spot once you're both there. Players in a stage can't be teleported to.
    /// </summary>
    internal static class Teleport
    {
        /// <summary>Slot rectangles from the last bar draw, in GUI units (before the bar's scale).</summary>
        private static readonly Dictionary<byte, Rect> Slots = new();
        private static float _slotScale = 1f;
        private static bool _wasDown;

        /// <summary>The slot under the mouse (0 = none).</summary>
        public static byte HoverId { get; private set; }

        // Travelling to another hub: who to land next to once we're there.
        private static byte _pendingId;
        private static int _pendingKey;
        private static float _pendingUntil;

        public static int Done { get; private set; }
        public static string Last { get; private set; } = "";

        public static void BeginBar(float scale)
        {
            Slots.Clear();
            _slotScale = scale;
        }

        public static void RecordSlot(byte id, Rect r) => Slots[id] = r;

        public static void Tick()
        {
            if (!NetSession.Connected)
            {
                HoverId = 0;
                _pendingId = 0;
                return;
            }
            Arrive();

            // Hover from the slots drawn last frame; a click opens the choice (the game ignores that click).
            HoverId = 0;
            bool down = false;
            try
            {
                down = Input.GetMouseButton(0);
                if (Cursor.visible && Slots.Count > 0)
                {
                    var m = Input.mousePosition;
                    var gui = new Vector2(m.x / _slotScale, (Screen.height - m.y) / _slotScale);
                    foreach (var kv in Slots)
                        if (kv.Value.Contains(gui)) { HoverId = kv.Key; break; }
                }
            }
            catch { }
            bool pressed = down && !_wasDown;
            _wasDown = down;
            if (!pressed || HoverId == 0 || HoverId == NetSession.MyId) return;
            if (ModMenu.IsOpen || Prompt.Active || Chat.IsOpen) return;
            GameInput.BlockThisFrame("teleport");
            Offer(HoverId);
        }

        /// <summary>True when you could teleport to that player now (for the bar's hover hint).</summary>
        public static bool CanOffer(byte id) =>
            id != NetSession.MyId && HubPresence.HubKey != 0 && NetSession.Peers.TryGetValue(id, out var p) && InHub(p);

        private static bool InHub(Peer p) => p.HubKey != 0 && p.HubAt > 0f && Time.unscaledTime - p.HubAt < 3f;

        /// <summary>Asks what to do with that player: teleport to them, or why you can't.</summary>
        public static void Offer(byte id)
        {
            if (!NetSession.Peers.TryGetValue(id, out var p)) return;
            var options = new List<PromptOption>();
            string body;
            if (HubPresence.HubKey == 0)
            {
                body = "Teleporting works on the Prince's Ship and in the plazas. Go back to one first.";
            }
            else if (!InHub(p))
            {
                body = $"{p.Name} isn't on the ship or in a plaza right now ({Whereabouts.Short(p.Activity, p.MapKey, 0)}), so there's nowhere to teleport to.";
            }
            else if (p.HubKey == HubPresence.HubKey)
            {
                body = $"{p.Name} is {HubPresence.PlaceName(p.HubKey)} with you.";
                options.Add(new PromptOption("Teleport", () => ModTasks.Run(() => To(id), toast: true), "Go to where they stand."));
            }
            else
            {
                body = $"{p.Name} is {HubPresence.PlaceName(p.HubKey)}; you're {HubPresence.PlaceName(HubPresence.HubKey)}.";
                options.Add(new PromptOption("Go there", () => ModTasks.Run(() => Travel(id), toast: true),
                    p.HubKey == HubPresence.ShipKey ? "Go to the Prince's Ship, then to them." :
                    "Go to that plaza (it changes your era, like the map jump), then to them."));
            }
            options.Add(new PromptOption("Cancel", null));
            Prompt.Show($"Teleport to {p.Name}", body, options);
        }

        /// <summary>Puts your walking character on that player's spot (same hub only).</summary>
        public static string To(byte id)
        {
            if (!NetSession.Peers.TryGetValue(id, out var p)) throw new InvalidOperationException("That player left.");
            if (HubPresence.HubKey == 0 || !InHub(p) || p.HubKey != HubPresence.HubKey)
                throw new InvalidOperationException($"{p.Name} isn't in the same place as you any more.");
            if (!HubPresence.TryMoveLocal(p.HubPos, p.HubYaw, out string how))
                throw new InvalidOperationException("Couldn't move you: " + how);
            Done++;
            Last = $"to {p.Name} at {p.HubPos} {HubPresence.PlaceName(p.HubKey)} ({how})";
            MelonLogger.Msg($"[Online] Teleport {Last}");
            return $"Teleported to {p.Name}.";
        }

        /// <summary>Goes to the hub that player is in, then onto their spot.</summary>
        public static string Travel(byte id)
        {
            if (!NetSession.Peers.TryGetValue(id, out var p) || !InHub(p)) throw new InvalidOperationException("They aren't on the ship or in a plaza any more.");
            int key = p.HubKey;
            string msg;
            if (key == HubPresence.ShipKey)
            {
                var ship = SceneLoader.Modes.FirstOrDefault(m => m.Scene == "OujiShip") ?? throw new InvalidOperationException("No way to the ship.");
                msg = SceneLoader.LoadMode(ship);
            }
            else
            {
                var plaza = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Plaza && !string.IsNullOrEmpty(e.PlazaKey) &&
                                                                     3000 + Il2CppApp.KatamariSin.SelectHirobaManager.GetStageIndex(e.PlazaKey) == key)
                            ?? throw new InvalidOperationException("Couldn't find their plaza.");
                msg = StageGate.Approved(() => SceneLoader.LoadPlaza(plaza));
            }
            _pendingId = id;
            _pendingKey = key;
            _pendingUntil = Time.unscaledTime + 40f;
            MelonLogger.Msg($"[Online] Teleport: going {HubPresence.PlaceName(key)} to {p.Name}.");
            return msg + $" Then to {p.Name}.";
        }

        /// <summary>After travelling: once you're there and your character is up, onto their spot.</summary>
        private static void Arrive()
        {
            if (_pendingId == 0) return;
            float now = Time.unscaledTime;
            if (now > _pendingUntil || !NetSession.Peers.TryGetValue(_pendingId, out var p))
            {
                _pendingId = 0;
                return;
            }
            if (HubPresence.HubKey != _pendingKey || !InHub(p) || p.HubKey != _pendingKey) return;
            if (!HubPresence.TryLocal(out _, out _)) return;
            byte id = _pendingId;
            _pendingId = 0;
            // A moment for the scene to settle (curtain, character set-up) before moving the character.
            ModTasks.Run(() => To(id), delay: 1.5f, toast: true);
        }
    }
}
