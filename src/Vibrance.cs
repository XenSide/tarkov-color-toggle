using System;
using System.Runtime.InteropServices;

namespace TarkovColor
{
    /// <summary>
    /// Saturation through NVIDIA's Digital Vibrance Control.
    ///
    /// Why this exists alongside <see cref="Saturation"/>: the Magnification colour matrix
    /// applies to the composed desktop, so while it is active DWM cannot hand a game's buffer
    /// straight to scanout and presentation drops to Composed: Flip. DVC is applied in the
    /// display pipeline instead, so it costs nothing in presentation - at the price of being
    /// NVIDIA-only, and of only being able to add saturation, never remove it.
    ///
    /// NVAPI exposes nothing but nvapi_QueryInterface; everything else is looked up by numeric
    /// id. The ids for Initialize, Unload and GetAssociatedNvidiaDisplayHandle come from
    /// NVIDIA's own nvapi_interface.h (MIT). The two DVC ids are not published by NVIDIA and
    /// come from community sources - which is why nothing here is trusted blindly:
    ///
    ///   - an unresolved id yields null from QueryInterface, and the feature reports itself
    ///     unavailable rather than calling into anything;
    ///   - the current level is read back and sanity checked before any value is written.
    ///
    /// The worst case is therefore "vibrance unavailable", not a bad write.
    /// </summary>
    public static class Vibrance
    {
        // From NVIDIA/nvapi nvapi_interface.h (MIT licensed).
        private const uint ID_Initialize = 0x0150E828;
        private const uint ID_Unload = 0xD22BDD7E;
        private const uint ID_GetAssociatedNvidiaDisplayHandle = 0x35C29134;

        // Not present in the public headers; community-sourced. Guarded, see class remarks.
        private const uint ID_GetDVCInfo = 0x4085DE45;
        private const uint ID_SetDVCLevel = 0x172409B4;

        /// <summary>Level range NVIDIA uses for DVC. 0 is neutral, not a cut.</summary>
        public const int MinLevel = 0;
        public const int MaxLevel = 63;

        [StructLayout(LayoutKind.Sequential)]
        private struct NV_DISPLAY_DVC_INFO
        {
            public uint version;
            public int currentLevel;
            public int minLevel;
            public int maxLevel;
        }

        // NVAPI version fields encode the struct size in the low word and the version in the high.
        private static readonly uint DvcInfoVersion = (uint)(Marshal.SizeOf(typeof(NV_DISPLAY_DVC_INFO)) | (1 << 16));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr QueryInterfaceDelegate(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UnloadDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate int GetDisplayHandleDelegate(string displayName, ref IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDvcInfoDelegate(IntPtr display, int outputId, ref NV_DISPLAY_DVC_INFO info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetDvcLevelDelegate(IntPtr display, int outputId, int level);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static bool _probed;
        private static bool _available;
        private static QueryInterfaceDelegate _query;
        private static GetDisplayHandleDelegate _getDisplayHandle;
        private static GetDvcInfoDelegate _getInfo;
        private static SetDvcLevelDelegate _setLevel;

        /// <summary>True when DVC resolved and answered plausibly on this machine.</summary>
        public static bool IsAvailable
        {
            get
            {
                Probe();
                return _available;
            }
        }

        private static T Resolve<T>(uint id) where T : class
        {
            IntPtr fn = _query(id);
            if (fn == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            try
            {
                IntPtr module = LoadLibrary("nvapi64.dll");
                if (module == IntPtr.Zero)
                {
                    Config.Log("Vibrance: nvapi64.dll not present (no NVIDIA driver).");
                    return;
                }

                IntPtr queryPtr = GetProcAddress(module, "nvapi_QueryInterface");
                if (queryPtr == IntPtr.Zero)
                {
                    Config.Log("Vibrance: nvapi_QueryInterface not exported.");
                    return;
                }
                _query = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
                    queryPtr, typeof(QueryInterfaceDelegate));

                InitializeDelegate init = Resolve<InitializeDelegate>(ID_Initialize);
                _getDisplayHandle = Resolve<GetDisplayHandleDelegate>(ID_GetAssociatedNvidiaDisplayHandle);
                _getInfo = Resolve<GetDvcInfoDelegate>(ID_GetDVCInfo);
                _setLevel = Resolve<SetDvcLevelDelegate>(ID_SetDVCLevel);

                if (init == null || _getDisplayHandle == null || _getInfo == null || _setLevel == null)
                {
                    Config.Log("Vibrance: one or more NVAPI entry points did not resolve; disabled.");
                    return;
                }

                int status = init();
                if (status != 0)
                {
                    Config.Log("Vibrance: NvAPI_Initialize returned " + status + "; disabled.");
                    return;
                }

                // Prove it actually works before trusting it with a write.
                int current;
                _available = TryReadLevel(out current);
                Config.Log(_available
                    ? "Vibrance: available, current level " + current
                    : "Vibrance: could not read a plausible level; disabled.");
            }
            catch (Exception ex)
            {
                Config.Log("Vibrance: probe failed: " + ex.Message);
                _available = false;
            }
        }

        private static bool TryGetDisplay(out IntPtr display)
        {
            display = IntPtr.Zero;
            try
            {
                string device = Ramp.PrimaryDeviceName();
                return _getDisplayHandle(device, ref display) == 0 && display != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Config.Log("Vibrance: could not resolve the display handle: " + ex.Message);
                return false;
            }
        }

        /// <summary>Reads the level, rejecting anything that does not look like a DVC range.</summary>
        private static bool TryReadLevel(out int level)
        {
            level = 0;

            IntPtr display;
            if (!TryGetDisplay(out display)) return false;

            NV_DISPLAY_DVC_INFO info = new NV_DISPLAY_DVC_INFO();
            info.version = DvcInfoVersion;

            if (_getInfo(display, 0, ref info) != 0) return false;

            // If the ids were wrong we would be reading whatever this call actually filled in,
            // so the answer has to look like a vibrance range before it is believed.
            if (info.minLevel != MinLevel || info.maxLevel != MaxLevel) return false;
            if (info.currentLevel < info.minLevel || info.currentLevel > info.maxLevel) return false;

            level = info.currentLevel;
            return true;
        }

        public static bool TryGetLevel(out int level)
        {
            level = 0;
            Probe();
            return _available && TryReadLevel(out level);
        }

        /// <summary>Sets the level, clamped to the valid range. 0 restores NVIDIA's default.</summary>
        public static bool SetLevel(int level)
        {
            Probe();
            if (!_available) return false;

            if (level < MinLevel) level = MinLevel;
            if (level > MaxLevel) level = MaxLevel;

            IntPtr display;
            if (!TryGetDisplay(out display)) return false;

            try
            {
                int status = _setLevel(display, 0, level);
                if (status != 0)
                {
                    Config.Log("Vibrance: SetDVCLevel returned " + status);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Config.Log("Vibrance: SetDVCLevel failed: " + ex.Message);
                return false;
            }
        }

        public static bool Reset()
        {
            return SetLevel(MinLevel);
        }
    }
}
