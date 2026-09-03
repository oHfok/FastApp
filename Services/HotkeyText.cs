using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace FastApp.Services
{
    /// <summary>
    /// Turns a stored hotkey into something worth reading.
    ///
    /// The sequence is persisted as WPF Key names because that is what has to
    /// parse back ("LeftCtrl,LeftShift,K"), and the display text was simply
    /// those names joined with plus signs: "LeftCtrl + LeftShift + K". Correct,
    /// and nothing anybody writes on a keycap.
    ///
    /// Derived from the sequence rather than read from the stored display text,
    /// so bindings recorded before this existed also read properly without
    /// anyone having to record them again.
    /// </summary>
    public static class HotkeyText
    {
        public const string None = "None";

        // Which side of the keyboard a modifier came from is not worth showing:
        // there is one Ctrl as far as anyone reading this is concerned.
        private static readonly Dictionary<Key, string> Modifiers = new()
        {
            [Key.LeftCtrl] = "Ctrl",
            [Key.RightCtrl] = "Ctrl",
            [Key.LeftShift] = "Shift",
            [Key.RightShift] = "Shift",
            [Key.LeftAlt] = "Alt",
            [Key.RightAlt] = "Alt",
            [Key.System] = "Alt",
            [Key.LWin] = "Win",
            [Key.RWin] = "Win"
        };

        /// <summary>Modifiers read in this order regardless of how they were pressed.</summary>
        private static readonly string[] ModifierOrder = { "Ctrl", "Shift", "Alt", "Win" };

        private static readonly Dictionary<Key, string> Named = new()
        {
            [Key.Return] = "Enter",
            [Key.Escape] = "Esc",
            [Key.Back] = "Backspace",
            [Key.Prior] = "Page Up",
            [Key.Next] = "Page Down",
            [Key.Capital] = "Caps Lock",
            [Key.Snapshot] = "Print Screen",
            [Key.OemComma] = ",",
            [Key.OemPeriod] = ".",
            [Key.OemMinus] = "-",
            [Key.OemPlus] = "=",
            [Key.OemQuestion] = "/",
            [Key.OemTilde] = "`",
            [Key.OemOpenBrackets] = "[",
            [Key.OemCloseBrackets] = "]",
            [Key.OemSemicolon] = ";",
            [Key.OemQuotes] = "'",
            [Key.OemBackslash] = "\\",
            [Key.OemPipe] = "\\"
        };

        public static string Describe(string sequence)
        {
            if (string.IsNullOrWhiteSpace(sequence)) return None;

            var keys = new List<Key>();
            var unknown = new List<string>();

            foreach (string token in sequence.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse<Key>(token.Trim(), ignoreCase: true, out var key)) keys.Add(key);
                // An unreadable token is shown rather than dropped: a binding
                // that displays as nothing looks unset, and this one is not.
                else unknown.Add(token.Trim());
            }

            string described = Describe(keys);
            if (unknown.Count == 0) return described;

            return described == None
                ? string.Join(" + ", unknown)
                : described + " + " + string.Join(" + ", unknown);
        }

        public static string Describe(IEnumerable<Key> keys)
        {
            if (keys == null) return None;

            var modifiers = new List<string>();
            var rest = new List<string>();

            foreach (var key in keys)
            {
                if (Modifiers.TryGetValue(key, out string modifier))
                {
                    // Holding both shifts is one Shift on screen.
                    if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
                }
                else
                {
                    string name = Name(key);
                    if (!rest.Contains(name)) rest.Add(name);
                }
            }

            var ordered = ModifierOrder.Where(modifiers.Contains).Concat(rest).ToList();
            return ordered.Count == 0 ? None : string.Join(" + ", ordered);
        }

        private static string Name(Key key)
        {
            if (Named.TryGetValue(key, out string named)) return named;

            // D1 is the 1 key; NumPad1 is worth distinguishing from it.
            string raw = key.ToString();
            if (raw.Length == 2 && raw[0] == 'D' && char.IsDigit(raw[1])) return raw[1].ToString();
            if (raw.StartsWith("NumPad", StringComparison.Ordinal)) return "Num " + raw.Substring(6);

            return raw;
        }
    }
}
