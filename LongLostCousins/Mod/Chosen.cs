using System;
using LongLostCousins.Dressing;
using LongLostCousins.Select;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins
{
    /// <summary>
    /// Whether the player wears a Long Lost Cousin now, and the dressing hooks that follow it.
    /// <para>She is her own cousin: picking her (on the select screen or in the tab) never changes the game's save. The
    /// settings record the cousin the save held when she was picked (save +0x19C, and the custom index for the Cousin
    /// Creator's 999), and she is worn while the settings name her and the save still holds that cousin. While she is
    /// worn, the game builds the Prince as the hidden model under her (<see cref="PrinceBase"/>). When the save's cousin
    /// changes (another cousin picked, a Plaza swap, the Cousin Creator, another slot loaded), she is taken off and the
    /// game's run-time cousin id is set back from the save. Stage 20's temporary 0 is not a change.</para>
    /// </summary>
    internal static class Chosen
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Chosen");
        /// <summary>The Cousin Creator's cousin id in the save (its custom index says which designed cousin).</summary>
        public const int CreatorId = 999;
        private static bool _on, _checkNow;
        private static float _nextSync;

        /// <summary>The result of the last <see cref="Refresh"/> (cheap: for per-frame checks).</summary>
        public static bool On => _on;

        /// <summary>An id that may stand in the save: not negative and not the mod's 100.</summary>
        public static bool IsStorableId(int id) => id >= 0 && id != SelectScreen.ModId;

        /// <summary>The cousin the save holds now (GlobalSaveData.GetSelectPlayerId, outside every scope of the mod), or -1.</summary>
        public static int SavedCousin()
        {
            var save = GameRefs.Save;
            if (save == null) return -1;
            using (SelectScreen.Suspended()) return save.GetSelectPlayerId;
        }

        /// <summary>
        /// The save's cousin and custom index as the worn test reads them: the fields themselves, except on the stage where
        /// the game always plays the Prince, which puts 0 in the save for the stage and keeps the real id in
        /// MainGameManager._beforeOujiIdx_Select. False when no save object exists.
        /// </summary>
        public static bool Effective(out int id, out int custom)
        {
            id = custom = -1;
            var save = GameRefs.Save;
            if (save == null) return false;
            id = save._selectPlayerId;
            custom = save._customPlayerIndex;
            if (id == 0 && InPrinceOnlyStage(out int kept)) id = kept;
            return true;
        }

        /// <summary>
        /// True on the stage where the game always plays the Prince; <paramref name="kept"/> is the save's id it put aside.
        /// The stage's StartSceneLoad writes the 0 and ClearResult / ToStageSelectCor put the id back; in case the 0 is
        /// seen while GlobalManager does not point at the stage's manager (yet, or any more), a saved 0 against a record
        /// that is not 0 looks the manager up in the scene before she is taken off.
        /// </summary>
        public static bool InPrinceOnlyStage(out int kept)
        {
            kept = -1;
            var gm = GameRefs.Main;
            if (gm == null && Settings.HasRecord && Settings.Saved != 0)
            {
                try { gm = UnityEngine.Object.FindObjectOfType<Il2CppApp.KatamariSin.MainGameManager>(); }
                catch { gm = null; }
            }
            if (gm == null || gm._stageIdx != Contexts.PrinceOnlyStage) return false;
            kept = gm._beforeOujiIdx_Select;
            return true;
        }

        /// <summary>
        /// True when the settings name a cousin and <paramref name="id"/>/<paramref name="custom"/> is the save she was
        /// picked with. With nothing recorded yet (a settings file from 0.1.0, the test, a pick at the title), the given
        /// save is recorded now. Call only with a slot loaded, and only with the save read through <see cref="Effective"/>:
        /// the record is made only by CheckSave and the stage/ending load step (<see cref="RecordIfNone"/>); everything
        /// else (<see cref="Now"/>, the getter's postfix) compares with <see cref="IsRecord"/>.
        /// </summary>
        public static bool Matches(int id, int custom)
        {
            if (!Settings.Active || !IsStorableId(id)) return false;
            if (!Settings.HasRecord)
            {
                Settings.Record(id, custom);
                _checkNow = true;   // the file is written from LateUpdate
            }
            return IsRecord(id, custom);
        }

        /// <summary>
        /// The compare alone, never a record: true when the settings name a cousin with a save recorded and
        /// <paramref name="id"/>/<paramref name="custom"/> is that save. For the getter's postfix, whose answer may be a
        /// scoped read (stage 20's 0 inside a loader scope) and must never become the record.
        /// </summary>
        public static bool IsRecord(int id, int custom) =>
            Settings.HasRecord && IsStorableId(id) && id == Settings.Saved && (id != CreatorId || custom == Settings.Custom);

        /// <summary>
        /// The stage's or the ending's load step, before <see cref="Now"/>: with a cousin chosen and nothing recorded yet,
        /// the save read through <see cref="Effective"/> is recorded. Never throws.
        /// </summary>
        public static void RecordIfNone()
        {
            try
            {
                if (!Settings.Active || Settings.HasRecord || !GameRefs.InGameSession) return;
                if (Effective(out int id, out int custom) && IsStorableId(id)) Matches(id, custom);
            }
            catch (Exception e) { Log.Warn("recording the save's cousin: " + e.Message); }
        }

        /// <summary>Worn now, read live (a compare, never a record): the settings name a cousin, a slot is loaded, and the save holds the cousin she was picked with.</summary>
        public static bool Now
        {
            get
            {
                if (!Settings.Active) return false;
                try { return GameRefs.InGameSession && Effective(out int id, out int custom) && IsRecord(id, custom); }
                catch { return false; }
            }
        }

        /// <summary>A hook saw something that may change the answer (a Plaza swap): check at the next LateUpdate, not in half a second.</summary>
        public static void CheckSoon() => _checkNow = true;

        /// <summary>The save as a phrase for the log.</summary>
        public static string Describe(int id, int custom) => id == CreatorId ? $"the Cousin Creator's cousin (custom index {custom})" : $"cousin {id}";

        /// <summary>Re-reads <see cref="Now"/> and installs or removes the dressing hooks when it changed. Never throws; call only from LateUpdate or a postfix of a method outside the dressing set.</summary>
        public static void Refresh()
        {
            try
            {
                bool now = Now;
                if (now && !Hooks.Installed) Hooks.Install();
                else if (!now && Hooks.Installed) Hooks.Uninstall();
                if (now != _on) Log.Info(now ? $"worn now: {Settings.Describe()} (the save's {Settings.DescribeRecord()} is kept)" : "not worn now");
                _on = now;
            }
            catch (Exception e) { Log.Warn("refresh: " + e.Message); }
        }

        /// <summary>
        /// MelonMod.OnLateUpdate: twice a second (or at once after a hook asked) while a cousin is in the settings, the hooks
        /// are in or the run-time id is still the mod's 0: records the save on the first check, takes her off when the save
        /// changed, puts the run-time id back, and writes a pending record. Never throws.
        /// </summary>
        public static void Sync()
        {
            if (!Settings.Active && !Hooks.Installed && !PrinceBase.Zeroed) return;
            float now = Time.unscaledTime;
            // With a cousin chosen and nothing recorded yet, every frame: only CheckSave records, so the first scoped load
            // after a slot is loaded should find the record made.
            bool unrecorded = Settings.Active && !Settings.HasRecord;
            if (!_checkNow && !unrecorded && now < _nextSync) return;
            _checkNow = false;
            _nextSync = now + 0.5f;
            try { CheckSave(); }
            catch (Exception e) { Log.Warn("checking the save: " + e.Message); }
            Refresh();
            try { Settings.Flush(); }
            catch (Exception e) { Log.Warn("writing the settings: " + e.Message); }
        }

        private static void CheckSave()
        {
            if (!GameRefs.InGameSession || !Effective(out int id, out int custom) || !IsStorableId(id)) return;
            if (Settings.Active && Settings.CousinId != null)
            {
                if (!Settings.HasRecord) Settings.Record(id, custom);
                else if (!Matches(id, custom))
                    TakeOff($"the save now holds {Describe(id, custom)}, not the {Settings.DescribeRecord()} she was picked with");
            }
            if (!Settings.Active && PrinceBase.Zeroed) PutBackNowId();
        }

        /// <summary>Turns her off because the save changed: the settings forget her and the run-time id follows the save again.</summary>
        public static void TakeOff(string why)
        {
            if (!Settings.Active) return;
            Log.Info($"{Settings.Describe()} taken off: {why}");
            Settings.Choose(null, null);
            PutBackNowId();
        }

        /// <summary>
        /// GlobalManager._nowCharacterID back to the save's cousin after the mod set it to 0 for her. Left for later inside
        /// a stage or the ending (their models were built with 0), and on the stage where the game plays the Prince.
        /// </summary>
        public static void PutBackNowId()
        {
            if (!PrinceBase.Zeroed) return;
            try
            {
                var g = GameRefs.Global;
                if (g == null || !GameRefs.InGameSession || GameRefs.Main != null || g.EndGameMan != null) return;
                if (!Effective(out int id, out _) || !IsStorableId(id)) return;
                PrinceBase.Zeroed = false;
                if (g._nowCharacterID != id)
                {
                    Log.Info($"GlobalManager._nowCharacterID back to the save's {Describe(id, -1)} (it was {g._nowCharacterID})");
                    g._nowCharacterID = id;
                }
            }
            catch (Exception e) { Log.Warn("putting the run-time cousin id back: " + e.Message); }
        }
    }
}
