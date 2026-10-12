using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TarkovColor
{
    /// <summary>
    /// Applies a profile end to end.
    ///
    /// Every effect here outlives the process that set it: the tonal curve lives in the
    /// GPU gamma ramp, vibrance in the display pipeline, the EQ in a file. One-shot
    /// invocations still hand off to the tray when it is running, so that a single place
    /// owns the current state, but nothing breaks when they apply it themselves.
    /// </summary>
    public static class Applier
    {
        public const string IpcWindowTitle = "TarkovColorToggle_IPC_a7f3";
        private const int WM_COPYDATA = 0x004A;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
            uint flags, uint timeout, out UIntPtr result);

        public static IntPtr FindTrayWindow()
        {
            return FindWindowW(null, IpcWindowTitle);
        }

        public static bool IsTrayRunning()
        {
            return FindTrayWindow() != IntPtr.Zero;
        }

        /// <summary>Applies a profile, or resets everything when given null.</summary>
        public static void ApplyLocal(Profile p)
        {
            if (p == null)
            {
                Ramp.Reset();
                Vibrance.Reset();
                AudioProfile.Apply(null);
                Config.Log("Applied: (default)");
                return;
            }

            Ramp.Apply(p);
            Vibrance.SetLevel(p.Vibrance);
            AudioProfile.Apply(p);
            Config.Log("Applied: " + p.Name
                + (string.IsNullOrEmpty(p.AudioFile) ? "" : " eq=" + p.AudioFile)
                + " gamma=" + p.Gamma.ToString("0.00")
                + " contrast=" + p.Contrast.ToString("0.00")
                + " brightness=" + p.Brightness.ToString("0.00")
                + " vibrance=" + p.Vibrance
                + " highlights=" + p.WhiteProtectionStrength);
        }

        /// <summary>
        /// Entry point for one-shot invocations (-On / -Off / -Apply). Hands off to the tray
        /// when it is running so one process owns the state; otherwise applies it directly.
        /// Returns true if the tray took it.
        /// </summary>
        public static bool Request(string profileName)
        {
            IntPtr hwnd = FindTrayWindow();
            if (hwnd != IntPtr.Zero && SendToTray(hwnd, profileName ?? string.Empty)) return true;

            Config cfg = Config.Load();
            Profile p = string.IsNullOrEmpty(profileName) ? null : cfg.Find(profileName);
            ApplyLocal(p);
            return false;
        }

        private static bool SendToTray(IntPtr hwnd, string payload)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(payload + "\0");
            IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                COPYDATASTRUCT cds = new COPYDATASTRUCT
                {
                    dwData = IntPtr.Zero,
                    cbData = bytes.Length,
                    lpData = buffer
                };
                UIntPtr result;
                IntPtr ret = SendMessageTimeoutW(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds, 2 /*SMTO_ABORTIFHUNG*/, 4000, out result);
                return ret != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static string DecodeCopyData(IntPtr lParam)
        {
            COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
            if (cds.cbData <= 0) return string.Empty;
            return Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2).TrimEnd('\0');
        }
    }
}
