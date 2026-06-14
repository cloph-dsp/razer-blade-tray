// RazerTray.cs - Razer Blade Tray Controller v5
// System tray icon - switches performance modes (Balanced/Gaming/Creator)
// + Windows power plan + dynamic icon + temps + game detection
// + file logging + state persistence + sleep/resume + auto fan curve
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
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

        // ---- Boost (software tracking only - 0x070f not supported on this model) ----
        bool _boostEnabled = false;

        public bool SetBoost(bool enabled)
        {
            _boostEnabled = enabled;
            return true;
        }

        public bool QueryBoost()
        {
            return _boostEnabled;
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
                case PowerMode.Gaming: return GUID_HIGH_PERFORMANCE;
                case PowerMode.Creator: return GUID_BALANCED; // Creator uses Balanced plan
                default: return GUID_BALANCED;
            }
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
    // GameDetector: reads game-modes.config, detects running games
    // -----------------------------------------------------------------------
    class GameConfig
    {
        public string name { get; set; }
        public string exe { get; set; }
        public string mode { get; set; }   // "Game Mode" or "Audio Mode"
        public int fanSpeed { get; set; }
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
        public bool BoostEnabled { get; set; }
        public bool AutoFanCurve { get; set; }
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
        // Curve: <45C->3500, <55C->4000, <65C->4500, <75C->5000, >=75C->5300
        public ushort GetTargetRpm(float cpuTemp)
        {
            if (cpuTemp < 45) return 3500;
            if (cpuTemp < 55) return 4000;
            if (cpuTemp < 65) return 4500;
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

        // ---- State ----
        PowerMode _currentMode = PowerMode.Balanced;
        ushort _fanSpeed0 = 0;
        ushort _fanSpeed1 = 0;
        bool _boostEnabled = false;
        bool _isManualOverride = false;
        bool _gameModeActive = false;
        bool _gameDetectionEnabled = true;

        // RPM history (max 8 per fan)
        Dictionary<int, Queue<ushort>> _fanHistory;
        const int MaxFanHistory = 8;

        // Cached temperatures
        float _cpuTemp = 0;
        float _gpuTemp = 0;
        DateTime _lastTempRead = DateTime.MinValue;

        // Icon color map
        Color _iconColor = Color.LimeGreen; // Balanced=green, Gaming=red, Creator=blue
        Bitmap _iconBitmap = null;
        Icon _currentIcon = null;

        // ---- Menu items (need member refs for enable/disable/check) ----
        ToolStripMenuItem _modeBalanced;
        ToolStripMenuItem _modeGaming;
        ToolStripMenuItem _modeCreator;
        ToolStripMenuItem _fan3500;
        ToolStripMenuItem _fan4000;
        ToolStripMenuItem _fan4500;
        ToolStripMenuItem _fan5000;
        ToolStripMenuItem _fanAuto;
        ToolStripMenuItem _fanCustom;
        ToolStripMenuItem _boostItem;
        ToolStripMenuItem _gameDetectionItem;
        ToolStripMenuItem _fanAutoCurveItem;

        // App dir (for log, state, config files)
        string _appDir;

        // Logger
        AppLogger _logger;

        // Persisted state
        PersistedState _state;

        // Auto fan curve
        FanCurveController _curveCtrl;
        bool _autoFanCurveEnabled = false;
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
            _state = new PersistedState(Path.Combine(_appDir, "RazerTray.state")).Load();
            if (_state != null) ApplyState();
            _curveCtrl = new FanCurveController();
            _gameDetector = new GameDetector(
                Path.Combine(_appDir, "game-modes.config"));

            _fanHistory = new Dictionary<int, Queue<ushort>>();
            _fanHistory[0] = new Queue<ushort>(MaxFanHistory);
            _fanHistory[1] = new Queue<ushort>(MaxFanHistory);

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

            // --- Performance Mode submenu ---
            var modeMenu = new ToolStripMenuItem("Performance Mode");
            _modeBalanced = new ToolStripMenuItem("Balanced", null, (s, e) => SetMode(PowerMode.Balanced));
            _modeGaming = new ToolStripMenuItem("Gaming", null, (s, e) => SetMode(PowerMode.Gaming));
            _modeCreator = new ToolStripMenuItem("Creator", null, (s, e) => SetMode(PowerMode.Creator));
            modeMenu.DropDownItems.AddRange(new ToolStripItem[] {
                _modeBalanced, _modeGaming, _modeCreator
            });
            _menu.Items.Add(modeMenu);

            // --- Fan Speed submenu ---
            var fanMenu = new ToolStripMenuItem("Fan Speed");
            _fan3500 = new ToolStripMenuItem("3500 RPM", null, (s, e) => SetFanPreset(3500));
            _fan4000 = new ToolStripMenuItem("4000 RPM", null, (s, e) => SetFanPreset(4000));
            _fan4500 = new ToolStripMenuItem("4500 RPM", null, (s, e) => SetFanPreset(4500));
            _fan5000 = new ToolStripMenuItem("5000 RPM", null, (s, e) => SetFanPreset(5000));
            _fanAuto = new ToolStripMenuItem("Auto (EC)", null, (s, e) => SetFanPreset(0));
            _fanCustom = new ToolStripMenuItem("Custom...", null, (s, e) => PromptCustomFan());
            fanMenu.DropDownItems.AddRange(new ToolStripItem[] {
                _fan3500, _fan4000, _fan4500, _fan5000, _fanAuto, _fanCustom
            });
            _menu.Items.Add(fanMenu);

            // --- Boost toggle ---
            _boostItem = new ToolStripMenuItem("Boost", null, (s, e) => ToggleBoost());
            _menu.Items.Add(_boostItem);

            _menu.Items.Add(new ToolStripSeparator());

            // --- Game Detection toggle ---
            _gameDetectionItem = new ToolStripMenuItem("Game Detection", null, (s, e) => ToggleGameDetection());
            _gameDetectionItem.Checked = _gameDetectionEnabled;
            _menu.Items.Add(_gameDetectionItem);

            // --- Auto Fan Curve toggle ---
            _fanAutoCurveItem = new ToolStripMenuItem("Auto Fan Curve", null, (s, e) => ToggleAutoFanCurve());
            _menu.Items.Add(_fanAutoCurveItem);

            // --- Open Config ---
            _menu.Items.Add(new ToolStripMenuItem("Open Config...", null, (s, e) => OpenConfigFile()));

            // --- Reload Config ---
            _menu.Items.Add(new ToolStripMenuItem("Reload Config", null, (s, e) => ReloadConfig()));

            _menu.Items.Add(new ToolStripSeparator());

            // --- Exit ---
            _menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));

            _trayIcon = new NotifyIcon();
            _trayIcon.ContextMenuStrip = _menu;
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
            if (_boostEnabled) sb.Append("+Boost");
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
                sb.AppendFormat("GPU:{0:F0}C", _gpuTemp);

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
                else
                {
                    _device.SetPowerMode(mode, autoFan: true);
                }
            }

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
            ApplyMode(next);
        }

        // ---- Set mode via menu ----
        void SetMode(PowerMode mode)
        {
            _isManualOverride = true;
            ApplyMode(mode);
        }

        // ---- Set fan preset ----
        void SetFanPreset(ushort rpm)
        {
            if (!_device.IsConnected) return;

            // Manual fan set disables auto fan curve
            _autoFanCurveEnabled = false;

            if (rpm == 0)
            {
                // Auto (EC control)
                _device.SetPowerMode(_currentMode, autoFan: true);
                _fanSpeed0 = 0;
                _fanSpeed1 = 0;
            }
            else
            {
                _device.SetFanSpeed(0, rpm);
                _device.SetFanSpeed(1, rpm);
                _fanSpeed0 = rpm;
                _fanSpeed1 = rpm;

                // Track history
                foreach (int fan in new[] { 0, 1 })
                {
                    var q = _fanHistory[fan];
                    q.Enqueue(rpm);
                    if (q.Count > MaxFanHistory) q.Dequeue();
                }
            }

            _isManualOverride = true;
            UpdateTooltip();
        }

        // ---- Toggle boost ----
        void ToggleBoost()
        {
            _boostEnabled = !_boostEnabled;
            _device.SetBoost(_boostEnabled);
            _boostItem.Checked = _boostEnabled;
            UpdateTooltip();
        }

        // ---- Toggle game detection ----
        void ToggleGameDetection()
        {
            _gameDetectionEnabled = !_gameDetectionEnabled;
            _gameDetectionItem.Checked = _gameDetectionEnabled;
            if (!_gameDetectionEnabled)
            {
                _gameModeActive = false;
                // Don't auto-revert; user switched it off manually
            }
            UpdateTooltip();
        }

        // ---- Reload config ----
        void ReloadConfig()
        {
            _gameDetector.Reload();
            _trayIcon.ShowBalloonTip(1500, "Razer Blade Tray",
                "Config reloaded: " + _gameDetector.GetGames().Count + " games",
                ToolTipIcon.Info);
        }

        // ---- Custom fan speed prompt ----
        void PromptCustomFan()
        {
            string input = InputBox("Enter fan speed (3100-5300 RPM):", "Custom Fan Speed", "4000");
            ushort rpm;
            if (ushort.TryParse(input, out rpm))
            {
                rpm = Math.Max((ushort)3100, Math.Min((ushort)5300, rpm));
                SetFanPreset(rpm);
            }
        }

        // ---- Simple InputBox ----
        static string InputBox(string prompt, string title, string defaultValue)
        {
            var form = new Form();
            form.Text = title;
            form.ClientSize = new Size(320, 120);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;

            var label = new Label { Text = prompt, Left = 12, Top = 12, Width = 290 };
            var textBox = new TextBox { Left = 12, Top = 36, Width = 290, Text = defaultValue };
            var okBtn = new Button { Text = "OK", Left = 150, Top = 72, Width = 70, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "Cancel", Left = 230, Top = 72, Width = 70, DialogResult = DialogResult.Cancel };

            form.Controls.AddRange(new Control[] { label, textBox, okBtn, cancelBtn });
            form.AcceptButton = okBtn;
            form.CancelButton = cancelBtn;

            return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
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
                _boostEnabled = _state.BoostEnabled;
                _autoFanCurveEnabled = _state.AutoFanCurve;
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
                _state.BoostEnabled = _boostEnabled;
                _state.AutoFanCurve = _autoFanCurveEnabled;
                _state.ManualOverride = _isManualOverride;
                _state.GameDetectionEnabled = _gameDetectionEnabled;
                _state.Save();
            }
            catch { }
        }

        // ---- Open config file in Notepad ----
        void OpenConfigFile()
        {
            try
            {
                string configPath = Path.Combine(_appDir, "game-modes.config");
                Process.Start("notepad.exe", configPath);
            }
            catch { }
        }

        // ---- Auto fan curve tick ----
        void FanCurveTick()
        {
            try
            {
                if (!_autoFanCurveEnabled) return;
                if (!_device.IsConnected) return;
                if (_cpuTemp <= 0) return; // no temp data yet

                ushort target = _curveCtrl.GetTargetRpm(_cpuTemp);

                // Only set if different from current
                if (_fanSpeed0 != target || _fanSpeed1 != target)
                {
                    _device.SetFanSpeed(0, target);
                    _device.SetFanSpeed(1, target);
                    _fanSpeed0 = target;
                    _fanSpeed1 = target;
                    _logger.Log("FanCurve: CPU=" + _cpuTemp.ToString("F0") + "C -> " + target + " RPM");
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

        // ---- Toggle auto fan curve ----
        void ToggleAutoFanCurve()
        {
            _autoFanCurveEnabled = !_autoFanCurveEnabled;
            if (!_autoFanCurveEnabled)
            {
                // Restore EC auto fan control
                _device.SetPowerMode(_currentMode, autoFan: true);
                _logger.Log("AutoFanCurve: disabled (EC auto restored)");
            }
            else
            {
                _logger.Log("AutoFanCurve: enabled");
            }
            UpdateFanMenuState();
            UpdateTooltip();
        }

        // ---- Enable/disable fan menu items based on auto fan curve state ----
        void UpdateFanMenuState()
        {
            try
            {
                if (_fan3500 == null) return;
                bool fanEnabled = !_autoFanCurveEnabled;
                _fan3500.Enabled = fanEnabled;
                _fan4000.Enabled = fanEnabled;
                _fan4500.Enabled = fanEnabled;
                _fan5000.Enabled = fanEnabled;
                _fanAuto.Enabled = fanEnabled;
                _fanCustom.Enabled = fanEnabled;
                _fanAutoCurveItem.Checked = _autoFanCurveEnabled;
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
                _boostItem.Checked = _boostEnabled;

                UpdateFanMenuState();
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
                var game = _gameDetector.DetectForegroundGame();

                if (game != null)
                {
                    // Game detected - apply its mode
                    if (!_gameModeActive)
                    {
                        _gameModeActive = true;
                        _isManualOverride = false;

                        PowerMode targetMode;
                        if (game.mode != null && game.mode.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0)
                            targetMode = PowerMode.Creator;
                        else if (game.mode != null && game.mode.IndexOf("Game", StringComparison.OrdinalIgnoreCase) >= 0)
                            targetMode = PowerMode.Gaming;
                        else
                            targetMode = PowerMode.Gaming;

                        ushort? fanRpm = game.fanSpeed > 0 ? (ushort?)game.fanSpeed : null;

                        ApplyMode(targetMode, fanRpm);
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
            }
            base.Dispose(disposing);
        }
    }
}
