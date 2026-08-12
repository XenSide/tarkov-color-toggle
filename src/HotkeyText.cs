using System;
using System.Text;
using System.Windows.Forms;

namespace TarkovColor
{
    public static class HotkeyText
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public static string Describe(uint modifiers, uint key)
        {
            if (key == 0) return string.Empty;

            StringBuilder sb = new StringBuilder();
            if ((modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((modifiers & MOD_ALT) != 0) sb.Append("Alt+");
            if ((modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((modifiers & MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(key));
            return sb.ToString();
        }

        private static string KeyName(uint key)
        {
            Keys k = (Keys)key;
            switch (k)
            {
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "+";
                case Keys.OemQuestion: return "/";
                case Keys.Oemtilde: return "`";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.OemBackslash:
                case Keys.OemPipe: return "\\";
                default: return k.ToString();
            }
        }

        /// <summary>Converts a WinForms key event into (modifiers, key), or key = 0 if it is a bare modifier.</summary>
        public static void FromKeyEvent(KeyEventArgs e, out uint modifiers, out uint key)
        {
            modifiers = 0;
            if (e.Control) modifiers |= MOD_CONTROL;
            if (e.Alt) modifiers |= MOD_ALT;
            if (e.Shift) modifiers |= MOD_SHIFT;

            Keys code = e.KeyCode;
            if (code == Keys.ControlKey || code == Keys.Menu || code == Keys.ShiftKey ||
                code == Keys.LWin || code == Keys.RWin || code == Keys.None)
            {
                key = 0;
                return;
            }

            key = (uint)code;
        }
    }
}
