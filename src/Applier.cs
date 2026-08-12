using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TarkovColor
{
    /// <summary>
    /// Applies a profile end to end.
    ///
    /// Gamma/contrast/brightness go into the GPU gamma ramp, which persists after the
    /// setting process exits. Saturation goes through the Magnification colour matrix,
    /// which only lives as long as the process that set it - so when the tray app is
    /// running, one-shot invocations hand the work over to it instead of doing it
    /// themselves. Without the tray, everything except saturation still applies.
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

        /// <summary>Applies locally: gamma ramp always, saturation only if this process will stay alive.</summary>
        public static void ApplyLocal(Profile p, bool includeSaturation)
        {
            if (p == null)
            {
                Ramp.Reset();
                if (includeSaturation) Saturation.Reset();
                Config.Log("Applied: (default)" + (includeSaturation ? " +saturation" : ""));
                return;
            }

            Ramp.Apply(p);
            if (includeSaturation) Saturation.Apply(p.Saturation);
            Config.Log("Applied: " + p.Name
                + " gamma=" + p.Gamma.ToString("0.00")
                + " contrast=" + p.Contrast.ToString("0.00")
                + " brightness=" + p.Brightness.ToString("0.00")
                + " saturation=" + p.Saturation.ToString("0.00")
                + (includeSaturation ? "" : " (saturation skipped: tray not running)"));
        }

        /// <summary>
        /// Entry point for one-shot invocations (-On / -Off / -Apply). Hands off to the tray
        /// app when it is running so saturation sticks; otherwise applies what it can.
        /// Returns true if saturation was covered.
        /// </summary>
        public static bool Request(string profileName)
        {
            IntPtr hwnd = FindTrayWindow();
            if (hwnd != IntPtr.Zero && SendToTray(hwnd, profileName ?? string.Empty)) return true;

            Config cfg = Config.Load();
            Profile p = string.IsNullOrEmpty(profileName) ? null : cfg.Find(profileName);
            ApplyLocal(p, false);
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
