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
    /// It is not free. The matrix applies to the composed desktop, so while it is active
    /// DWM cannot hand a game's buffer straight to scanout: presentation drops from
    /// hardware independent flip to Composed: Flip. Gamma, contrast and brightness do not
    /// have this cost, since they live in the display controller's LUT after composition.
    ///
    /// So nothing here is touched at all unless a profile actually asks for saturation,
    /// and the effect is released as soon as it returns to neutral - rather than leaving an
    /// identity matrix in place, which would keep the composition penalty for no benefit.
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
        private static bool _effectActive;

        /// <summary>True when a non-neutral matrix is currently applied.</summary>
        public static bool IsActive { get { return _effectActive; } }

        private static bool EnsureInitialized()
        {
            if (_initialized) return true;
            _initialized = MagInitialize();
            if (!_initialized) Config.Log("MagInitialize failed; saturation unavailable.");
            return _initialized;
        }

        /// <summary>1.0 = unchanged, 0.0 = greyscale, &gt;1.0 = more saturated.</summary>
        public static bool Apply(double saturation)
        {
            // Treat anything indistinguishable from neutral as "no effect wanted", so the
            // Magnification path is never engaged just to apply an identity transform.
            if (Math.Abs(saturation - 1.0) < 0.001) return Release();

            if (!EnsureInitialized()) return false;

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

            bool ok = MagSetFullscreenColorEffect(ref effect);
            if (ok) _effectActive = true;
            return ok;
        }

        /// <summary>
        /// Drops the colour transform entirely, restoring hardware presentation. Uninitialising
        /// is what actually releases it; setting an identity matrix would not.
        /// </summary>
        public static bool Release()
        {
            if (!_initialized) return true;

            bool ok = MagUninitialize();
            _initialized = false;
            _effectActive = false;
            if (!ok) Config.Log("MagUninitialize failed.");
            return ok;
        }

        public static bool Reset()
        {
            return Release();
        }

        public static void Shutdown()
        {
            Release();
        }
    }
}
