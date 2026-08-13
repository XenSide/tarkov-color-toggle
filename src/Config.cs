using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TarkovColor
{
    [DataContract]
    public class Profile
    {
        [DataMember(Order = 0)] public string Name { get; set; }

        /// <summary>File name of an .icc profile in the app folder used as the base curve, or null for a linear base.</summary>
        [DataMember(Order = 1)] public string IccFile { get; set; }

        /// <summary>1.0 = neutral. Higher is brighter midtones.</summary>
        [DataMember(Order = 2)] public double Gamma { get; set; }

        /// <summary>1.0 = neutral.</summary>
        [DataMember(Order = 3)] public double Contrast { get; set; }

        /// <summary>0.0 = neutral, range -0.5 .. 0.5.</summary>
        [DataMember(Order = 4)] public double Brightness { get; set; }

        /// <summary>1.0 = neutral, 0.0 = greyscale. Applied via the Magnification colour matrix.</summary>
        [DataMember(Order = 5)] public double Saturation { get; set; }

        [DataMember(Order = 6)] public uint HotkeyModifiers { get; set; }
        [DataMember(Order = 7)] public uint HotkeyKey { get; set; }

        public Profile()
        {
            Name = "New profile";
            Gamma = 1.0;
            Contrast = 1.0;
            Brightness = 0.0;
            Saturation = 1.0;
        }

        /// <summary>Older config files predate Saturation; 0 there means "unset", not greyscale.</summary>
        [OnDeserialized]
        private void OnDeserialized(StreamingContext ctx)
        {
            if (Gamma <= 0) Gamma = 1.0;
            if (Contrast <= 0) Contrast = 1.0;
            if (Saturation <= 0) Saturation = 1.0;
        }

        public bool HasHotkey { get { return HotkeyKey != 0; } }

        public Profile Clone()
        {
            return new Profile
            {
                Name = Name,
                IccFile = IccFile,
                Gamma = Gamma,
                Contrast = Contrast,
                Brightness = Brightness,
                Saturation = Saturation,
                HotkeyModifiers = HotkeyModifiers,
                HotkeyKey = HotkeyKey
            };
        }
    }

    /// <summary>"While this process runs, use this profile."</summary>
    [DataContract]
    public class AppRule
    {
        /// <summary>Executable name including extension, e.g. "EscapeFromTarkov.exe".</summary>
        [DataMember(Order = 0)] public string ProcessName { get; set; }

        [DataMember(Order = 1)] public string ProfileName { get; set; }

        public AppRule() { }

        public AppRule(string process, string profile)
        {
            ProcessName = process;
            ProfileName = profile;
        }

        public AppRule Clone() { return new AppRule(ProcessName, ProfileName); }

        /// <summary>Process name without the extension, as reported by Process.ProcessName.</summary>
        public string BaseName
        {
            get
            {
                string n = ProcessName == null ? "" : ProcessName.Trim();
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                return n;
            }
        }
    }

    [DataContract]
    public class Config
    {
        [DataMember(Order = 0)] public List<Profile> Profiles { get; set; }

        /// <summary>Legacy single-game setting, migrated into Rules on load.</summary>
        [DataMember(Order = 1)] public string TarkovProfile { get; set; }

        [DataMember(Order = 2)] public uint ResetHotkeyModifiers { get; set; }
        [DataMember(Order = 3)] public uint ResetHotkeyKey { get; set; }

        [DataMember(Order = 4)] public List<AppRule> Rules { get; set; }

        public Config()
        {
            Profiles = new List<Profile>();
            Rules = new List<AppRule>();
        }

        /// <summary>Carries pre-Rules config files forward.</summary>
        [OnDeserialized]
        private void OnDeserialized(StreamingContext ctx)
        {
            if (Profiles == null) Profiles = new List<Profile>();
            if (Rules == null) Rules = new List<AppRule>();

            if (Rules.Count == 0 && !string.IsNullOrEmpty(TarkovProfile))
                Rules.Add(new AppRule("EscapeFromTarkov.exe", TarkovProfile));
            TarkovProfile = null;

            // One executable maps to one profile; drop any duplicates a previous build allowed.
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<AppRule> unique = new List<AppRule>();
            foreach (AppRule r in Rules)
            {
                string key = r.ProcessName == null ? "" : r.ProcessName.Trim();
                if (key.Length == 0 || seen.ContainsKey(key)) continue;
                seen[key] = true;
                unique.Add(r);
            }
            Rules = unique;
        }

        public static string AppDir
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        public static string ConfigPath { get { return Path.Combine(AppDir, "profiles.json"); } }
        public static string StatePath { get { return Path.Combine(AppDir, "state.txt"); } }
        public static string LogPath { get { return Path.Combine(AppDir, "toggle.log"); } }

        public Profile Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Profile p in Profiles)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public static Config Load()
        {
            if (!File.Exists(ConfigPath)) return CreateDefault();
            try
            {
                using (FileStream fs = File.OpenRead(ConfigPath))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(Config));
                    Config cfg = (Config)ser.ReadObject(fs);
                    if (cfg == null) return CreateDefault();
                    if (cfg.Profiles == null) cfg.Profiles = new List<Profile>();
                    return cfg;
                }
            }
            catch
            {
                return CreateDefault();
            }
        }

        public void Save()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(Config));
                ser.WriteObject(ms, this);
                File.WriteAllBytes(ConfigPath, ms.ToArray());
            }
        }

        /// <summary>
        /// First-run config. If an .icc sits next to the exe it becomes the base of a
        /// "Tarkov" profile, preserving the behaviour of earlier versions.
        /// </summary>
        public static Config CreateDefault()
        {
            Config cfg = new Config();

            string icc = null;
            try
            {
                string preferred = Path.Combine(AppDir, "profile.icc");
                if (File.Exists(preferred))
                {
                    icc = "profile.icc";
                }
                else
                {
                    string[] found = Directory.GetFiles(AppDir, "*.icc");
                    if (found.Length > 0) icc = Path.GetFileName(found[0]);
                }
            }
            catch { }

            Profile tarkov = new Profile
            {
                Name = "Tarkov",
                IccFile = icc,
                Gamma = 1.0,
                Contrast = 1.0,
                Brightness = 0.0,
                Saturation = 1.0
            };
            cfg.Profiles.Add(tarkov);
            cfg.Rules.Add(new AppRule("EscapeFromTarkov.exe", tarkov.Name));
            return cfg;
        }

        /// <summary>The profile for the first rule whose process is currently running, or null.</summary>
        public Profile ResolveActiveRule()
        {
            if (Rules.Count == 0) return null;

            System.Collections.Generic.Dictionary<string, bool> running =
                new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    running[p.ProcessName] = true;
                    p.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log("Could not enumerate processes: " + ex.Message);
                return null;
            }

            foreach (AppRule r in Rules)
            {
                string baseName = r.BaseName;
                if (baseName.Length == 0) continue;
                if (running.ContainsKey(baseName)) return Find(r.ProfileName);
            }
            return null;
        }

        public static string ReadActiveProfileName()
        {
            try
            {
                if (File.Exists(StatePath)) return File.ReadAllText(StatePath).Trim();
            }
            catch { }
            return null;
        }

        public static void WriteActiveProfileName(string name)
        {
            try { File.WriteAllText(StatePath, name ?? string.Empty); }
            catch { }
        }

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("o") + " " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
