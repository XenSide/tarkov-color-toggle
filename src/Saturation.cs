using System;
using System.Runtime.InteropServices;

namespace TarkovColor
{
    /// <summary>
    /// Desktop-wide saturation via the Magnification API's colour transform matrix.
    ///
    /// A gamma ramp is a per-channel 1D LUT and mathematically cannot change saturation
    /// (that needs channel mixing). This 5x5 matrix can, and it is GPU-vendor neutral.
    ///
    /// Caveat: the effect only lives as long as the process that set it, so this is
    /// driven by the tray app rather than by the short-lived one-shot invocations.
    /// </summary>
    public static class Saturation
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MAGCOLOREFFECT
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
            public float[] transform;
        }

        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagInitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagUninitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);

        private static bool _initialized;

        public static bool Initialize()
        {
            if (_initialized) return true;
            _initialized = MagInitialize();
            return _initialized;
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            try { Apply(1.0); } catch { }
            MagUninitialize();
            _initialized = false;
        }

        /// <summary>1.0 = unchanged, 0.0 = greyscale, &gt;1.0 = more saturated.</summary>
        public static bool Apply(double saturation)
        {
            if (!Initialize()) return false;

            float s = (float)saturation;

            // Rec. 709 luma weights.
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float ir = lr * (1 - s), ig = lg * (1 - s), ib = lb * (1 - s);

            // Row-vector convention: out[j] = sum_i in[i] * m[i][j]
            float[] m = new float[25];
            m[0] = ir + s; m[1] = ir; m[2] = ir; m[3] = 0; m[4] = 0;
            m[5] = ig; m[6] = ig + s; m[7] = ig; m[8] = 0; m[9] = 0;
            m[10] = ib; m[11] = ib; m[12] = ib + s; m[13] = 0; m[14] = 0;
            m[15] = 0; m[16] = 0; m[17] = 0; m[18] = 1; m[19] = 0;
            m[20] = 0; m[21] = 0; m[22] = 0; m[23] = 0; m[24] = 1;

            MAGCOLOREFFECT effect = new MAGCOLOREFFECT();
            effect.transform = m;
            return MagSetFullscreenColorEffect(ref effect);
        }

        public static bool Reset()
        {
            return Apply(1.0);
        }
    }
}
