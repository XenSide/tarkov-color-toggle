using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TarkovColor
{
    /// <summary>
    /// Builds 256x3 gamma ramps and pushes them to the display's GPU LUT.
    /// This is the same mechanism calibration tools use, so it survives exclusive fullscreen.
    /// </summary>
    public static class Ramp
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDCW(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

        public static string PrimaryDeviceName()
        {
            foreach (Screen s in Screen.AllScreens)
            {
                if (s.Primary) return s.DeviceName;
            }
            throw new Exception("Could not find the primary screen.");
        }

        /// <summary>Applies a profile to the primary display and records it as active.</summary>
        public static void Apply(Profile p)
        {
            ushort[] ramp = Build(p);
            Push(ramp);
            Config.WriteActiveProfileName(p == null ? null : p.Name);
        }

        /// <summary>Resets the primary display to a linear ramp (Windows default).</summary>
        public static void Reset()
        {
            Push(Identity());
            Config.WriteActiveProfileName(null);
        }

        /// <summary>Applies a ramp without touching the stored active-profile state (used for live preview).</summary>
        public static void Preview(Profile p)
        {
            Push(Build(p));
        }

        private static void Push(ushort[] ramp)
        {
            string device = PrimaryDeviceName();
            IntPtr hdc = CreateDCW(device, null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                throw new Exception("CreateDC failed for " + device + " (error " + Marshal.GetLastWin32Error() + ").");
            try
            {
                if (!SetDeviceGammaRamp(hdc, ramp))
                    throw new Exception("SetDeviceGammaRamp failed for " + device + " (error " + Marshal.GetLastWin32Error() + ").");
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        public static ushort[] Identity()
        {
            ushort[] ramp = new ushort[768];
            for (int c = 0; c < 3; c++)
                for (int i = 0; i < 256; i++)
                    ramp[c * 256 + i] = (ushort)Math.Min(65535, i * 257);
            return ramp;
        }

        /// <summary>
        /// Base curve (ICC vcgt if the profile names one, otherwise linear) with
        /// gamma, then contrast, then brightness applied on top.
        /// </summary>
        public static ushort[] Build(Profile p)
        {
            if (p == null) return Identity();

            double[][] baseCurve = LinearBase();
            if (!string.IsNullOrEmpty(p.IccFile))
            {
                string iccPath = Path.Combine(Config.AppDir, p.IccFile);
                if (File.Exists(iccPath)) baseCurve = IccBase(iccPath);
            }

            double gamma = Clamp(p.Gamma, 0.10, 5.0);
            double contrast = Clamp(p.Contrast, 0.10, 5.0);
            double brightness = Clamp(p.Brightness, -1.0, 1.0);

            ushort[] ramp = new ushort[768];
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < 256; i++)
                {
                    double v = baseCurve[c][i];
                    if (gamma != 1.0) v = Math.Pow(v, 1.0 / gamma);
                    if (contrast != 1.0) v = (v - 0.5) * contrast + 0.5;
                    if (brightness != 0.0) v = v + brightness;
                    ramp[c * 256 + i] = (ushort)Math.Round(Clamp(v, 0.0, 1.0) * 65535.0);
                }
            }
            return ramp;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static double[][] LinearBase()
        {
            double[][] curve = new double[3][];
            for (int c = 0; c < 3; c++)
            {
                curve[c] = new double[256];
                for (int i = 0; i < 256; i++) curve[c][i] = i / 255.0;
            }
            return curve;
        }

        // ---------- ICC vcgt parsing ----------

        private static uint ReadUInt32BE(byte[] b, int off)
        {
            return ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
        }

        private static ushort ReadUInt16BE(byte[] b, int off)
        {
            return (ushort)(((uint)b[off] << 8) | b[off + 1]);
        }

        private static int ReadInt32BE(byte[] b, int off)
        {
            return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
        }

        /// <summary>Reads the vcgt (video card gamma table) tag as three 0..1 curves.</summary>
        private static double[][] IccBase(string iccPath)
        {
            byte[] bytes = File.ReadAllBytes(iccPath);
            uint tagCount = ReadUInt32BE(bytes, 128);

            int vcgtOffset = -1;
            for (int i = 0; i < tagCount; i++)
            {
                int entryOff = 132 + (i * 12);
                if (entryOff + 12 > bytes.Length) break;
                string sig = Encoding.ASCII.GetString(bytes, entryOff, 4);
                if (sig == "vcgt")
                {
                    vcgtOffset = (int)ReadUInt32BE(bytes, entryOff + 4);
                    break;
                }
            }

            // No calibration curve in this profile: fall back to linear rather than failing.
            if (vcgtOffset < 0) return LinearBase();

            uint gammaType = ReadUInt32BE(bytes, vcgtOffset + 8);
            double[][] curve = new double[3][];
            for (int c = 0; c < 3; c++) curve[c] = new double[256];

            if (gammaType == 0)
            {
                ushort numChannels = ReadUInt16BE(bytes, vcgtOffset + 12);
                ushort numEntries = ReadUInt16BE(bytes, vcgtOffset + 14);
                ushort entrySize = ReadUInt16BE(bytes, vcgtOffset + 16);
                if (numChannels != 3) throw new Exception("Unexpected vcgt channel count: " + numChannels);

                int dataStart = vcgtOffset + 18;
                double[][] raw = new double[3][];
                for (int c = 0; c < 3; c++) raw[c] = new double[numEntries];

                for (int c = 0; c < 3; c++)
                {
                    for (int e = 0; e < numEntries; e++)
                    {
                        int off = dataStart + (c * numEntries + e) * entrySize;
                        if (entrySize == 1) raw[c][e] = bytes[off] / 255.0;
                        else if (entrySize == 2) raw[c][e] = ReadUInt16BE(bytes, off) / 65535.0;
                        else throw new Exception("Unsupported vcgt entry size: " + entrySize);
                    }
                }

                for (int c = 0; c < 3; c++)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        if (numEntries == 256) curve[c][i] = raw[c][i];
                        else
                        {
                            int srcIdx = Math.Min(numEntries - 1, (int)Math.Round(i * (numEntries - 1) / 255.0));
                            curve[c][i] = raw[c][srcIdx];
                        }
                    }
                }
            }
            else if (gammaType == 1)
            {
                int baseOff = vcgtOffset + 12;
                for (int c = 0; c < 3; c++)
                {
                    int chOff = baseOff + c * 12;
                    double g = ReadInt32BE(bytes, chOff) / 65536.0;
                    double min = ReadInt32BE(bytes, chOff + 4) / 65536.0;
                    double max = ReadInt32BE(bytes, chOff + 8) / 65536.0;
                    for (int i = 0; i < 256; i++)
                        curve[c][i] = Math.Pow(i / 255.0, g) * (max - min) + min;
                }
            }
            else
            {
                throw new Exception("Unsupported vcgt gamma type: " + gammaType);
            }

            return curve;
        }
    }
}
