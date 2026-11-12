using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace OUAKModAPI
{
    /// <summary>
    /// Hooks on the game's functions (Harmony), owned by one mod and removable together: run code before or after
    /// a game method, change its arguments or result, or replace it (a prefix that returns false skips the
    /// original). Hooking never throws: a hook that can't be made is logged and reported as false.
    /// <code>
    /// static readonly HookSet Hooks = new("my.mod");
    /// Hooks.Postfix(typeof(MainGameManager), nameof(MainGameManager.RestartStage), typeof(MyHooks), nameof(MyHooks.AfterRestart));
    /// ...
    /// static class MyHooks { public static void AfterRestart(MainGameManager __instance) { ... } }
    /// </code>
    /// Keep hooks small and wrap their bodies in try/catch: an exception thrown inside a hooked game method
    /// unwinds through the game's native code and can crash the game outright. <see cref="HotMod"/>s get a
    /// HookSet that is removed when they unload.
    /// </summary>
    public sealed class HookSet : IDisposable
    {
        private readonly HarmonyLib.Harmony _harmony;
        private readonly List<MethodBase> _patched = new();

        public string Id { get; }
        /// <summary>How many methods are hooked now.</summary>
        public int Count => _patched.Count;
        /// <summary>The methods hooked now.</summary>
        public IReadOnlyList<MethodBase> Patched => _patched;

        public HookSet(string id)
        {
            Id = id;
            _harmony = new HarmonyLib.Harmony(id);
        }

        /// <summary>Runs <paramref name="handlerType"/>.<paramref name="handler"/> before <paramref name="type"/>.<paramref name="method"/>.</summary>
        public bool Prefix(Type type, string method, Type handlerType, string handler, Type[] args = null) =>
            Patch(Find(type, method, args), Handler(handlerType, handler), null);

        /// <summary>Runs <paramref name="handlerType"/>.<paramref name="handler"/> after <paramref name="type"/>.<paramref name="method"/>.</summary>
        public bool Postfix(Type type, string method, Type handlerType, string handler, Type[] args = null) =>
            Patch(Find(type, method, args), null, Handler(handlerType, handler));

        /// <summary>The general form: any of prefix / postfix / finalizer on a method you looked up yourself.</summary>
        public bool Patch(MethodBase target, MethodInfo prefix = null, MethodInfo postfix = null, MethodInfo finalizer = null)
        {
            if (target == null || (prefix == null && postfix == null && finalizer == null)) return false;
            try
            {
                _harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(finalizer) : null);
                if (!_patched.Contains(target)) _patched.Add(target);
                return true;
            }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"[{Id}] Couldn't hook {target.DeclaringType?.Name}.{target.Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>Removes every hook this set made.</summary>
        public void UnpatchAll()
        {
            try { _harmony.UnpatchSelf(); }
            catch (Exception e) { ApiCore.Log.Warn($"[{Id}] Unhooking failed: {e.Message}"); }
            _patched.Clear();
        }

        public void Dispose() => UnpatchAll();

        private MethodBase Find(Type type, string method, Type[] args)
        {
            try
            {
                var m = args != null ? AccessTools.Method(type, method, args) : AccessTools.Method(type, method);
                if (m == null) ApiCore.Log.Warn($"[{Id}] {type?.Name}.{method} wasn't found.");
                return m;
            }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"[{Id}] {type?.Name}.{method}: {e.Message}");
                return null;
            }
        }

        private MethodInfo Handler(Type type, string name)
        {
            var m = AccessTools.Method(type, name);
            if (m == null) ApiCore.Log.Warn($"[{Id}] Hook handler {type?.Name}.{name} wasn't found.");
            else if (!m.IsStatic) { ApiCore.Log.Warn($"[{Id}] Hook handler {type.Name}.{name} must be static."); return null; }
            return m;
        }
    }
}
