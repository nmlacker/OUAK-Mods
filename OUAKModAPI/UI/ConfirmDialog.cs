using System;

namespace OUAKModAPI
{
    /// <summary>What <see cref="ConfirmDialog.Ask"/> shows and does.</summary>
    public sealed class ConfirmRequest
    {
        public string Title;
        public string Body;
        public string ConfirmLabel = "Confirm";
        /// <summary>The dialog says a backup is made first and asks for a backup note (your Action makes the backup).</summary>
        public bool MakesBackup = true;
        /// <summary>Runs on the next Update with the user's backup note; returns the status message.</summary>
        public Func<string, string> Action;
        public Action Then;
    }

    /// <summary>
    /// The modal gate for anything that edits, moves or restores save data: the player must type "I understand"
    /// (and may add a note that is stored with the automatic backup) before the action runs. The mod window draws
    /// it over the current page; opening it opens the window. Every mod that writes saves must go through it.
    /// </summary>
    public static class ConfirmDialog
    {
        public const string Phrase = "I understand";

        public static ConfirmRequest Active { get; private set; }
        public static readonly TextField PhraseField = new(24, $"type \"{Phrase}\" here");
        public static readonly TextField NoteField = new(200, "optional - e.g. \"before trying the AA stages\"");

        public static bool PhraseAccepted =>
            string.Equals(PhraseField.Text.Trim().TrimEnd('.', '!'), Phrase, StringComparison.OrdinalIgnoreCase);

        public static void Ask(ConfirmRequest request)
        {
            Active = request;
            ModMenu.Open();
            PhraseField.Text = "";
            NoteField.Text = "";
            ModMenu.FocusPhrase = true;
        }

        public static void Accept()
        {
            if (Active == null || !PhraseAccepted) return;
            var req = Active;
            string note = NoteField.Text.Trim();
            Close();
            ModTasks.Run(() => req.Action(note), req.Then);
        }

        public static void Close()
        {
            Active = null;
        }
    }
}
