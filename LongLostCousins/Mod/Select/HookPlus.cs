using System;
using System.Reflection;
using HarmonyLib;
using OUAKModAPI;

namespace LongLostCousins.Select
{
    /// <summary>
    /// HookSet's prefix, postfix and finalizer on one game method in one call. The API's HookSet.Prefix and Postfix take
    /// no finalizer, and a finalizer is what brings a scope counter back down when the game's method throws.
    /// Tools/audit_calls.ps1 reads these calls like HookSet.Prefix/Postfix: keep the target first and the argument types last.
    /// </summary>
    internal static class HookPlus
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Hooks");

        /// <summary>Hooks <paramref name="type"/>.<paramref name="method"/> with the named handlers (null: none of that kind). False when it could not be made.</summary>
        public static bool Hook(this HookSet set, Type type, string method, Type handlerType, string prefix, string postfix, string finalizer, Type[] args = null)
        {
            MethodBase target = null;
            try { target = args != null ? AccessTools.Method(type, method, args) : AccessTools.Method(type, method); }
            catch (Exception e) { Log.Warn($"{type?.Name}.{method}: {e.Message}"); }
            if (target == null) { Log.Warn($"{type?.Name}.{method} wasn't found"); return false; }
            MethodInfo pre = Handler(handlerType, prefix), post = Handler(handlerType, postfix), fin = Handler(handlerType, finalizer);
            if ((prefix != null && pre == null) || (postfix != null && post == null) || (finalizer != null && fin == null)) return false;
            return set.Patch(target, pre, post, fin);
        }

        private static MethodInfo Handler(Type type, string name)
        {
            if (name == null) return null;
            var m = AccessTools.Method(type, name);
            if (m == null || !m.IsStatic) Log.Warn($"hook handler {type?.Name}.{name} wasn't found or isn't static");
            return m != null && m.IsStatic ? m : null;
        }
    }
}
