using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

internal static class TarkovColorToggle
{
    private const string ProcessName = "EscapeFromTarkov.exe";
    private const string TaskOnName = "TarkovColorOn";
    private const string TaskOffName = "TarkovColorOff";
    private const string FilterOnName = "TarkovColorStartFilter";
    private const string FilterOffName = "TarkovColorStopFilter";
    private const string ConsumerOnName = "TarkovColorStartConsumer";
    private const string ConsumerOffName = "TarkovColorStopConsumer";
    private const string Scope = @"root\subscription";

    private static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
    private static string ExeDir { get { return Path.GetDirectoryName(ExePath); } }
    private static string LogPath { get { return Path.Combine(ExeDir, "toggle.log"); } }
    private static string SchtasksPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"); } }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDCW(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && Eq(args[0], "-On")) { DoToggle(true); return 0; }
            if (args.Length == 1 && Eq(args[0], "-Off")) { DoToggle(false); return 0; }

            if (args.Length >= 1 && Eq(args[0], "-Install"))
            {
                string user = args.Length >= 2 ? args[1] : (Environment.UserDomainName + "\\" + Environment.UserName);
                if (!IsElevated()) { RelaunchElevated("-Install", user); return 0; }
                Install(user);
                MessageBox.Show("Installed. It's now watching for " + ProcessName + ".", "Tarkov Color Toggle");
                return 0;
            }

            if (args.Length >= 1 && Eq(args[0], "-Uninstall"))
            {
                if (!IsElevated()) { RelaunchElevated("-Uninstall"); return 0; }
                Uninstall();
                MessageBox.Show("Uninstalled.", "Tarkov Color Toggle");
                return 0;
            }

            return InteractiveMenu();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Tarkov Color Toggle - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int InteractiveMenu()
    {
        bool installed = TaskExists(TaskOnName);
        if (!installed)
        {
            DialogResult result = MessageBox.Show(
                "Install automatic color profile switching for " + ProcessName + "?\n\n" +
                "This applies the .icc profile found next to this program while " + ProcessName +
                " is running, and reverts when it closes.",
                "Tarkov Color Toggle - Install",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                string user = Environment.UserDomainName + "\\" + Environment.UserName;
                if (!IsElevated()) { RelaunchElevated("-Install", user); return 0; }
                Install(user);
                MessageBox.Show("Installed. It's now watching for " + ProcessName + ".", "Tarkov Color Toggle");
            }
        }
        else
        {
            DialogResult result = MessageBox.Show(
                "Tarkov Color automation is currently installed.\n\nUninstall it?",
                "Tarkov Color Toggle - Uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                if (!IsElevated()) { RelaunchElevated("-Uninstall"); return 0; }
                Uninstall();
                MessageBox.Show("Uninstalled.", "Tarkov Color Toggle");
            }
        }
        return 0;
    }

    // ---------- toggle action ----------

    private static void DoToggle(bool on)
    {
        string primary = PrimaryDisplayDeviceName();
        ushort[] ramp = on ? GetIccVcgtRamp(FindIccPathOrThrow()) : GetIdentityRamp();

        IntPtr hdc = CreateDCW(primary, null, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            throw new Exception("CreateDCW failed for " + primary + ", err=" + Marshal.GetLastWin32Error());
        try
        {
            if (!SetDeviceGammaRamp(hdc, ramp))
                throw new Exception("SetDeviceGammaRamp failed for " + primary + ", err=" + Marshal.GetLastWin32Error());
        }
        finally
        {
            DeleteDC(hdc);
        }

        Log((on ? "On" : "Off") + " Device=" + primary + " Result=OK User=" + Environment.UserName);
    }

    private static string FindIccPathOrThrow()
    {
        string preferred = Path.Combine(ExeDir, "profile.icc");
        if (File.Exists(preferred)) return preferred;

        string[] candidates = Directory.GetFiles(ExeDir, "*.icc");
        if (candidates.Length > 0) return candidates[0];

        throw new Exception("No .icc profile found next to " + ExePath);
    }

    private static string PrimaryDisplayDeviceName()
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            if (screen.Primary) return screen.DeviceName;
        }
        throw new Exception("Could not find primary screen");
    }

    // ---------- install / uninstall ----------

    private static bool IsElevated()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static void RelaunchElevated(params string[] args)
    {
        StringBuilder sb = new StringBuilder();
        foreach (string a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(a).Append('"');
        }
        ProcessStartInfo psi = new ProcessStartInfo(ExePath, sb.ToString())
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        try { Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { /* user declined UAC */ }
    }

    private static void Install(string user)
    {
        if (FindIccPathOrThrow() == null) throw new Exception("No .icc profile found next to the executable.");

        Uninstall(); // clean slate

        CreateTask(TaskOnName, "-On", user);
        CreateTask(TaskOffName, "-Off", user);

        CreateEventFilter(FilterOnName, "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '" + ProcessName + "'");
        string stopPrefix = ProcessName.Length > 14 ? ProcessName.Substring(0, 14) : ProcessName;
        CreateEventFilter(FilterOffName, "SELECT * FROM Win32_ProcessStopTrace WHERE ProcessName LIKE '" + stopPrefix + "%'");

        CreateCommandLineConsumer(ConsumerOnName, "\"" + SchtasksPath + "\" /run /tn \"" + TaskOnName + "\"");
        CreateCommandLineConsumer(ConsumerOffName, "\"" + SchtasksPath + "\" /run /tn \"" + TaskOffName + "\"");

        CreateBinding(FilterOnName, ConsumerOnName);
        CreateBinding(FilterOffName, ConsumerOffName);

        Log("Installed for user=" + user);
    }

    private static void Uninstall()
    {
        RemoveBindings();
        RemoveInstancesByName("__EventFilter", FilterOnName);
        RemoveInstancesByName("__EventFilter", FilterOffName);
        RemoveInstancesByName("CommandLineEventConsumer", ConsumerOnName);
        RemoveInstancesByName("CommandLineEventConsumer", ConsumerOffName);

        DeleteTaskIfExists(TaskOnName);
        DeleteTaskIfExists(TaskOffName);

        Log("Uninstalled");
    }

    private static void CreateTask(string taskName, string arg, string user)
    {
        string tr = "\\\"" + ExePath + "\\\" " + arg;
        string cmdArgs = "/create /tn \"" + taskName + "\" /tr \"" + tr +
            "\" /sc ONCE /st 00:00 /sd 01/01/2020 /ru \"" + user + "\" /it /rl LIMITED /f";
        RunProcess(SchtasksPath, cmdArgs, true);
    }

    private static void DeleteTaskIfExists(string taskName)
    {
        RunProcess(SchtasksPath, "/delete /tn \"" + taskName + "\" /f", false);
    }

    private static bool TaskExists(string taskName)
    {
        return RunProcess(SchtasksPath, "/query /tn \"" + taskName + "\"", false) == 0;
    }

    private static int RunProcess(string exe, string args, bool throwOnError)
    {
        ProcessStartInfo psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (Process p = Process.Start(psi))
        {
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (throwOnError && p.ExitCode != 0)
                throw new Exception(exe + " " + args + " failed: " + stdout + stderr);
            return p.ExitCode;
        }
    }

    private static void CreateEventFilter(string name, string query)
    {
        ManagementClass mc = new ManagementClass(new ManagementScope(Scope), new ManagementPath("__EventFilter"), null);
        ManagementObject obj = mc.CreateInstance();
        obj["Name"] = name;
        obj["EventNamespace"] = "root\\cimv2";
        obj["QueryLanguage"] = "WQL";
        obj["Query"] = query;
        obj.Put();
    }

    private static void CreateCommandLineConsumer(string name, string commandLine)
    {
        ManagementClass mc = new ManagementClass(new ManagementScope(Scope), new ManagementPath("CommandLineEventConsumer"), null);
        ManagementObject obj = mc.CreateInstance();
        obj["Name"] = name;
        obj["CommandLineTemplate"] = commandLine;
        obj.Put();
    }

    private static void CreateBinding(string filterName, string consumerName)
    {
        ManagementClass mc = new ManagementClass(new ManagementScope(Scope), new ManagementPath("__FilterToConsumerBinding"), null);
        ManagementObject obj = mc.CreateInstance();
        obj["Filter"] = "__EventFilter.Name=\"" + filterName + "\"";
        obj["Consumer"] = "CommandLineEventConsumer.Name=\"" + consumerName + "\"";
        obj.Put();
    }

    private static void RemoveBindings()
    {
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(
            new ManagementScope(Scope), new ObjectQuery("SELECT * FROM __FilterToConsumerBinding"));
        foreach (ManagementObject obj in searcher.Get())
        {
            string filter = Convert.ToString(obj["Filter"]);
            string consumer = Convert.ToString(obj["Consumer"]);
            if ((filter != null && filter.IndexOf("TarkovColor", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (consumer != null && consumer.IndexOf("TarkovColor", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                obj.Delete();
            }
        }
    }

    private static void RemoveInstancesByName(string className, string name)
    {
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(
            new ManagementScope(Scope), new ObjectQuery("SELECT * FROM " + className + " WHERE Name = '" + name + "'"));
        foreach (ManagementObject obj in searcher.Get())
        {
            obj.Delete();
        }
    }

    // ---------- vcgt parsing ----------

    private static void Log(string msg)
    {
        File.AppendAllText(LogPath, DateTime.Now.ToString("o") + " " + msg + Environment.NewLine);
    }

    private static bool Eq(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

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

    private static ushort[] GetIdentityRamp()
    {
        ushort[] ramp = new ushort[768];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++)
                ramp[c * 256 + i] = (ushort)Math.Min(65535, i * 257);
        return ramp;
    }

    private static ushort[] GetIccVcgtRamp(string iccPath)
    {
        byte[] bytes = File.ReadAllBytes(iccPath);
        uint tagCount = ReadUInt32BE(bytes, 128);

        int vcgtOffset = -1;
        for (int i = 0; i < tagCount; i++)
        {
            int entryOff = 132 + (i * 12);
            string sig = Encoding.ASCII.GetString(bytes, entryOff, 4);
            if (sig == "vcgt")
            {
                vcgtOffset = (int)ReadUInt32BE(bytes, entryOff + 4);
                break;
            }
        }
        if (vcgtOffset < 0) throw new Exception("No vcgt tag found in " + iccPath);

        uint gammaType = ReadUInt32BE(bytes, vcgtOffset + 8);
        ushort[] ramp = new ushort[768];

        if (gammaType == 0)
        {
            ushort numChannels = ReadUInt16BE(bytes, vcgtOffset + 12);
            ushort numEntries = ReadUInt16BE(bytes, vcgtOffset + 14);
            ushort entrySize = ReadUInt16BE(bytes, vcgtOffset + 16);
            if (numChannels != 3) throw new Exception("Unexpected vcgt channel count: " + numChannels);

            int dataStart = vcgtOffset + 18;
            double[][] channelValues = new double[3][];
            for (int c = 0; c < 3; c++) channelValues[c] = new double[numEntries];

            for (int c = 0; c < 3; c++)
            {
                for (int e = 0; e < numEntries; e++)
                {
                    int off = dataStart + (c * numEntries + e) * entrySize;
                    if (entrySize == 1) channelValues[c][e] = bytes[off] * 257.0;
                    else if (entrySize == 2) channelValues[c][e] = ReadUInt16BE(bytes, off);
                    else throw new Exception("Unsupported vcgt entry size: " + entrySize);
                }
            }

            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < 256; i++)
                {
                    double v;
                    if (numEntries == 256) v = channelValues[c][i];
                    else
                    {
                        int srcIdx = Math.Min(numEntries - 1, (int)Math.Round(i * (numEntries - 1) / 255.0));
                        v = channelValues[c][srcIdx];
                    }
                    ramp[c * 256 + i] = (ushort)Math.Round(Math.Min(65535, Math.Max(0, v)));
                }
            }
        }
        else if (gammaType == 1)
        {
            int baseOff = vcgtOffset + 12;
            for (int c = 0; c < 3; c++)
            {
                int chOff = baseOff + c * 12;
                double gamma = ReadInt32BE(bytes, chOff) / 65536.0;
                double min = ReadInt32BE(bytes, chOff + 4) / 65536.0;
                double max = ReadInt32BE(bytes, chOff + 8) / 65536.0;
                for (int i = 0; i < 256; i++)
                {
                    double v = (Math.Pow(i / 255.0, gamma) * (max - min) + min) * 65535.0;
                    ramp[c * 256 + i] = (ushort)Math.Round(Math.Min(65535, Math.Max(0, v)));
                }
            }
        }
        else
        {
            throw new Exception("Unsupported vcgt gamma type: " + gammaType);
        }

        return ramp;
    }
}
