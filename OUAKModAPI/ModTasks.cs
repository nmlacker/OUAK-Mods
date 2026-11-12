using System;
using System.Collections.Generic;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Runs game actions on the main thread at the right moment, and keeps the status line the mod window shows.
    /// Never run game logic from inside OnGUI (Unity calls it several times a frame): queue it with
    /// <see cref="Run(Func{string}, Action, float, bool)"/> instead. An action returns the message to show; one that
    /// throws shows the exception's message as an error (an <see cref="InvalidOperationException"/> is treated as
    /// a normal "can't do that now" message and isn't logged as a crash).
    /// </summary>
    public static class ModTasks
    {
        private sealed class Pending
        {
            public float At, Deadline;
            public Func<bool> When;
            public Func<string> Action;
            public Action Then;
            public bool Toast;
        }

        private static readonly List<Pending> Queue = new();
        private static string _toastText;
        private static bool _toastIsError;
        private static float _toastUntil;

        /// <summary>The last action's message (shown at the bottom of the mod window).</summary>
        public static string Status { get; private set; } = "Ready.";
        public static bool StatusIsError { get; private set; }

        /// <summary>Queues <paramref name="action"/> for the next Update (or <paramref name="delay"/> seconds later).</summary>
        /// <param name="then">Runs after the action succeeded.</param>
        /// <param name="toast">Also show the message on screen while the mod window is closed.</param>
        public static void Run(Func<string> action, Action then = null, float delay = 0f, bool toast = false)
        {
            if (action == null) return;
            Queue.Add(new Pending { At = Time.unscaledTime + delay, Action = action, Then = then, Toast = toast });
        }

        /// <summary>Runs <paramref name="action"/> once <paramref name="when"/> is true, or after <paramref name="timeout"/> seconds anyway.</summary>
        public static void RunWhen(Func<bool> when, Func<string> action, float timeout)
        {
            if (action == null) return;
            float now = Time.unscaledTime;
            Queue.Add(new Pending { At = now + 0.2f, Deadline = now + timeout, When = when, Action = action });
        }

        /// <summary>Runs a plain action later on the main thread (no status message).</summary>
        public static void Later(Action action, float delay = 0f)
        {
            if (action == null) return;
            Run(() => { action(); return null; }, delay: delay);
        }

        /// <summary>A short message on screen, shown while the mod window is closed.</summary>
        public static void Toast(string text, bool error = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            _toastText = text;
            _toastIsError = error;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        /// <summary>The toast to draw now, or null.</summary>
        public static string CurrentToast(out bool error)
        {
            error = _toastIsError;
            return _toastText != null && Time.unscaledTime < _toastUntil ? _toastText : null;
        }

        /// <summary>Sets the status line (and logs it).</summary>
        public static void SetStatus(string msg, bool error)
        {
            if (msg == null) return;
            Status = msg;
            StatusIsError = error;
            if (error) ApiCore.Log.Warn(msg);
            else ApiCore.Log.Info(msg);
        }

        internal static void Tick()
        {
            if (Queue.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = 0; i < Queue.Count; i++)
            {
                var p = Queue[i];
                if (p.At > now) continue;
                if (p.When != null && now < p.Deadline)
                {
                    bool ready;
                    try { ready = p.When(); } catch { ready = true; }
                    if (!ready) continue;
                }
                Queue.RemoveAt(i--);
                try
                {
                    string msg = p.Action();
                    if (msg != null)
                    {
                        SetStatus(msg, false);
                        if (p.Toast) Toast(msg);
                    }
                    p.Then?.Invoke();
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    SetStatus(inner.Message, true);
                    if (p.Toast) Toast(inner.Message, true);
                    if (inner is not InvalidOperationException) ApiCore.Log.Error(e.ToString());
                }
            }
        }
    }
}
