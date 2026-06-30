// NvApi.cs — NVAPI wrapper for RazerTray
// Dynamic load of nvapi64.dll via nvapi_QueryInterface by ordinal.
// DRS (Driver Settings) control + GPU utilization/clocks monitoring.
//
// Function IDs from NVIDIA NVAPI SDK (github.com/NVIDIA/nvapi) + falahati/NvAPIWrapper.
// MIT-licensed struct offsets verified against NvApiDriverSettings.h (R590).
//
// Integrates with RazerTray via:
//   NvApi.Initialize()           — load nvapi64 + init
//   NvApi.SetGlobalPowerMode()   — ApplyMode() hook
//   NvApi.SetGameProfilePower()  — GameDetectionTick() hook
//   NvApi.GetGpuClocks()         — UpdateTooltip() enrichment
//   NvApi.GetGpuUtilization()    — UpdateTooltip() enrichment

using System;
using System.Runtime.InteropServices;

namespace RazerTray
{
    // -----------------------------------------------------------------------
    // NVAPI return codes
    // -----------------------------------------------------------------------
    enum NvStatus
    {
        OK = 0,
        ERROR = -1,
        LIBRARY_NOT_FOUND = -2,
        NO_IMPLEMENTATION = -3,
        SETTING_NOT_FOUND = -4,
        DOUBLE_INIT = -5,
        PROFILE_NOT_FOUND = -6
    }

    // -----------------------------------------------------------------------
    // Power management mode values (PREFERRED_PSTATE)
    // -----------------------------------------------------------------------
    enum NvPowerMode : uint
    {
        Adaptive = 0,
        PreferMax = 1,
        DriverControlled = 2,
        ConsistentPerf = 3,
        PreferMin = 4,
        OptimalPower = 5
    }

    // -----------------------------------------------------------------------
    // Optimus rendering mode (SHIM_RENDERING_MODE)
    // -----------------------------------------------------------------------
    enum NvOptimusMode : uint
    {
        Auto = 0,
        Optimus = 1,
        HighPerfGPU = 2
    }

    // -----------------------------------------------------------------------
    // NvApi: static NVAPI wrapper
    // -----------------------------------------------------------------------
    static class NvApi
    {
        // ========== Function IDs (from NVAPI SDK + NvAPIWrapper) ==========
        const uint FID_INITIALIZE                = 0x0150E828;
        const uint FID_UNLOAD                    = 0xD22BDD7E;
        const uint FID_DRS_CREATE_SESSION        = 0x0694D52E;
        const uint FID_DRS_DESTROY_SESSION       = 0xDAD9CFF8;
        const uint FID_DRS_LOAD_SETTINGS         = 0x375DBD6B;
        const uint FID_DRS_SAVE_SETTINGS         = 0xFCBC7E14;
        const uint FID_DRS_GET_CURRENT_GLOBAL    = 0x617BFF9F;
        const uint FID_DRS_SET_SETTING           = 0x5B261DA8;
        const uint FID_DRS_FIND_PROFILE_BY_NAME  = 0x7E4A9A0B;
        const uint FID_DRS_CREATE_PROFILE        = 0xCC176068;
        const uint FID_ENUM_PHYSICAL_GPUS        = 0xE5AC921F;
        const uint FID_GPU_DYNAMIC_PSTATES_EX    = 0x60DED2ED;
        const uint FID_GPU_CLOCK_FREQUENCIES     = 0xDCB616C3;

        // ========== Setting IDs (NvApiDriverSettings.h) ==========
        const uint SETTING_PREFERRED_PSTATE      = 0x1057EB71;
        const uint SETTING_SHIM_RENDERING_MODE   = 0x10F9DC81;  // Optimus

        // ========== State ==========
        static IntPtr _module;
        static bool _loaded;
        static bool _initialized;
        static IntPtr _gpuHandle;

        // ========== kernel32 ==========
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr hModule);

        // ========== nvapi_QueryInterface delegate ==========
        // The only export from nvapi64.dll — returns function pointer by ordinal
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr QueryInterfaceDelegate(uint functionId);
        static QueryInterfaceDelegate _queryInterface;

        // ========== Resolved function delegates ==========
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvInitDelegate();
        static NvInitDelegate _initFn;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvUnloadDelegate();
        static NvUnloadDelegate _unloadFn;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsCreateSessionDelegate(ref IntPtr session);
        static DrsCreateSessionDelegate _drsCreateSession;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsDestroySessionDelegate(IntPtr session);
        static DrsDestroySessionDelegate _drsDestroySession;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsLoadSettingsDelegate(IntPtr session);
        static DrsLoadSettingsDelegate _drsLoadSettings;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsSaveSettingsDelegate(IntPtr session);
        static DrsSaveSettingsDelegate _drsSaveSettings;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsGetCurrentGlobalProfileDelegate(IntPtr session, ref IntPtr profile);
        static DrsGetCurrentGlobalProfileDelegate _drsGetGlobalProfile;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsSetSettingDelegate(IntPtr session, IntPtr profile, IntPtr setting);
        static DrsSetSettingDelegate _drsSetSetting;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsFindProfileByNameDelegate(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string name, ref IntPtr profile);
        static DrsFindProfileByNameDelegate _drsFindProfileByName;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DrsCreateProfileDelegate(IntPtr session, IntPtr profileInfo, ref IntPtr profile);
        static DrsCreateProfileDelegate _drsCreateProfile;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] gpus, ref int count);
        static EnumPhysicalGpusDelegate _enumPhysicalGpus;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetDynamicPstatesExDelegate(IntPtr gpu, IntPtr info);
        static GpuGetDynamicPstatesExDelegate _gpuGetPstates;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetClockFrequenciesDelegate(IntPtr gpu, IntPtr clocks);
        static GpuGetClockFrequenciesDelegate _gpuGetClocks;

        // ========== Public API ==========

        /// <summary>
        /// Load nvapi64.dll, resolve function pointers, initialize NVAPI.
        /// Safe to call multiple times (re-entrant guard).
        /// </summary>
        public static NvStatus Initialize()
        {
            if (_loaded && _initialized)
                return NvStatus.DOUBLE_INIT;

            if (!_loaded)
            {
                _module = LoadLibrary("nvapi64.dll");
                if (_module == IntPtr.Zero)
                    return NvStatus.LIBRARY_NOT_FOUND;

                // Resolve the gateway function
                IntPtr qiPtr = GetProcAddress(_module, "nvapi_QueryInterface");
                if (qiPtr == IntPtr.Zero)
                {
                    FreeLibrary(_module);
                    _module = IntPtr.Zero;
                    return NvStatus.LIBRARY_NOT_FOUND;
                }
                _queryInterface = (QueryInterfaceDelegate)
                    Marshal.GetDelegateForFunctionPointer(qiPtr, typeof(QueryInterfaceDelegate));

                // Resolve all function pointers
                _initFn = GetFn<NvInitDelegate>(FID_INITIALIZE);
                _unloadFn = GetFn<NvUnloadDelegate>(FID_UNLOAD);
                _drsCreateSession = GetFn<DrsCreateSessionDelegate>(FID_DRS_CREATE_SESSION);
                _drsDestroySession = GetFn<DrsDestroySessionDelegate>(FID_DRS_DESTROY_SESSION);
                _drsLoadSettings = GetFn<DrsLoadSettingsDelegate>(FID_DRS_LOAD_SETTINGS);
                _drsSaveSettings = GetFn<DrsSaveSettingsDelegate>(FID_DRS_SAVE_SETTINGS);
                _drsGetGlobalProfile = GetFn<DrsGetCurrentGlobalProfileDelegate>(FID_DRS_GET_CURRENT_GLOBAL);
                _drsSetSetting = GetFn<DrsSetSettingDelegate>(FID_DRS_SET_SETTING);
                _drsFindProfileByName = GetFn<DrsFindProfileByNameDelegate>(FID_DRS_FIND_PROFILE_BY_NAME);
                _drsCreateProfile = GetFn<DrsCreateProfileDelegate>(FID_DRS_CREATE_PROFILE);
                _enumPhysicalGpus = GetFn<EnumPhysicalGpusDelegate>(FID_ENUM_PHYSICAL_GPUS);
                _gpuGetPstates = GetFn<GpuGetDynamicPstatesExDelegate>(FID_GPU_DYNAMIC_PSTATES_EX);
                _gpuGetClocks = GetFn<GpuGetClockFrequenciesDelegate>(FID_GPU_CLOCK_FREQUENCIES);

                _loaded = true;
            }

            if (_initFn == null)
                return NvStatus.NO_IMPLEMENTATION;

            int ret = _initFn();
            if (ret != 0)
                return (NvStatus)ret;

            // Enumerate first physical GPU for monitoring
            _gpuHandle = EnumerateFirstGpu();

            _initialized = true;
            return NvStatus.OK;
        }

        /// <summary>
        /// Shutdown NVAPI and free library.
        /// </summary>
        public static void Shutdown()
        {
            if (!_loaded) return;
            if (_initialized && _unloadFn != null)
                _unloadFn();
            _initialized = false;
            _loaded = false;
            _gpuHandle = IntPtr.Zero;
            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
                _module = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Whether NVAPI DRS operations are fully available.
        /// Checks all DRS delegates used by SetGlobalPowerMode etc.
        /// </summary>
        static bool HasDrsSupport
        {
            get
            {
                return _drsCreateSession != null
                    && _drsLoadSettings != null
                    && _drsSaveSettings != null
                    && _drsGetGlobalProfile != null
                    && _drsSetSetting != null
                    && _drsDestroySession != null;
            }
        }

        /// <summary>
        /// Whether NVAPI is initialized and functional.
        /// </summary>
        public static bool IsAvailable
        {
            get { return _loaded && _initialized && _initFn != null; }
        }

        // ========== DRS Profile Operations ==========

        /// <summary>
        /// Set the global power management mode (PREFERRED_PSTATE).
        /// 0 = Adaptive, 1 = Prefer Max Performance
        /// </summary>
        public static NvStatus SetGlobalPowerMode(NvPowerMode mode)
        {
            if (!IsAvailable) return NvStatus.LIBRARY_NOT_FOUND;
            return DrsSetDwordSetting(IntPtr.Zero, SETTING_PREFERRED_PSTATE, (uint)mode);
        }

        /// <summary>
        /// Set the global Optimus rendering mode (SHIM_RENDERING_MODE).
        /// 0 = Auto, 2 = High-performance NVIDIA GPU
        /// </summary>
        public static NvStatus SetGlobalOptimusMode(NvOptimusMode mode)
        {
            if (!IsAvailable) return NvStatus.LIBRARY_NOT_FOUND;
            return DrsSetDwordSetting(IntPtr.Zero, SETTING_SHIM_RENDERING_MODE, (uint)mode);
        }

        /// <summary>
        /// Set power mode on a specific game profile (by name).
        /// The profile must already exist in the NVIDIA driver.
        /// </summary>
        public static NvStatus SetGameProfilePowerMode(string profileName, NvPowerMode mode)
        {
            if (!IsAvailable) return NvStatus.LIBRARY_NOT_FOUND;
            if (string.IsNullOrEmpty(profileName)) return NvStatus.ERROR;
            if (_drsFindProfileByName == null) return NvStatus.NO_IMPLEMENTATION;

            IntPtr session = IntPtr.Zero;
            IntPtr profile = IntPtr.Zero;

            try
            {
                session = DrsOpenSession();
                if (session == IntPtr.Zero) return NvStatus.ERROR;

                int ret = _drsFindProfileByName(session, profileName, ref profile);
                if (ret != 0)
                    return NvStatus.PROFILE_NOT_FOUND;

                return DrsSetDwordSettingOnProfile(session, profile, SETTING_PREFERRED_PSTATE, (uint)mode);
            }
            finally
            {
                DrsCloseSession(session);
            }
        }

        // ========== GPU Monitoring ==========

        /// <summary>
        /// Get GPU core utilization percentage (0-100).
        /// </summary>
        public static bool GetGpuUtilization(out int percent)
        {
            percent = 0;
            if (!IsAvailable || _gpuHandle == IntPtr.Zero || _gpuGetPstates == null)
                return false;

            int structSize = 4 + 4 + 4 * 4; // version + flags + 4*uint
            IntPtr buf = Marshal.AllocHGlobal(structSize);
            try
            {
                Marshal.WriteInt32(buf, 0, structSize | (1 << 16)); // version
                Marshal.WriteInt32(buf, 4, 0); // flags

                int ret = _gpuGetPstates(_gpuHandle, buf);
                if (ret != 0) return false;

                // utilization[0].raw at offset 8
                uint raw0 = (uint)Marshal.ReadInt32(buf, 8);
                uint present = raw0 & 1;
                uint pct = (raw0 >> 1) & 0x7FFFFFFF;
                if (present == 0) return false;
                percent = (int)pct;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>
        /// Get GPU core and memory clock in MHz.
        /// </summary>
        public static bool GetGpuClocks(out int gpuMHz, out int memMHz)
        {
            gpuMHz = 0;
            memMHz = 0;
            if (!IsAvailable || _gpuHandle == IntPtr.Zero || _gpuGetClocks == null)
                return false;

            int structSize = 4 + 4 + 32 * 4; // version + reserved + 32*domain
            IntPtr buf = Marshal.AllocHGlobal(structSize);
            try
            {
                Marshal.WriteInt32(buf, 0, structSize | (1 << 16)); // version
                Marshal.WriteInt32(buf, 4, 0); // reserved

                int ret = _gpuGetClocks(_gpuHandle, buf);
                if (ret != 0) return false;

                // domain[0] (graphics) at offset 8
                // domain[1] (memory) at offset 12
                uint rawGfx = (uint)Marshal.ReadInt32(buf, 8);
                uint rawMem = (uint)Marshal.ReadInt32(buf, 12);

                if ((rawGfx & 1) != 0)
                    gpuMHz = (int)((rawGfx >> 1) / 1000);
                if ((rawMem & 1) != 0)
                    memMHz = (int)((rawMem >> 1) / 1000);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // ========== Internal: DRS Helpers ==========

        /// <summary>Set a DWORD setting on the GLOBAL profile.</summary>
        static NvStatus DrsSetDwordSetting(IntPtr existingSession, uint settingId, uint value)
        {
            if (!HasDrsSupport) return NvStatus.NO_IMPLEMENTATION;

            IntPtr session = existingSession;
            bool ownSession = (session == IntPtr.Zero);
            IntPtr profile = IntPtr.Zero;
            IntPtr settingBuf = IntPtr.Zero;

            try
            {
                if (ownSession)
                {
                    session = DrsOpenSession();
                    if (session == IntPtr.Zero) return NvStatus.ERROR;
                }

                // Get global profile
                int ret = _drsGetGlobalProfile(session, ref profile);
                if (ret != 0) return (NvStatus)ret;
                if (profile == IntPtr.Zero) return NvStatus.ERROR;

                // Build NVDRS_SETTING
                settingBuf = BuildDwordSetting(settingId, value);
                if (settingBuf == IntPtr.Zero) return NvStatus.ERROR;

                ret = _drsSetSetting(session, profile, settingBuf);
                if (ret != 0) return (NvStatus)ret;

                // Save if we own the session
                if (ownSession)
                {
                    ret = _drsSaveSettings(session);
                    if (ret != 0) return (NvStatus)ret;
                }

                return NvStatus.OK;
            }
            finally
            {
                if (settingBuf != IntPtr.Zero) Marshal.FreeHGlobal(settingBuf);
                if (ownSession) DrsCloseSession(session);
            }
        }

        /// <summary>Set a DWORD setting on a specific profile handle.</summary>
        static NvStatus DrsSetDwordSettingOnProfile(IntPtr session, IntPtr profile, uint settingId, uint value)
        {
            if (!HasDrsSupport) return NvStatus.NO_IMPLEMENTATION;

            IntPtr settingBuf = IntPtr.Zero;
            try
            {
                settingBuf = BuildDwordSetting(settingId, value);
                if (settingBuf == IntPtr.Zero) return NvStatus.ERROR;

                int ret = _drsSetSetting(session, profile, settingBuf);
                if (ret != 0) return (NvStatus)ret;

                ret = _drsSaveSettings(session);
                if (ret != 0) return (NvStatus)ret;

                return NvStatus.OK;
            }
            finally
            {
                if (settingBuf != IntPtr.Zero) Marshal.FreeHGlobal(settingBuf);
            }
        }

        /// <summary>Open and load a DRS session. Returns IntPtr.Zero on failure.</summary>
        static IntPtr DrsOpenSession()
        {
            if (_drsCreateSession == null || _drsLoadSettings == null)
                return IntPtr.Zero;

            IntPtr session = IntPtr.Zero;
            int ret = _drsCreateSession(ref session);
            if (ret != 0 || session == IntPtr.Zero) return IntPtr.Zero;

            ret = _drsLoadSettings(session);
            if (ret != 0)
            {
                _drsDestroySession(session);
                return IntPtr.Zero;
            }
            return session;
        }

        /// <summary>Save and close a DRS session.</summary>
        static void DrsCloseSession(IntPtr session)
        {
            if (session != IntPtr.Zero && _drsDestroySession != null)
                _drsDestroySession(session);
        }

        // ========== Internal: Buffer Builders ==========

        /// <summary>
        /// Build an NVDRS_SETTING_V1 buffer for a DWORD setting.
        ///
        /// Layout (Pack=8):
        ///   0:   uint version             (4)
        ///   4:   wchar_t settingName[2048] (4096)
        ///  4100: uint settingId           (4)
        ///  4104: uint settingType         (4)  — 0 = DWORD
        ///  4108: uint settingLocation     (4)  — 0 = current profile
        ///  4112: uint isCurrentPredefined (4)
        ///  4116: uint isPredefinedValid   (4)
        ///  4120: uint predefinedValue     (4)  — union { dwordValue } @ offset 0
        ///  4124: padding (4092)                 — union size = 4096
        ///  8216: uint currentValue         (4)  — union { dwordValue }
        ///  8220: padding (4092)
        /// Total: 12312
        /// </summary>
        static IntPtr BuildDwordSetting(uint settingId, uint value)
        {
            int size = 12312;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                // Clear to zero
                for (int i = 0; i < size; i += 4)
                    Marshal.WriteInt32(buf, i, 0);

                // Version = sizeof(NVDRS_SETTING_V1) | (1 << 16)
                // For our layout: 12312 | 0x10000 = 0x3018 | 0x10000 = 0x13018
                int ver = size | (1 << 16);
                Marshal.WriteInt32(buf, 0, ver);

                // settingName is already zero-filled (empty string)

                // settingId
                Marshal.WriteInt32(buf, 4100, (int)settingId);

                // settingType = NVDRS_DWORD_TYPE = 0 (already zero)
                // settingLocation = NVDRS_CURRENT_PROFILE_LOCATION = 0 (already zero)
                // isCurrentPredefined = 0 (already zero)
                // isPredefinedValid = 0 (already zero)

                // predefinedValue.dwordValue at offset 4120
                Marshal.WriteInt32(buf, 4120, (int)value);

                // currentValue.dwordValue at offset 8216
                Marshal.WriteInt32(buf, 8216, (int)value);

                return buf;
            }
            catch
            {
                Marshal.FreeHGlobal(buf);
                return IntPtr.Zero;
            }
        }

        // ========== Internal: GPU Enumeration ==========

        /// <summary>Enumerate physical GPUs and return the first NVIDIA GPU handle.</summary>
        static IntPtr EnumerateFirstGpu()
        {
            if (_enumPhysicalGpus == null) return IntPtr.Zero;

            IntPtr[] gpus = new IntPtr[64];
            int count = 0;
            int ret = _enumPhysicalGpus(gpus, ref count);
            if (ret != 0 || count == 0) return IntPtr.Zero;

            return gpus[0];
        }

        // ========== Internal: Function Resolution ==========

        /// <summary>Resolve a function pointer via nvapi_QueryInterface.</summary>
        static T GetFn<T>(uint functionId) where T : class
        {
            if (_queryInterface == null) return null;
            IntPtr ptr = _queryInterface(functionId);
            if (ptr == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }
    }
}
