// RazerTray.cs - Razer Blade Tray Controller v5.1
// System tray icon - switches performance modes (Balanced/Gaming/Creator)
// + Windows power plan + dynamic icon + temps + game detection
// + file logging + state persistence + sleep/resume + auto fan curve
// + auto-start + process management + NVAPI GPU control
//
// Repository: https://github.com/cloph-dsp/razertray
// License: MIT
//
// Uses libusb-1.0.dll for USB HID control transfers with the CORRECT
// packet format reverse-engineered from librazerblade (Meetem/librazerblade).
//
// Compile:
//   csc.exe /target:winexe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Web.Extensions.dll RazerTray.cs
//
// Razer USB power modes: 0=Balanced, 1=Gaming, 2=Creator
//
// Packet format (90 bytes, librazerblade-compatible):
//   [0]=status, [1]=transaction_id, [2-3]=remaining_packets(ushort),
//   [4]=protocol_type, [5]=data_size, [6]=command_class,
//   [7]=command_id(7bit type+1bit dir), [8-87]=args[80],
//   [88]=crc(XOR 2-87), [89]=reserved

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Thread = System.Threading.Thread;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RazerTray
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Single-instance guard (avoids duplication from admin-elevation
            // double-launch on startup)
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true,
                "Global\\RazerBladeTray-0e8a7f35-9c12-4f1a-b123-8ec3b5f2d901",
                out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    enum PowerMode : byte
    {
        Balanced = 0,
        Gaming = 1,
        Creator = 2
    }

    // -----------------------------------------------------------------------
    // NativeUSB: P/Invoke wrapper for libusb-1.0.dll
    // -----------------------------------------------------------------------
    class NativeUSB : IDisposable
    {
        const int LIBUSB_ENDPOINT_IN = 0x80;
        const int LIBUSB_ENDPOINT_OUT = 0x00;
        const int LIBUSB_REQUEST_TYPE_CLASS = 0x01;
        const int LIBUSB_RECIPIENT_INTERFACE = 0x01;

        public const byte RTYPE_H2D = 0x21;   // host-to-device, class, iface
        public const byte RTYPE_D2H = 0xA1;   // device-to-host, class, iface
        public const byte BREQ_SET_REPORT = 0x09;
        public const byte BREQ_GET_REPORT = 0x01;

        const ushort VID_RAZER = 0x1532;
        const ushort PID_BLADE_2019_BASE = 0x0246;
        const int RAZER_USB_IFACE = 1;
        public const int PKT_SIZE = 90;
        const int TIMEOUT_MS = 1000;

        // Command class / IDs
        public const byte CMD_CLASS = 0x0D;
        public const byte PKT_FAN = 0x01;
        public const byte PKT_POWER = 0x02;

        // libusb-1.0.dll DllImports (Cdecl)
        [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int libusb_init(out IntPtr ctx);

        [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void libusb_exit(IntPtr ctx);

        [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr libusb_open_device_with_vid_pid(IntPtr ctx, ushort vid, ushort pid);

        [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int libusb_claim_interface(IntPtr dev, int iface);

        [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int libusb_release_interface(IntPtr dev, int iface);

        [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int libusb_control_transfer(IntPtr dev, byte bmRequestType,
            byte bRequest, ushort wValue, ushort wIndex,
            byte[] data, ushort wLength, uint timeout);

        [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void libusb_close(IntPtr dev);

        IntPtr _ctx = IntPtr.Zero;
        IntPtr _devHandle = IntPtr.Zero;

        public bool IsOpen { get { return _devHandle != IntPtr.Zero; } }

        public void Open()
        {
            if (_ctx == IntPtr.Zero)
            {
                int ret = libusb_init(out _ctx);
                if (ret != 0)
                    throw new Exception("libusb_init failed: error " + ret);
            }
            _devHandle = libusb_open_device_with_vid_pid(_ctx, VID_RAZER, PID_BLADE_2019_BASE);
            if (_devHandle == IntPtr.Zero)
                throw new Exception("Razer Blade device not found (VID=1532, PID=0246)");

            int r = libusb_claim_interface(_devHandle, RAZER_USB_IFACE);
            if (r != 0)
                throw new Exception("libusb_claim_interface(" + RAZER_USB_IFACE + ") failed: error " + r);
        }

        public void Close()
        {
            if (_devHandle != IntPtr.Zero)
            {
                libusb_release_interface(_devHandle, RAZER_USB_IFACE);
                libusb_close(_devHandle);
                _devHandle = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Close();
            if (_ctx != IntPtr.Zero)
            {
                libusb_exit(_ctx);
                _ctx = IntPtr.Zero;
            }
        }

        // ---- CRC ----
        public static byte CalcCrc(byte[] pkt)
        {
            byte crc = 0;
            for (int i = 2; i <= 87; i++) crc ^= pkt[i];
            return crc;
        }

        // ---- Packet building ----
        public static byte[] BuildPacket(byte cmdClass, byte cmdId, byte[] args)
        {
            byte[] pkt = new byte[PKT_SIZE];
            // [1] transaction_id
            pkt[1] = 0;
            // [2-3] remaining_packets
            pkt[2] = 0; pkt[3] = 0;
            // [4] protocol_type
            pkt[4] = 0;
            // [5] data_size
            pkt[5] = (byte)(args != null ? Math.Min(args.Length, 80) : 0);
            // [6] command_class
            pkt[6] = cmdClass;
            // [7] command_id (7-bit type + 1-bit direction)
            pkt[7] = cmdId;
            // [8-87] args
            if (args != null)
            {
                int copyLen = Math.Min(args.Length, 80);
                Array.Copy(args, 0, pkt, 8, copyLen);
            }
            // [88] CRC
            pkt[88] = CalcCrc(pkt);
            // [89] reserved
            pkt[89] = 0;
            return pkt;
        }

        // ---- Send packet (H2D) ----
        public int SendPacket(byte cmdClass, byte cmdId, byte[] args)
        {
            if (!IsOpen)
                throw new InvalidOperationException("USB device not open");
            byte[] pkt = BuildPacket(cmdClass, cmdId, args);
            return libusb_control_transfer(
                _devHandle, RTYPE_H2D, BREQ_SET_REPORT,
                0x0300, 0, pkt, PKT_SIZE, TIMEOUT_MS);
        }

        // ---- Send then read response (H2D + D2H) ----
        public int ReadPacket(byte cmdClass, byte cmdId, byte[] args, byte[] response)
        {
            if (!IsOpen)
                throw new InvalidOperationException("USB device not open");
            // Write query
            byte[] pkt = BuildPacket(cmdClass, cmdId, args);
            int ret = libusb_control_transfer(
                _devHandle, RTYPE_H2D, BREQ_SET_REPORT,
                0x0300, 0, pkt, PKT_SIZE, TIMEOUT_MS);
            if (ret < 0) return ret;

            // Read response
            byte[] readBuf = new byte[PKT_SIZE];
            ret = libusb_control_transfer(
                _devHandle, RTYPE_D2H, BREQ_GET_REPORT,
                0x0100, 0, readBuf, PKT_SIZE, TIMEOUT_MS);
            if (ret > 0 && response != null)
            {
                int copyLen = Math.Min(response.Length, 80);
                Array.Copy(readBuf, 8, response, 0, copyLen);
            }
            return ret;
        }
    }

    // -----------------------------------------------------------------------
    // DeviceController: high-level USB operations for Razer Blade
    // -----------------------------------------------------------------------
    class DeviceController : IDisposable
    {
        NativeUSB _usb;

        public bool IsConnected { get { return _usb != null && _usb.IsOpen; } }

        public DeviceController()
        {
            _usb = new NativeUSB();
        }

        public void Connect()
        {
            if (!_usb.IsOpen)
                _usb.Open();
        }

        public void Disconnect()
        {
            _usb.Close();
        }

        public void Dispose()
        {
            if (_usb != null)
            {
                _usb.Dispose();
                _usb = null;
            }
        }

        // ---- Power Mode ----
        // SetPowerMode(mode, autoFan): autoFan=true returns fan control to EC
        public bool SetPowerMode(PowerMode mode, bool autoFan)
        {
            try
            {
                if (!IsConnected) return false;
                byte[] args = { 0x00, 0x01, (byte)mode, (byte)(autoFan ? 0 : 1) };
                int ret = _usb.SendPacket(NativeUSB.CMD_CLASS, NativeUSB.PKT_POWER, args);
                return ret >= 0;
            }
            catch { return false; }
        }

        // QueryPowerMode(): returns current mode or null on failure
        public PowerMode? QueryPowerMode()
        {
            try
            {
                if (!IsConnected) return null;
                byte[] response = new byte[80];
                byte cmdId = (byte)(NativeUSB.PKT_POWER | 0x80); // query bit
                int ret = _usb.ReadPacket(NativeUSB.CMD_CLASS, cmdId,
                    new byte[] { 0x00, 0x01 }, response);
                if (ret >= 0 && response[0] <= 2)
                    return (PowerMode)response[0];
                return null;
            }
            catch { return null; }
        }

        // ---- Fan Speed ----
        public bool SetFanSpeed(byte fanId, ushort rpm)
        {
            try
            {
                if (!IsConnected) return false;
                // Must first set manual fan mode
                byte[] modeArgs = { 0x00, 0x01, 0x00, 0x01 }; // manual
                _usb.SendPacket(NativeUSB.CMD_CLASS, NativeUSB.PKT_POWER, modeArgs);
                // Now set fan speed
                byte speedDiv100 = (byte)Math.Max(0, Math.Min(255, rpm / 100));
                byte[] args = { 0x00, fanId, speedDiv100 };
                int ret = _usb.SendPacket(NativeUSB.CMD_CLASS, NativeUSB.PKT_FAN, args);
                return ret >= 0;
            }
            catch { return false; }
        }

        // QueryFanSpeed(fanId): returns RPM or 0
        public ushort QueryFanSpeed(byte fanId)
        {
            try
            {
                if (!IsConnected) return 0;
                byte[] response = new byte[80];
                byte cmdId = (byte)(NativeUSB.PKT_FAN | 0x80); // query bit
                int ret = _usb.ReadPacket(NativeUSB.CMD_CLASS, cmdId,
                    new byte[] { 0x00, fanId }, response);
                if (ret >= 0)
                    return (ushort)(response[2] * 100);
                return 0;
            }
            catch { return 0; }
        }

        // ---- Combo: set mode + optionally restore auto fan ----
        public bool SetModeWithFan(PowerMode mode, bool autoFan = true, ushort? fanRpm = null)
        {
            try
            {
                if (!IsConnected) return false;

                // 1. Set power mode
                if (!SetPowerMode(mode, autoFan: false))
                    return false;

                // 2. If fanRpm specified, set fan speed
                if (fanRpm.HasValue && fanRpm.Value > 0)
                {
                    SetFanSpeed(0, fanRpm.Value);
                    SetFanSpeed(1, fanRpm.Value);
                }

                // 3. If autoFan requested, restore EC auto control
                if (autoFan)
                    SetPowerMode(mode, autoFan: true);

                return true;
            }
            catch { return false; }
        }
    }

    // -----------------------------------------------------------------------
    // TemperatureMonitor: CPU/GPU temp via WMI
    // -----------------------------------------------------------------------
    class TemperatureMonitor
    {
        // CPU: MSAcpi_ThermalZoneTemperature (root\WMI)
        // Returns Celsius or 0
        public float GetCpuTemperature()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    float sum = 0;
                    int count = 0;
                    foreach (var obj in searcher.Get())
                    {
                        uint raw = Convert.ToUInt32(obj["CurrentTemperature"]);
                        // Raw value is tenths of Kelvin
                        sum += (raw / 10.0f) - 273.15f;
                        count++;
                    }
                    if (count > 0) return sum / count;
                }
            }
            catch { }
            return 0;
        }

        // GPU: NVML_GPU via root\cimv2\NV (Nvidia driver WMI provider)
        public float GetGpuTemperature()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\cimv2\NV",
                    "SELECT Temperature FROM NVML_GPU"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return Convert.ToSingle(obj["Temperature"]);
                    }
                }
            }
            catch { }
            return 0;
        }
    }

    // -----------------------------------------------------------------------
    // PowerPlanManager: Windows power plan switching via powercfg
    // -----------------------------------------------------------------------
    class PowerPlanManager
    {
        // Known plan GUIDs (Windows 10/11)
        public static readonly Guid GUID_BALANCED = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid GUID_HIGH_PERFORMANCE = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid GUID_POWER_SAVER = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        public static readonly Guid GUID_BITSUM_HIGH_PERF = new Guid("dd7348fa-b3ba-4af1-b0d1-8b32566769d8");

        // Returns GUID of currently active plan
        public Guid CurrentPlanGuid()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    // Output: "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)"
                    int idx = line.IndexOf("GUID: ");
                    if (idx >= 0)
                    {
                        string guidStr = line.Substring(idx + 6, 36);
                        return new Guid(guidStr);
                    }
                }
            }
            catch { }
            return Guid.Empty;
        }

        public bool SetActivePlan(Guid planGuid)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/setactive " + planGuid.ToString("B"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public Guid ModeToPlanGuid(PowerMode mode)
        {
            switch (mode)
            {
                case PowerMode.Balanced: return GUID_BALANCED;
                case PowerMode.Gaming: return GUID_BITSUM_HIGH_PERF; // Bitsum Highest Performance
                case PowerMode.Creator: return GUID_BALANCED;
                default: return GUID_BALANCED;
            }
        }

        // ---- Power saving subparameter overrides (CPU, PCIe, disk) ----
        static void RunPowerCfg(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                }
            }
            catch { }
        }

        /// <summary>
        /// Apply power-saving subparameter overrides on the active plan.
        /// Disables turbo boost, maxes PCIe ASPM, sets disk timeout.
        /// </summary>
        public static void ApplyPowerSaver()
        {
            // CPU max 99% — disables turbo boost
            RunPowerCfg("-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99");
            // CPU min 5%
            RunPowerCfg("-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5");
            // PCIe ASPM maximum savings (2 = MaxPowerSavings)
            RunPowerCfg("-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PCIEXPRESS ASPM 2");
            // Disk timeout 10 minutes
            RunPowerCfg("-change -disk-timeout-ac 10");
        }

        /// <summary>
        /// Restore normal subparameter values on the active plan.
        /// </summary>
        public static void RemovePowerSaver()
        {
            // CPU max 100% — re-enable turbo boost
            RunPowerCfg("-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100");
            // CPU min 5%
            RunPowerCfg("-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5");
            // PCIe ASPM moderate (1 = ModerateSavings)
            RunPowerCfg("-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PCIEXPRESS ASPM 1");
            // No disk timeout
            RunPowerCfg("-change -disk-timeout-ac 0");
        }
    }

    // -----------------------------------------------------------------------
    // User32: Foreground window detection
    // -----------------------------------------------------------------------
    class User32
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    // -----------------------------------------------------------------------
    // NativeProcess: P/Invoke for process priority/Io/power-throttling
    // -----------------------------------------------------------------------
    static class NativeProcess
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr hProcess,
            int ProcessInformationClass, IntPtr ProcessInformation, int ProcessInformationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        // ProcessInformationClass constants
        public const int ProcessPowerThrottling = 29;
        public const int ProcessIoPriority = 33;

        // Access flags
        public const uint PROCESS_SET_INFORMATION = 0x0200;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;       // 1
            public uint ControlMask;   // bitmask of flags to control
            public uint StateMask;     // bitmask of flags to enable
        }

        public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;
        public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 4;
    }

    // -----------------------------------------------------------------------
    // ProcessRule: describes settings to apply to a process
    // -----------------------------------------------------------------------
    class ProcessRule
    {
        public string Exe { get; set; }              // "bitwig studio.exe"
        public ProcessPriorityClass? Priority { get; set; }
        public int? IoPriority { get; set; }         // 0=VeryLow 1=Low 2=Normal 3=High
        public IntPtr? AffinityMask { get; set; }
        public bool NoPowerThrottling { get; set; }
        public string ModeHint { get; set; }         // "Audio" or "Game" for power mode hint
    }

    // -----------------------------------------------------------------------
    // ProcessManager: applies priority/IO/affinity/power-throttle to processes
    // -----------------------------------------------------------------------
    class ProcessManager : IDisposable
    {
        // Built-in system rules (shipped with the app, not user-configurable)
        static readonly List<ProcessRule> SystemRules = new List<ProcessRule>
        {
            new ProcessRule {
                Exe = "bitwig studio.exe", Priority = ProcessPriorityClass.RealTime,
                IoPriority = 3, AffinityMask = new IntPtr(0xFFD), // skip core 1
                NoPowerThrottling = true, ModeHint = "Audio"
            },
            new ProcessRule {
                Exe = "bitwigaudioengine-x64-avx2.exe", Priority = ProcessPriorityClass.High,
                IoPriority = 3, AffinityMask = new IntPtr(0xFFD),
                NoPowerThrottling = true, ModeHint = "Audio"
            },
            new ProcessRule {
                Exe = "bitwigpluginhost-x64-sse41.exe", Priority = ProcessPriorityClass.High,
                IoPriority = 3, NoPowerThrottling = true, ModeHint = "Audio"
            },
            new ProcessRule {
                Exe = "bitwigpluginhost-x86-sse41.exe", Priority = ProcessPriorityClass.High,
                IoPriority = 3, NoPowerThrottling = true, ModeHint = "Audio"
            },
            new ProcessRule {
                Exe = "audiodg.exe", Priority = ProcessPriorityClass.High,
                IoPriority = 3, NoPowerThrottling = true, ModeHint = "Audio"
            },
            new ProcessRule {
                Exe = "cortexlauncherservice.exe", Priority = ProcessPriorityClass.BelowNormal,
                ModeHint = null
            },
        };

        // User rules derived from game-modes.config
        List<ProcessRule> _userRules = new List<ProcessRule>();
        object _userRulesLock = new object();

        // Cache of recently applied (pid, timestamp) to avoid hammering
        Dictionary<int, DateTime> _applied = new Dictionary<int, DateTime>();
        const int CacheSeconds = 10;

        System.Threading.Timer _timer;
        AppLogger _logger;
        bool _disposed;

        public ProcessManager(AppLogger logger)
        {
            _logger = logger;
        }

        public void Start()
        {
            _timer = new System.Threading.Timer(Tick, null, 5000, 5000);
        }

        public void SetUserRules(List<GameConfig> games)
        {
            lock (_userRulesLock)
            {
                _userRules.Clear();
                if (games == null) return;
                foreach (var g in games)
                {
                    var rule = new ProcessRule { Exe = g.exe };
                    if (!string.IsNullOrEmpty(g.priority))
                    {
                        switch (g.priority.ToLowerInvariant())
                        {
                            case "real time": rule.Priority = ProcessPriorityClass.RealTime; break;
                            case "high":      rule.Priority = ProcessPriorityClass.High; break;
                            case "above normal": rule.Priority = ProcessPriorityClass.AboveNormal; break;
                            case "normal":    rule.Priority = ProcessPriorityClass.Normal; break;
                            case "below normal": rule.Priority = ProcessPriorityClass.BelowNormal; break;
                            case "idle":      rule.Priority = ProcessPriorityClass.Idle; break;
                        }
                    }
                    if (!string.IsNullOrEmpty(g.ioPriority))
                    {
                        switch (g.ioPriority.ToLowerInvariant())
                        {
                            case "high":     rule.IoPriority = 3; break;
                            case "normal":   rule.IoPriority = 2; break;
                            case "low":      rule.IoPriority = 1; break;
                            case "very low": rule.IoPriority = 0; break;
                        }
                    }
                    if (!string.IsNullOrEmpty(g.cpuAffinity))
                    {
                        rule.AffinityMask = ParseAffinity(g.cpuAffinity);
                    }
                    rule.NoPowerThrottling = g.noPowerThrottling;
                    if (g.mode != null && g.mode.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0)
                        rule.ModeHint = "Audio";
                    else if (g.mode != null && g.mode.IndexOf("Game", StringComparison.OrdinalIgnoreCase) >= 0)
                        rule.ModeHint = "Game";
                    _userRules.Add(rule);
                }
            }
        }

        static IntPtr ParseAffinity(string spec)
        {
            try
            {
                // "0,2-11" style: comma-separated numbers or ranges
                ulong mask = 0;
                var parts = spec.Split(',');
                foreach (var p in parts)
                {
                    var trimmed = p.Trim();
                    if (trimmed.Contains('-'))
                    {
                        var range = trimmed.Split('-');
                        int start = int.Parse(range[0].Trim());
                        int end = int.Parse(range[1].Trim());
                        for (int i = start; i <= end; i++)
                            mask |= (1UL << i);
                    }
                    else
                    {
                        int cpu = int.Parse(trimmed);
                        mask |= (1UL << cpu);
                    }
                }
                return new IntPtr((long)mask);
            }
            catch { return IntPtr.Zero; }
        }

        void Tick(object state)
        {
            try
            {
                // Collect all rules
                var allRules = new List<ProcessRule>(SystemRules);
                lock (_userRulesLock)
                    allRules.AddRange(_userRules);

                // Group by exe for efficient lookup
                var byExe = new Dictionary<string, List<ProcessRule>>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in allRules)
                {
                    if (string.IsNullOrEmpty(r.Exe)) continue;
                    if (!byExe.ContainsKey(r.Exe))
                        byExe[r.Exe] = new List<ProcessRule>();
                    byExe[r.Exe].Add(r);
                }

                // Scan all processes once
                Process[] procs;
                try { procs = Process.GetProcesses(); }
                catch { return; }

                foreach (var proc in procs)
                {
                    try
                    {
                        string exeName = proc.ProcessName + ".exe";
                        List<ProcessRule> rules;
                        if (!byExe.TryGetValue(exeName, out rules)) continue;

                        // Skip if recently applied for this PID
                        if (_applied.ContainsKey(proc.Id) &&
                            (DateTime.Now - _applied[proc.Id]).TotalSeconds < CacheSeconds)
                            continue;

                        foreach (var rule in rules)
                        {
                            ApplyToProcess(proc, rule);
                        }
                        _applied[proc.Id] = DateTime.Now;

                        // Clean stale cache
                        var stale = _applied.Where(kv => (DateTime.Now - kv.Value).TotalSeconds > CacheSeconds * 3).Select(kv => kv.Key).ToList();
                        foreach (var k in stale) _applied.Remove(k);
                    }
                    catch { }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }

        void ApplyToProcess(Process proc, ProcessRule rule)
        {
            try
            {
                if (rule.Priority.HasValue)
                {
                    proc.PriorityClass = rule.Priority.Value;
                }
            }
            catch { /* access denied etc - normal for system processes */ }

            // I/O priority and power throttling need OpenProcess with extra rights
            try
            {
                uint access = NativeProcess.PROCESS_SET_INFORMATION;
                if (rule.IoPriority.HasValue) access |= NativeProcess.PROCESS_SET_INFORMATION;
                if (rule.NoPowerThrottling) access |= NativeProcess.PROCESS_SET_LIMITED_INFORMATION;

                IntPtr hProc = NativeProcess.OpenProcess(access, false, (uint)proc.Id);
                if (hProc == IntPtr.Zero) return;

                try
                {
                    if (rule.IoPriority.HasValue)
                    {
                        int ioPrio = rule.IoPriority.Value;
                        int cb = Marshal.SizeOf(ioPrio);
                        IntPtr ptr = Marshal.AllocHGlobal(cb);
                        try
                        {
                            Marshal.StructureToPtr(ioPrio, ptr, false);
                            NativeProcess.SetProcessInformation(hProc,
                                NativeProcess.ProcessIoPriority, ptr, cb);
                        }
                        finally { Marshal.FreeHGlobal(ptr); }
                    }

                    if (rule.NoPowerThrottling)
                    {
                        var state = new NativeProcess.PROCESS_POWER_THROTTLING_STATE
                        {
                            Version = 1,
                            ControlMask = NativeProcess.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                            StateMask = 0 // 0 = disable throttling
                        };
                        int cb = Marshal.SizeOf(state);
                        IntPtr ptr = Marshal.AllocHGlobal(cb);
                        try
                        {
                            Marshal.StructureToPtr(state, ptr, false);
                            NativeProcess.SetProcessInformation(hProc,
                                NativeProcess.ProcessPowerThrottling, ptr, cb);
                        }
                        finally { Marshal.FreeHGlobal(ptr); }
                    }

                    // Set affinity (requires PROCESS_SET_INFORMATION)
                    if (rule.AffinityMask.HasValue && rule.AffinityMask.Value != IntPtr.Zero)
                    {
                        try { proc.ProcessorAffinity = rule.AffinityMask.Value; }
                        catch { }
                    }
                }
                finally
                {
                    NativeProcess.CloseHandle(hProc);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_timer != null) { _timer.Dispose(); _timer = null; }
            }
        }
    }

    // -----------------------------------------------------------------------
    // GameDetector: reads game-modes.config, detects running games
    // -----------------------------------------------------------------------
    class GpuProfileConfig
    {
        // Power management mode: null=no change, 0=Adaptive, 1=PreferMax
        public int? gpuPowerMode { get; set; }
        // Optimus rendering mode: null=no change, 0=Auto, 2=HighPerfGPU
        public int? optimusRenderingMode { get; set; }
    }

    class GameConfig
    {
        public string name { get; set; }
        public string exe { get; set; }
        public string mode { get; set; }   // "Game Mode" or "Audio Mode"
        public int fanSpeed { get; set; }
        public string priority { get; set; }        // "real time","high","above normal","normal","below normal","idle"
        public string ioPriority { get; set; }      // "high","normal","low","very low"
        public string cpuAffinity { get; set; }     // e.g. "0,2-11" or null=all
        public bool noPowerThrottling { get; set; } // disable Efficiency Mode
        // NVIDIA GPU profile override (null = no NV change)
        public GpuProfileConfig gpuProfile { get; set; }
    }

    class ConfigFile
    {
        public List<GameConfig> Games { get; set; }

        public static ConfigFile Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new ConfigFile { Games = new List<GameConfig>() };

                string json = File.ReadAllText(path);
                var serializer = new JavaScriptSerializer();
                var list = serializer.Deserialize<List<GameConfig>>(json);
                return new ConfigFile { Games = list ?? new List<GameConfig>() };
            }
            catch
            {
                return new ConfigFile { Games = new List<GameConfig>() };
            }
        }
    }

    class GameDetector
    {
        ConfigFile _config;
        string _configPath;
        DateTime _lastLoad = DateTime.MinValue;

        public string ConfigPath
        {
            get { return _configPath; }
            set
            {
                _configPath = value;
                Reload();
            }
        }

        public GameDetector(string configPath = null)
        {
            _configPath = configPath ?? Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "game-modes.config");
            _config = ConfigFile.Load(_configPath);
            _lastLoad = DateTime.Now;
        }

        public void Reload()
        {
            if (!string.IsNullOrEmpty(_configPath))
            {
                _config = ConfigFile.Load(_configPath);
                _lastLoad = DateTime.Now;
            }
        }

        // Returns the GameConfig for the currently focused window, or null
        public GameConfig DetectForegroundGame()
        {
            IntPtr hWnd = User32.GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            uint pid = 0;
            User32.GetWindowThreadProcessId(hWnd, out pid);
            if (pid == 0) return null;

            try
            {
                Process proc = Process.GetProcessById((int)pid);
                string exeName = proc.ProcessName; // without .exe
                string fullExe = exeName + ".exe";

                foreach (var game in _config.Games)
                {
                    if (string.Equals(game.exe, fullExe, StringComparison.OrdinalIgnoreCase))
                        return game;
                }
            }
            catch { }

            return null;
        }

        // Check if a specific exe is running
        public bool IsGameRunning(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return false;
            string nameNoExt = Path.GetFileNameWithoutExtension(exeName);
            try
            {
                var procs = Process.GetProcessesByName(nameNoExt);
                return procs.Length > 0;
            }
            catch { return false; }
        }

        // Auto-reload if config modified
        public void CheckReload()
        {
            if (!string.IsNullOrEmpty(_configPath) && File.Exists(_configPath))
            {
                DateTime lastWrite = File.GetLastWriteTime(_configPath);
                if (lastWrite > _lastLoad)
                    Reload();
            }
        }

        public List<GameConfig> GetGames()
        {
            return _config.Games;
        }
    }

    // -----------------------------------------------------------------------
    // AppLogger: Simple file logging
    // -----------------------------------------------------------------------
    class AppLogger
    {
        string _logPath;
        object _lock = new object();

        public AppLogger(string logPath)
        {
            _logPath = logPath;
        }

        public void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // -----------------------------------------------------------------------
    // PersistedState: JSON state serialization
    // -----------------------------------------------------------------------
    class PersistedState
    {
        public string Mode { get; set; }
        public int FanSpeed0 { get; set; }
        public int FanSpeed1 { get; set; }
        public bool ManualOverride { get; set; }
        public bool GameDetectionEnabled { get; set; }

        string _statePath;

        public PersistedState(string statePath)
        {
            _statePath = statePath;
        }

        public PersistedState Load()
        {
            try
            {
                if (File.Exists(_statePath))
                {
                    string json = File.ReadAllText(_statePath);
                    var serializer = new JavaScriptSerializer();
                    return serializer.Deserialize<PersistedState>(json);
                }
            }
            catch { }
            return null;
        }

        public void Save()
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(this);
                File.WriteAllText(_statePath, json);
            }
            catch { }
        }
    }

    // -----------------------------------------------------------------------
    // FanCurveController: temperature-based auto fan curve
    // -----------------------------------------------------------------------
    class FanCurveController
    {
        // Quiet curve: <45C->2500, <55C->3000, <65C->4000, <75C->5000, >=75C->5300
        public ushort GetTargetRpm(float cpuTemp)
        {
            if (cpuTemp < 45) return 2500;
            if (cpuTemp < 55) return 3000;
            if (cpuTemp < 65) return 4000;
            if (cpuTemp < 75) return 5000;
            return 5300;
        }
    }

    // -----------------------------------------------------------------------
    // TrayContext: System tray icon + full application logic
    // -----------------------------------------------------------------------
    class TrayContext : ApplicationContext
    {
        // ---- Components ----
        NotifyIcon _trayIcon;
        ContextMenuStrip _menu;

        // Subsystems
        DeviceController _device;
        PowerPlanManager _powerMgr;
        TemperatureMonitor _tempMon;
        GameDetector _gameDetector;
        ProcessManager _processMgr;

        // ---- State ----
        PowerMode _currentMode = PowerMode.Balanced;
        ushort _fanSpeed0 = 0;
        ushort _fanSpeed1 = 0;
        bool _isManualOverride = false;
        bool _gameModeActive = false;
        bool _gameDetectionEnabled = true;

        // Run at Startup
        const string RUN_REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RUN_REG_VALUE = "RazerTray";
        ToolStripMenuItem _startupItem;

        // Cached temperatures
        float _cpuTemp = 0;
        float _gpuTemp = 0;
        DateTime _lastTempRead = DateTime.MinValue;

        // GPU monitoring (NVAPI)
        int _gpuClockMHz = 0;
        int _gpuMemMHz = 0;
        int _gpuUtilPercent = 0;

        // Icon color map
        Color _iconColor = Color.LimeGreen; // Balanced=green, Gaming=red, Creator=blue
        Bitmap _iconBitmap = null;
        Icon _currentIcon = null;

        // ---- Menu items (need member refs for enable/disable/check) ----
        ToolStripMenuItem _modeBalanced;
        ToolStripMenuItem _modeGaming;
        ToolStripMenuItem _modeCreator;
        ToolStripMenuItem _autoDetectItem;
        bool _ecFallback = false;

        // App dir (for log, state, config files)
        string _appDir;

        // Logger
        AppLogger _logger;

        // Persisted state
        PersistedState _state;

        // Auto fan curve
        FanCurveController _curveCtrl;
        Timer _fanCurveTimer;

        // ---- Constructor ----
        public TrayContext()
        {
            // Init subsystems
            _device = new DeviceController();
            _powerMgr = new PowerPlanManager();
            _tempMon = new TemperatureMonitor();

            _appDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);

            // Logger, state, curve
            _logger = new AppLogger(Path.Combine(_appDir, "RazerTray.log"));
            _logger.Log("Starting RazerTray v5");

            // Initialize NVAPI (gracefully degrades if unavailable)
            NvStatus nvStatus = NvApi.Initialize();
            if (nvStatus == NvStatus.OK)
                _logger.Log("NVAPI initialized");
            else
                _logger.Log("NVAPI not available: " + nvStatus);
            _state = new PersistedState(Path.Combine(_appDir, "RazerTray.state")).Load();
            if (_state != null) ApplyState();
            _curveCtrl = new FanCurveController();
            _gameDetector = new GameDetector(
                Path.Combine(_appDir, "game-modes.config"));

            _processMgr = new ProcessManager(_logger);
            _processMgr.SetUserRules(_gameDetector.GetGames());
            _processMgr.Start();

            // Try USB connect
            try { _device.Connect(); } catch { }

            // Read current power plan to set initial mode
            SyncModeFromPlan();

            // Build UI
            BuildMenu();
            UpdateIcon();
            UpdateTooltip();
            _trayIcon.Visible = true;

            // Double-click cycles modes
            _trayIcon.DoubleClick += (s, e) => CycleMode();

            // ---- Timers ----
            var refreshTimer = new Timer { Interval = 5000 };
            refreshTimer.Tick += (s, e) => RefreshTimerTick();
            refreshTimer.Start();

            var gameTimer = new Timer { Interval = 2000 };
            gameTimer.Tick += (s, e) => GameDetectionTick();
            gameTimer.Start();

            var tempTimer = new Timer { Interval = 10000 };
            tempTimer.Tick += (s, e) => TempTimerTick();
            tempTimer.Start();

            // Auto fan curve timer (5s)
            _fanCurveTimer = new Timer { Interval = 5000 };
            _fanCurveTimer.Tick += (s, e) => FanCurveTick();
            _fanCurveTimer.Start();

            // Sleep/Resume handler
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        // ---- Build context menu ----
        void BuildMenu()
        {
            _menu = new ContextMenuStrip();

            // --- Power Mode submenu (no Boost) ---
            var modeMenu = new ToolStripMenuItem("Power Mode");
            _modeBalanced = new ToolStripMenuItem("Balanced", null, (s, e) => SetMode(PowerMode.Balanced));
            _modeGaming = new ToolStripMenuItem("Gaming", null, (s, e) => SetMode(PowerMode.Gaming));
            _modeCreator = new ToolStripMenuItem("Creator", null, (s, e) => SetMode(PowerMode.Creator));
            modeMenu.DropDownItems.AddRange(new ToolStripItem[] {
                _modeBalanced, _modeGaming, _modeCreator
            });
            _menu.Items.Add(modeMenu);

            _menu.Items.Add(new ToolStripSeparator());

            // --- Auto-Detect (detection + process boost + trim) ---
            _autoDetectItem = new ToolStripMenuItem("Auto-Detect", null, (s, e) => ToggleAutoDetect());
            _autoDetectItem.Checked = _gameDetectionEnabled;
            _menu.Items.Add(_autoDetectItem);

            // --- Manage Games ---
            _menu.Items.Add(new ToolStripMenuItem("Manage Games...", null, (s, e) => ShowGameManager()));

            _menu.Items.Add(new ToolStripSeparator());

            // --- Run at startup ---
            _startupItem = new ToolStripMenuItem("Run at Startup", null, (s, e) => ToggleStartup());
            _startupItem.Checked = IsStartupEnabled();
            _menu.Items.Add(_startupItem);

            _menu.Items.Add(new ToolStripSeparator());

            // --- System submenu ---
            var sysMenu = new ToolStripMenuItem("System");
            sysMenu.DropDownItems.Add(new ToolStripMenuItem("Apply Tweaks", null, (s, e) => ApplySystemTweaks()));
            sysMenu.DropDownItems.Add(new ToolStripSeparator());
            sysMenu.DropDownItems.Add(new ToolStripMenuItem("Install Scheduled Task", null, (s, e) => InstallScheduledTask()));
            sysMenu.DropDownItems.Add(new ToolStripSeparator());
            sysMenu.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));
            _menu.Items.Add(sysMenu);

            _trayIcon = new NotifyIcon();
            _trayIcon.ContextMenuStrip = _menu;
        }

        // ---- Timer resolution API (winmm) ----
        [DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint uPeriod);
        bool _highResTimer = false;

        // Game boost: cache of suspended process IDs
        List<int> _suspendedPids = new List<int>();
        bool _gameBoostEnabled = false;

        // ---- NtSuspendProcess / NtResumeProcess (ntdll) ----
        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSuspendProcess(IntPtr hProcess);
        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtResumeProcess(IntPtr hProcess);

        // ---- EmptyWorkingSet (psapi) ----
        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EmptyWorkingSet(IntPtr hProcess);

        void SuspendProcessByName(string nameNoExt)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(nameNoExt))
                {
                    if (_suspendedPids.Contains(p.Id)) continue;
                    IntPtr h = NativeProcess.OpenProcess(
                        NativeProcess.PROCESS_SET_INFORMATION | NativeProcess.PROCESS_QUERY_LIMITED_INFORMATION,
                        false, (uint)p.Id);
                    if (h != IntPtr.Zero)
                    {
                        int ret = NtSuspendProcess(h);
                        if (ret == 0)
                        {
                            _suspendedPids.Add(p.Id);
                            _logger.Log("Suspended: " + p.ProcessName + " (PID " + p.Id + ")");
                        }
                        NativeProcess.CloseHandle(h);
                    }
                }
            }
            catch { }
        }

        void ResumeSuspendedProcesses()
        {
            try
            {
                foreach (int pid in _suspendedPids)
                {
                    try
                    {
                        Process p = Process.GetProcessById(pid);
                        IntPtr h = NativeProcess.OpenProcess(
                            NativeProcess.PROCESS_SET_INFORMATION | NativeProcess.PROCESS_QUERY_LIMITED_INFORMATION,
                            false, (uint)p.Id);
                        if (h != IntPtr.Zero)
                        {
                            NtResumeProcess(h);
                            NativeProcess.CloseHandle(h);
                        }
                    }
                    catch { }
                }
                _suspendedPids.Clear();
                _logger.Log("Resumed all suspended processes");
            }
            catch { }
        }

        void TrimMemory()
        {
            try
            {
                long before = GC.GetTotalMemory(false);
                // Only trim RazerTray's own working set (not all system processes!)
                using (var self = Process.GetCurrentProcess())
                {
                    IntPtr h = NativeProcess.OpenProcess(0x1F0FFF, false, (uint)self.Id);
                    if (h != IntPtr.Zero)
                    {
                        EmptyWorkingSet(h);
                        NativeProcess.CloseHandle(h);
                    }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long after = GC.GetTotalMemory(false);
                _logger.Log("Memory trimmed: " + (before - after) / 1024 + " KB freed");
            }
            catch { }
        }

        void SetTimerResolution(bool highRes)
        {
            if (highRes == _highResTimer) return;
            if (highRes) timeBeginPeriod(1);
            else timeEndPeriod(1);
            _highResTimer = highRes;
            _logger.Log("TimerResolution: " + (highRes ? "1ms" : "default"));
        }

        // ---- Icon ----
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        void UpdateIcon()
        {
            Color color = _iconColor;
            if (!_device.IsConnected) color = Color.Gray;

            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Brush brush = new SolidBrush(color))
                {
                    // Lightning bolt "Z" shape
                    PointF[] bolt = {
                        new PointF(10f, 1f), new PointF(1f, 10f), new PointF(8f, 10f),
                        new PointF(6f, 15f), new PointF(15f, 5f), new PointF(9f, 5f)
                    };
                    g.FillPolygon(brush, bolt);
                }
            }

            // Swap icon (keep bitmap alive for HICON)
            if (_currentIcon != null) _currentIcon.Dispose();
            if (_iconBitmap != null) _iconBitmap.Dispose();
            _iconBitmap = bmp;
            _currentIcon = Icon.FromHandle(bmp.GetHicon());
            if (_trayIcon != null)
                _trayIcon.Icon = _currentIcon;
        }

        // ---- Tooltip ----
        void UpdateTooltip()
        {
            if (_trayIcon == null) return;

            // NotifyIcon.Text is limited to 63 chars on Windows
            StringBuilder sb = new StringBuilder();
            sb.Append(_currentMode.ToString());
            if (_ecFallback) sb.Append("(EC)");
            sb.Append(' ');

            if (_device.IsConnected)
            {
                sb.AppendFormat("F1:{0}F2:{1} ", _fanSpeed0, _fanSpeed1);
            }
            else
            {
                sb.Append("USB:Disc ");
            }

            if (_cpuTemp > 0)
                sb.AppendFormat("CPU:{0:F0}C ", _cpuTemp);
            if (_gpuTemp > 0)
                sb.AppendFormat("GPU:{0:F0}C ", _gpuTemp);
            if (_gpuClockMHz > 0)
                sb.AppendFormat("{0}MHz", _gpuClockMHz);
            if (_gpuUtilPercent > 0)
                sb.AppendFormat(" {0}%", _gpuUtilPercent);

            if (_gameModeActive)
                sb.Append(" *Game*");

            string text = sb.ToString().TrimEnd();
            if (text.Length > 63)
                text = text.Substring(0, 60) + "...";
            _trayIcon.Text = text;
        }

        // ---- Mode sync from Windows power plan ----
        void SyncModeFromPlan()
        {
            try
            {
                Guid active = _powerMgr.CurrentPlanGuid();
                if (active == PowerPlanManager.GUID_HIGH_PERFORMANCE)
                    _currentMode = PowerMode.Gaming;
                else if (active == PowerPlanManager.GUID_POWER_SAVER)
                    _currentMode = PowerMode.Balanced;
                else
                    _currentMode = PowerMode.Balanced;
            }
            catch { }
        }

        // ---- Apply mode (USB + power plan + fan) ----
        void ApplyMode(PowerMode mode, ushort? fanRpm = null)
        {
            _currentMode = mode;

            // Update icon color
            switch (mode)
            {
                case PowerMode.Gaming: _iconColor = Color.Red; break;
                case PowerMode.Creator: _iconColor = Color.DodgerBlue; break;
                default: _iconColor = Color.LimeGreen; break;
            }

            // High-res timer for Gaming/Audio modes
            SetTimerResolution(mode == PowerMode.Gaming || mode == PowerMode.Creator);

            // Set Windows power plan
            Guid planGuid = _powerMgr.ModeToPlanGuid(mode);
            _powerMgr.SetActivePlan(planGuid);

            // Set USB mode + fan
            if (_device.IsConnected)
            {
                if (fanRpm.HasValue && fanRpm.Value > 0)
                {
                    _device.SetModeWithFan(mode, autoFan: false, fanRpm: fanRpm.Value);
                }
                else if (_ecFallback)
                {
                    // Shift+click fallback: EC controls the fan
                    _device.SetPowerMode(mode, autoFan: true);
                }
                else
                {
                    // Smart curve with per-mode offset
                    _device.SetPowerMode(mode, autoFan: false);
                    ushort target = _curveCtrl.GetTargetRpm(_cpuTemp);
                    int offset = mode == PowerMode.Gaming ? 500 :
                                 mode == PowerMode.Creator ? 300 : 0;
                    target = (ushort)Math.Min(target + offset, (ushort)5300);
                    _device.SetFanSpeed(0, target);
                    _device.SetFanSpeed(1, target);
                    _fanSpeed0 = target;
                    _fanSpeed1 = target;
                }
            }

            // Apply NVIDIA global profile setting
            if (NvApi.IsAvailable)
            {
                NvPowerMode gpuMode;
                switch (mode)
                {
                    case PowerMode.Gaming:
                    case PowerMode.Creator:
                        gpuMode = NvPowerMode.PreferMax;
                        break;
                    default:
                        gpuMode = NvPowerMode.OptimalPower;
                        break;
                }
                NvApi.SetGlobalPowerMode(gpuMode);
            }

            // Apply power-saving subparameter overrides on the active plan
            if (mode == PowerMode.Balanced)
                PowerPlanManager.ApplyPowerSaver();
            else
                PowerPlanManager.RemovePowerSaver();

            // Update UI state
            _modeBalanced.Checked = (mode == PowerMode.Balanced);
            _modeGaming.Checked = (mode == PowerMode.Gaming);
            _modeCreator.Checked = (mode == PowerMode.Creator);

            UpdateIcon();
            UpdateTooltip();

            _trayIcon.ShowBalloonTip(2000, "Razer Blade Tray",
                "Mode: " + mode.ToString(), ToolTipIcon.Info);
        }

        // ---- Cycle mode: Balanced -> Gaming -> Creator -> Balanced ----
        void CycleMode()
        {
            PowerMode next;
            switch (_currentMode)
            {
                case PowerMode.Balanced: next = PowerMode.Gaming; break;
                case PowerMode.Gaming: next = PowerMode.Creator; break;
                default: next = PowerMode.Balanced; break;
            }
            _isManualOverride = true;
            _ecFallback = (Control.ModifierKeys & Keys.Shift) != 0;
            ApplyMode(next);
        }

        // ---- Set mode via menu ----
        void SetMode(PowerMode mode)
        {
            _isManualOverride = true;
            _ecFallback = (Control.ModifierKeys & Keys.Shift) != 0;
            ApplyMode(mode);
        }

        // ---- Toggle Auto-Detect (detection + process boost + trim) ----
        void ToggleAutoDetect()
        {
            _gameDetectionEnabled = !_gameDetectionEnabled;
            _autoDetectItem.Checked = _gameDetectionEnabled;
            _gameBoostEnabled = _gameDetectionEnabled; // process boost follows detection
            if (!_gameDetectionEnabled)
            {
                _gameModeActive = false;
                ResumeSuspendedProcesses();
            }
            _logger.Log("AutoDetect: " + (_gameDetectionEnabled ? "ON" : "OFF"));
            UpdateTooltip();
        }

        // ---- Run at Startup (HKCU Run key) ----
        bool IsStartupEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RUN_REG_KEY, false))
                {
                    if (key == null) return false;
                    var val = key.GetValue(RUN_REG_VALUE) as string;
                    return val != null && val.IndexOf("RazerTray", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        void SetStartupEnabled(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RUN_REG_KEY, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue(RUN_REG_VALUE, exePath);
                    }
                    else
                    {
                        if (key.GetValue(RUN_REG_VALUE) != null)
                            key.DeleteValue(RUN_REG_VALUE);
                    }
                }
                _logger.Log("Startup: " + (enable ? "ON" : "OFF"));
            }
            catch (Exception ex)
            {
                _logger.Log("Startup toggle failed: " + ex.Message);
            }
        }

        void ToggleStartup()
        {
            bool enabled = !IsStartupEnabled();
            SetStartupEnabled(enabled);
            _startupItem.Checked = enabled;
        }

        // ---- Install as scheduled task (admin-elevated logon) ----
        void InstallScheduledTask()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string taskName = "RazerTray";
                string cmd = string.Format("schtasks /create /tn \"{0}\" /tr \"\\\"{1}\\\"\" /sc onlogon /rl highest /f",
                    taskName, exePath);
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    UseShellExecute = true,
                    Verb = "runas", // prompt UAC for admin
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0)
                    {
                        _logger.Log("Scheduled task installed: " + taskName);
                        _trayIcon.ShowBalloonTip(3000, "RazerTray",
                            "Scheduled task installed (runs at logon with highest privileges)",
                            ToolTipIcon.Info);
                    }
                    else
                    {
                        _trayIcon.ShowBalloonTip(3000, "RazerTray",
                            "Failed to install scheduled task (exit code " + p.ExitCode + ")",
                            ToolTipIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log("InstallScheduledTask failed: " + ex.Message);
                _trayIcon.ShowBalloonTip(3000, "RazerTray",
                    "Failed to install scheduled task: " + ex.Message,
                    ToolTipIcon.Error);
            }
        }

        // ---- Reload config ----
        void ReloadConfig()
        {
            _gameDetector.Reload();
            _trayIcon.ShowBalloonTip(1500, "Razer Blade Tray",
                "Config reloaded: " + _gameDetector.GetGames().Count + " games",
                ToolTipIcon.Info);
        }

        // ---- Exit ----
        void ExitApp()
        {
            SaveState();
            _trayIcon.Visible = false;
            if (_device.IsConnected)
            {
                // Restore auto fan on exit
                _device.SetPowerMode(_currentMode, autoFan: true);
            }
            _device.Dispose();
            Application.Exit();
        }

        // ================================================================
        // v5: State persistence, auto fan curve, sleep/resume
        // ================================================================

        // ---- Apply persisted state after loading ----
        void ApplyState()
        {
            try
            {
                if (_state == null) return;
                if (!string.IsNullOrEmpty(_state.Mode))
                {
                    PowerMode mode;
                    if (Enum.TryParse<PowerMode>(_state.Mode, out mode))
                    {
                        _currentMode = mode;
                    }
                }
                if (_state.FanSpeed0 > 0) _fanSpeed0 = (ushort)_state.FanSpeed0;
                if (_state.FanSpeed1 > 0) _fanSpeed1 = (ushort)_state.FanSpeed1;
                _isManualOverride = _state.ManualOverride;
                _gameDetectionEnabled = _state.GameDetectionEnabled;
            }
            catch { }
        }

        // ---- Save current state to file ----
        void SaveState()
        {
            try
            {
                if (_state == null)
                    _state = new PersistedState(Path.Combine(_appDir, "RazerTray.state"));
                _state.Mode = _currentMode.ToString();
                _state.FanSpeed0 = _fanSpeed0;
                _state.FanSpeed1 = _fanSpeed1;
                _state.ManualOverride = _isManualOverride;
                _state.GameDetectionEnabled = _gameDetectionEnabled;
                _state.Save();
            }
            catch { }
        }

        // ---- Show game manager dialog ----
        void ShowGameManager()
        {
            string configPath = Path.Combine(_appDir, "game-modes.config");
            var form = new GameManagerForm(configPath, () => {
                _gameDetector.Reload();
                _logger.Log("Config reloaded: " + _gameDetector.GetGames().Count + " games");
            });
            form.ShowDialog();
        }

        // ---- One-shot system registry tweaks ----
        void ApplySystemTweaks()
        {
            try
            {
                int ok = 0, fail = 0;

                // 1. MMCSS Audio: raise scheduling to High
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Audio", true))
                    {
                        if (k != null)
                        {
                            k.SetValue("Scheduling Category", "High", RegistryValueKind.String);
                            k.SetValue("SFIO Priority", "High", RegistryValueKind.String);
                            k.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 2. MMCSS Games: add Latency Sensitive
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", true))
                    {
                        if (k != null)
                        {
                            k.SetValue("Latency Sensitive", "True", RegistryValueKind.String);
                            k.SetValue("SFIO Priority", "High", RegistryValueKind.String);
                            k.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 3. SystemResponsiveness = 0 (reduce background throttling)
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true))
                    {
                        if (k != null)
                        {
                            k.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 4. Win32PrioritySeparation: short quantums, variable, foreground boost
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\PriorityControl", true))
                    {
                        if (k != null)
                        {
                            k.SetValue("Win32PrioritySeparation", 38, RegistryValueKind.DWord);
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 5. Network tweaks: Nagle's algorithm per interface
                try
                {
                    string tcpipPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
                    using (var ifaces = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(tcpipPath))
                    {
                        if (ifaces != null)
                        {
                            foreach (var guid in ifaces.GetSubKeyNames())
                            {
                                using (var iface = ifaces.OpenSubKey(guid, true))
                                {
                                    if (iface != null)
                                    {
                                        iface.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                                        iface.SetValue("TcpNoDelay", 1, RegistryValueKind.DWord);
                                    }
                                }
                            }
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 6. Disable NetBIOS over TCP/IP per adapter
                try
                {
                    string netbtPath = @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces";
                    using (var ifaces = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(netbtPath))
                    {
                        if (ifaces != null)
                        {
                            foreach (var guid in ifaces.GetSubKeyNames())
                            {
                                using (var iface = ifaces.OpenSubKey(guid, true))
                                {
                                    if (iface != null)
                                        iface.SetValue("NetbiosOptions", 2, RegistryValueKind.DWord);
                                }
                            }
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 7. Disable LLMNR multicast
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                        @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient"))
                    {
                        k.SetValue("EnableMulticast", 0, RegistryValueKind.DWord);
                        ok++;
                    }
                }
                catch { fail++; }

                // 8. Disable Xbox Game Monitoring service
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\xbgm", true))
                    {
                        if (k != null)
                        {
                            k.SetValue("Start", 4, RegistryValueKind.DWord);
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 9. Disable mouse acceleration
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Control Panel\Mouse", true))
                    {
                        if (k != null)
                        {
                            k.SetValue("MouseSpeed", "0", RegistryValueKind.String);
                            k.SetValue("MouseThreshold1", "0", RegistryValueKind.String);
                            k.SetValue("MouseThreshold2", "0", RegistryValueKind.String);
                            ok++;
                        }
                    }
                }
                catch { fail++; }

                // 10. Visual effects: adjust for best performance
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", true))
                    {
                        if (k != null)
                        {
                            k.SetValue("VisualFXSetting", 2, RegistryValueKind.DWord);
                            ok++;
                        }
                    }
                }
                catch { fail++; }

// 11. GPU Preemption tweaks (reduce GPU latency)
                 try
                 {
                     using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                         @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Power"))
                     {
                         k.SetValue("ComputePreemption", 0, RegistryValueKind.DWord);
                         k.SetValue("DisableCudaContextPreemption", 1, RegistryValueKind.DWord);
                         k.SetValue("EnableAsyncMidBufferPreemption", 0, RegistryValueKind.DWord);
                         k.SetValue("EnableCEPreemption", 0, RegistryValueKind.DWord);
                         k.SetValue("EnableMidBufferPreemption", 0, RegistryValueKind.DWord);
                         ok++;
                     }
                 }
                 catch { fail++; }
 
                 // 12. Disable CPU Power Throttling
                 try
                 {
                     using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                         @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"))
                     {
                         if (k != null)
                         {
                             k.SetValue("PowerThrottlingOff", 1, RegistryValueKind.DWord);
                             ok++;
                         }
                     }
                 }
                 catch { fail++; }

                string msg = ok + " tweaks applied";
                if (fail > 0) msg += ", " + fail + " failed (need admin?)";
                _logger.Log("SystemTweaks: " + msg);
                _trayIcon.ShowBalloonTip(3000, "System Tweaks", msg, ToolTipIcon.Info);
            }
            catch { }
        }

        // ---- Auto fan curve tick ----
        void FanCurveTick()
        {
            try
            {
                if (_ecFallback) return; // EC in control
                if (!_device.IsConnected) return;
                if (_cpuTemp <= 0) return; // no temp data yet

                ushort target = _curveCtrl.GetTargetRpm(_cpuTemp);

                // Per-mode offset: Gaming=aggressive, Creator=moderate, Balanced=none
                int offset = _currentMode == PowerMode.Gaming ? 500 :
                             _currentMode == PowerMode.Creator ? 300 : 0;
                target = (ushort)Math.Min(target + offset, (ushort)5300);

                // Only set if different from current
                if (_fanSpeed0 != target || _fanSpeed1 != target)
                {
                    _device.SetFanSpeed(0, target);
                    _device.SetFanSpeed(1, target);
                    _fanSpeed0 = target;
                    _fanSpeed1 = target;
                    _logger.Log("FanCurve: " + _currentMode + " CPU=" + _cpuTemp.ToString("F0") + "C -> " + target + " RPM");
                }
            }
            catch { }
        }

        // ---- Sleep / Resume handler ----
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.Resume)
                {
                    _logger.Log("System resume");
                    // Reconnect USB
                    _device.Disconnect();
                    _device.Connect();
                    // Reapply current mode
                    if (_device.IsConnected)
                    {
                        _device.SetPowerMode(_currentMode, autoFan: true);
                        _logger.Log("Resume: reapplied mode " + _currentMode);
                    }
                }
            }
            catch { }
        }



        // ================================================================
        // Timers
        // ================================================================

        // ---- Refresh timer (5s): poll USB state ----
        void RefreshTimerTick()
        {
            try
            {
                if (_device.IsConnected)
                {
                    // Query current mode from EC
                    var mode = _device.QueryPowerMode();
                    if (mode.HasValue && mode.Value <= PowerMode.Creator)
                    {
                        _currentMode = mode.Value;
                    }

                    // Query fan speeds
                    _fanSpeed0 = _device.QueryFanSpeed(0);
                    _fanSpeed1 = _device.QueryFanSpeed(1);
                }

                // Sync menu checked states
                _modeBalanced.Checked = (_currentMode == PowerMode.Balanced);
                _modeGaming.Checked = (_currentMode == PowerMode.Gaming);
                _modeCreator.Checked = (_currentMode == PowerMode.Creator);

                UpdateTooltip();
            }
            catch { }
        }

        // ---- Game detection timer (2s) ----
        void GameDetectionTick()
        {
            try
            {
                if (!_gameDetectionEnabled) return;

                _gameDetector.CheckReload();
                _processMgr.SetUserRules(_gameDetector.GetGames());
                var game = _gameDetector.DetectForegroundGame();

                if (game != null)
                {
                    // Game detected - apply its mode
                    if (!_gameModeActive)
                    {
                        _gameModeActive = true;
                        _isManualOverride = false;

                        // Game Boost: suspend background processes + trim memory
                        if (_gameBoostEnabled)
                        {
                            try
                            {
                                SuspendProcessByName("explorer");
                                SuspendProcessByName("chrome");
                                SuspendProcessByName("firefox");
                                SuspendProcessByName("msedge");
                                SuspendProcessByName("brave");
                                SuspendProcessByName("opera");
                                TrimMemory();
                            }
                            catch { }
                        }

                        PowerMode targetMode;
                        if (game.mode != null && game.mode.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0)
                            targetMode = PowerMode.Creator;
                        else if (game.mode != null && game.mode.IndexOf("Game", StringComparison.OrdinalIgnoreCase) >= 0)
                            targetMode = PowerMode.Gaming;
                        else
                            targetMode = PowerMode.Gaming;

                        ushort? fanRpm = game.fanSpeed > 0 ? (ushort?)game.fanSpeed : null;

                        ApplyMode(targetMode, fanRpm);

                        // Apply per-game NVIDIA GPU profile if configured
                        if (game.gpuProfile != null && NvApi.IsAvailable)
                        {
                            try
                            {
                                if (game.gpuProfile.gpuPowerMode.HasValue)
                                {
                                    // Try to find existing game profile by exe name
                                    string profileName = Path.GetFileNameWithoutExtension(game.exe);
                                    var result = NvApi.SetGameProfilePowerMode(profileName,
                                        (NvPowerMode)game.gpuProfile.gpuPowerMode.Value);
                                    if (result == NvStatus.OK)
                                        _logger.Log("NVAPI: set power mode on profile " + profileName);
                                }
                            }
                            catch { }
                        }

                        _trayIcon.ShowBalloonTip(1500, "Game Mode",
                            "Auto-switched to " + targetMode + " for " + game.name,
                            ToolTipIcon.Info);
                    }
                }
                else
                {
                    // No game focused
                    if (_gameModeActive && !_isManualOverride)
                    {
                        _gameModeActive = false;

                        // Resume background processes if game boost was active
                        if (_gameBoostEnabled)
                            ResumeSuspendedProcesses();

                        // Revert to balanced
                        ApplyMode(PowerMode.Balanced);
                        _trayIcon.ShowBalloonTip(1500, "Game Mode",
                            "Reverted to Balanced", ToolTipIcon.Info);
                    }
                }
            }
            catch { }
        }

        // ---- Temperature timer (10s) ----
        void TempTimerTick()
        {
            try
            {
                _cpuTemp = _tempMon.GetCpuTemperature();
                _gpuTemp = _tempMon.GetGpuTemperature();
                _lastTempRead = DateTime.Now;

                // Refresh GPU clocks + utilization via NVAPI
                if (NvApi.IsAvailable)
                {
                    int c, m, u;
                    if (NvApi.GetGpuClocks(out c, out m)) { _gpuClockMHz = c; _gpuMemMHz = m; }
                    if (NvApi.GetGpuUtilization(out u)) _gpuUtilPercent = u;
                }

                FanCurveTick();
                UpdateTooltip();
            }
            catch { }
        }

        // ---- Cleanup ----
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                if (_fanCurveTimer != null) { _fanCurveTimer.Dispose(); _fanCurveTimer = null; }
                if (_currentIcon != null) { _currentIcon.Dispose(); _currentIcon = null; }
                if (_iconBitmap != null) { _iconBitmap.Dispose(); _iconBitmap = null; }
                if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
                if (_device != null) { _device.Dispose(); _device = null; }
                if (_processMgr != null) { _processMgr.Dispose(); _processMgr = null; }
                NvApi.Shutdown();
            }
            base.Dispose(disposing);
        }
    }

    // -----------------------------------------------------------------------
    // GameManagerForm: visual game list with add/remove
    // -----------------------------------------------------------------------
    class GameManagerForm : Form
    {
        ListView _list;
        string _configPath;
        Action _onChanged;

        public GameManagerForm(string configPath, Action onChanged)
        {
            _configPath = configPath;
            _onChanged = onChanged;
            BuildUI();
            LoadGames();
        }

        void BuildUI()
        {
            Text = "Game Mode Manager";
            ClientSize = new Size(500, 350);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.Columns.Add("Game", 180);
            _list.Columns.Add("Executable", 160);
            _list.Columns.Add("Mode", 70);
            _list.Columns.Add("Fan", 60);

            var panel = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8) };
            var addBtn = new Button { Text = "Add Game...", Width = 110, Height = 28 };
            addBtn.Click += (s, e) => AddGame();
            var removeBtn = new Button { Text = "Remove", Width = 80, Height = 28, Left = 120 };
            removeBtn.Click += (s, e) => RemoveGame();
            var closeBtn = new Button { Text = "Close", Width = 80, Height = 28, Left = panel.Width - 100, Anchor = AnchorStyles.Right };
            closeBtn.Click += (s, e) => Close();
            panel.Controls.Add(addBtn);
            panel.Controls.Add(removeBtn);
            panel.Controls.Add(closeBtn);

            Controls.Add(_list);
            Controls.Add(panel);
        }

        void LoadGames()
        {
            _list.Items.Clear();
            var cfg = ConfigFile.Load(_configPath);
            foreach (var g in cfg.Games)
            {
                var item = new ListViewItem(g.name);
                item.SubItems.Add(g.exe);
                item.SubItems.Add(g.mode == "Game Mode" ? "Gaming" : "Audio");
                item.SubItems.Add(g.fanSpeed > 0 ? g.fanSpeed.ToString() : "Auto");
                _list.Items.Add(item);
            }
        }

        void AddGame()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select Game Executable";
                dlg.Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                string exePath = dlg.FileName;
                string exeName = Path.GetFileName(exePath);
                string gameName = Path.GetFileNameWithoutExtension(exePath);

                // Show mode picker
                string[] modes = { "Game Mode", "Audio Mode" };
                var modeForm = new Form();
                modeForm.Text = "Add Game";
                modeForm.ClientSize = new Size(300, 160);
                modeForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                modeForm.StartPosition = FormStartPosition.CenterParent;
                modeForm.MinimizeBox = false;
                modeForm.MaximizeBox = false;

                var nameLabel = new Label { Text = "Name:", Left = 12, Top = 12, Width = 60 };
                var nameBox = new TextBox { Text = gameName, Left = 80, Top = 10, Width = 200 };
                var modeLabel = new Label { Text = "Mode:", Left = 12, Top = 42, Width = 60 };
                var modeBox = new ComboBox { Left = 80, Top = 40, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                modeBox.Items.AddRange(modes);
                modeBox.SelectedIndex = 0;
                var okBtn = new Button { Text = "Add", Left = 130, Top = 100, Width = 70, DialogResult = DialogResult.OK };
                var cancelBtn = new Button { Text = "Cancel", Left = 210, Top = 100, Width = 70, DialogResult = DialogResult.Cancel };

                modeForm.Controls.AddRange(new Control[] { nameLabel, nameBox, modeLabel, modeBox, okBtn, cancelBtn });
                modeForm.AcceptButton = okBtn;
                modeForm.CancelButton = cancelBtn;

                if (modeForm.ShowDialog() != DialogResult.OK) return;

                gameName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(gameName)) gameName = Path.GetFileNameWithoutExtension(exePath);

                var cfg = ConfigFile.Load(_configPath);
                cfg.Games.Add(new GameConfig
                {
                    name = gameName,
                    exe = exeName,
                    mode = modeBox.SelectedItem.ToString(),
                    fanSpeed = 0
                });
                SaveConfig(cfg);
                LoadGames();
            }
        }

        void RemoveGame()
        {
            if (_list.SelectedItems.Count == 0) return;
            var item = _list.SelectedItems[0];
            string exe = item.SubItems[1].Text;

            var cfg = ConfigFile.Load(_configPath);
            cfg.Games.RemoveAll(g => g.exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
            SaveConfig(cfg);
            LoadGames();
        }

        void SaveConfig(ConfigFile cfg)
        {
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(cfg.Games);
            File.WriteAllText(_configPath, json);
            if (_onChanged != null) _onChanged();
        }
    }
}
