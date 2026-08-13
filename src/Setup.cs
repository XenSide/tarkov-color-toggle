using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace TarkovColor
{
    /// <summary>
    /// Optional third-party pieces for the audio side.
    ///
    /// Neither is bundled, and that is deliberate rather than incidental: Equalizer APO
    /// is GPLv2, and LoudMax is freeware whose author explicitly forbids redistribution.
    /// Both are therefore fetched from their own official sources at setup time, so the
    /// user obtains them from the authors rather than from this project.
    /// </summary>
    public static class Setup
    {
        public const string ApoSite = "https://equalizerapo.com/download.html";
        public const string LoudMaxSite = "https://loudmax.blogspot.com/";

        private const string LoudMaxZipUrl =
            "https://www.dropbox.com/scl/fi/32w2qx7qtr8d14bzzrnwr/LoudMax_v1_47_WIN_VST2.zip?rlkey=8nnbs9speatvm40fy9iax69kb&dl=1";

        public static bool ApoInstalled { get { return AudioProfile.ConfigDir != null; } }

        public static string VstDir
        {
            get
            {
                string cfg = AudioProfile.ConfigDir;
                if (cfg == null) return null;
                return Path.Combine(Path.GetDirectoryName(cfg), "VSTPlugins");
            }
        }

        public static bool LoudMaxInstalled
        {
            get
            {
                string dir = VstDir;
                return dir != null && File.Exists(Path.Combine(dir, "LoudMax64.dll"));
            }
        }

        /// <summary>Points the user at Equalizer APO's own installer; it needs device selection and a reboot.</summary>
        public static void OfferApo(IWin32Window owner)
        {
            DialogResult r = MessageBox.Show(owner,
                "Equalizer APO is not installed.\n\n"
                + "It is what makes the audio side work; the display side works fine without it.\n\n"
                + "Open its download page? Its installer asks which output device to attach to "
                + "(pick your headphones) and needs a reboot. Run this setup again afterwards.",
                "Display Profile Switcher - Audio setup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (r == DialogResult.Yes) OpenUrl(ApoSite);
        }

        /// <summary>
        /// Downloads LoudMax from the author's site into Equalizer APO's plugin folder.
        /// Requires elevation, since that folder is not user-writable.
        /// </summary>
        public static bool InstallLoudMax(IWin32Window owner)
        {
            string vst = VstDir;
            if (vst == null) return false;

            string tempZip = Path.Combine(Path.GetTempPath(), "LoudMax_VST2.zip");
            string tempDir = Path.Combine(Path.GetTempPath(), "LoudMax_extract");

            try
            {
                // .NET 4.0 defaults to TLS 1.0, which modern hosts refuse.
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // Tls12

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0");
                    wc.DownloadFile(LoudMaxZipUrl, tempZip);
                }

                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir);

                string dll = Path.Combine(tempDir, "LoudMax64.dll");
                if (!File.Exists(dll)) throw new Exception("LoudMax64.dll was not in the archive.");

                Directory.CreateDirectory(vst);
                File.Copy(dll, Path.Combine(vst, "LoudMax64.dll"), true);

                string dll32 = Path.Combine(tempDir, "LoudMax.dll");
                if (File.Exists(dll32)) File.Copy(dll32, Path.Combine(vst, "LoudMax.dll"), true);

                Config.Log("Installed LoudMax into " + vst);
                return true;
            }
            catch (Exception ex)
            {
                Config.Log("LoudMax download failed: " + ex.Message);

                // The download link can rot; fall back to letting the user fetch it themselves.
                DialogResult r = MessageBox.Show(owner,
                    "Could not download LoudMax automatically:\n" + ex.Message + "\n\n"
                    + "Open the author's site to download it manually?\n"
                    + "Pick the VST2 build for Windows and put LoudMax64.dll in:\n" + vst,
                    "Display Profile Switcher - LoudMax",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (r == DialogResult.Yes) OpenUrl(LoudMaxSite);
                return false;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Whether any shipped preset actually loads the limiter plugin. Matches the
        /// VSTPlugin directive rather than the word, so that presets merely mentioning
        /// LoudMax in a comment do not trigger a pointless download prompt.
        /// </summary>
        public static bool AnyPresetNeedsLoudMax()
        {
            try
            {
                foreach (string f in Directory.GetFiles(AudioProfile.PresetDir, "*.txt"))
                {
                    foreach (string raw in File.ReadAllLines(f))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        if (line.StartsWith("VSTPlugin", StringComparison.OrdinalIgnoreCase) &&
                            line.IndexOf("LoudMax", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Config.Log("Could not open " + url + ": " + ex.Message);
            }
        }
    }
}
