using System;
using System.IO;
using Microsoft.Win32;

namespace TarkovColor
{
    /// <summary>
    /// Swaps the active Equalizer APO configuration when a profile is applied.
    ///
    /// APO's config folder is user-writable by design (that is how its own Editor works
    /// unelevated), and APO watches that folder, so switching profiles needs no admin
    /// rights and takes effect immediately. Files outside that folder are loaded but
    /// NOT watched, which is why the active file has to live there.
    /// </summary>
    public static class AudioProfile
    {
        private const string ActiveFileName = "active.txt";
        private const string IncludeLine = "Include: " + ActiveFileName;

        /// <summary>Folder holding the user's own EQ presets, next to our executable.</summary>
        public static string PresetDir { get { return Path.Combine(Config.AppDir, "audio"); } }

        /// <summary>APO's config folder, or null when Equalizer APO is not installed.</summary>
        public static string ConfigDir
        {
            get
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey k = b.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
                        {
                            if (k == null) continue;
                            string path = k.GetValue("ConfigPath") as string;
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                        }
                    }
                    catch { }
                }
                return null;
            }
        }

        public static bool IsAvailable { get { return ConfigDir != null; } }

        public static string[] ListPresets()
        {
            try
            {
                if (!Directory.Exists(PresetDir)) return new string[0];
                string[] full = Directory.GetFiles(PresetDir, "*.txt");
                string[] names = new string[full.Length];
                for (int i = 0; i < full.Length; i++) names[i] = Path.GetFileName(full[i]);
                return names;
            }
            catch { return new string[0]; }
        }

        /// <summary>
        /// Writes the profile's EQ preset (or a neutral file when it has none) into APO's
        /// config folder. Silently does nothing when Equalizer APO is not installed.
        /// </summary>
        public static void Apply(Profile p)
        {
            string configDir = ConfigDir;
            if (configDir == null) return;

            string body;
            string presetName = p == null ? null : p.AudioFile;

            if (string.IsNullOrEmpty(presetName))
            {
                body = "# No EQ profile active.\r\n";
            }
            else
            {
                string presetPath = Path.Combine(PresetDir, presetName);
                if (!File.Exists(presetPath))
                {
                    Config.Log("Audio preset not found, leaving EQ neutral: " + presetPath);
                    body = "# Preset missing: " + presetName + "\r\n";
                }
                else
                {
                    body = File.ReadAllText(presetPath);
                }
            }

            try
            {
                File.WriteAllText(Path.Combine(configDir, ActiveFileName), body);
                EnsureInclude(configDir);
            }
            catch (Exception ex)
            {
                Config.Log("Could not write the Equalizer APO config: " + ex.Message);
            }
        }

        /// <summary>Removes our include line and active file, leaving Equalizer APO as we found it.</summary>
        public static void Remove()
        {
            string configDir = ConfigDir;
            if (configDir == null) return;

            try
            {
                string cfg = Path.Combine(configDir, "config.txt");
                if (File.Exists(cfg))
                {
                    string[] lines = File.ReadAllLines(cfg);
                    System.Collections.Generic.List<string> kept = new System.Collections.Generic.List<string>();
                    foreach (string line in lines)
                    {
                        if (line.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase)) continue;
                        kept.Add(line);
                    }
                    File.WriteAllLines(cfg, kept.ToArray());
                }

                string active = Path.Combine(configDir, ActiveFileName);
                if (File.Exists(active)) File.Delete(active);

                Config.Log("Removed the Equalizer APO integration.");
            }
            catch (Exception ex)
            {
                Config.Log("Could not clean up the Equalizer APO config: " + ex.Message);
            }
        }

        /// <summary>Re-adds the include line if something dropped it. Safe to call at any time.</summary>
        public static void EnsureIncludeNow()
        {
            string configDir = ConfigDir;
            if (configDir != null) EnsureInclude(configDir);
        }

        /// <summary>
        /// Makes sure config.txt pulls in our active file. APO's Editor rewrites config.txt
        /// wholesale when it saves, which drops the line, so this is re-checked every time.
        /// </summary>
        private static void EnsureInclude(string configDir)
        {
            string cfg = Path.Combine(configDir, "config.txt");
            try
            {
                string existing = File.Exists(cfg) ? File.ReadAllText(cfg) : "";
                if (existing.IndexOf(IncludeLine, StringComparison.OrdinalIgnoreCase) >= 0) return;

                string sep = existing.Length == 0 || existing.EndsWith("\n") ? "" : "\r\n";
                File.WriteAllText(cfg, existing + sep + IncludeLine + "\r\n");
                Config.Log("Re-added the Equalizer APO include line to config.txt");
            }
            catch (Exception ex)
            {
                Config.Log("Could not update config.txt: " + ex.Message);
            }
        }
    }
}
