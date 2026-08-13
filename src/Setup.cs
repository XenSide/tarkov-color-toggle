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

        /// <summary>Always resolves to the current release rather than a pinned version.</summary>
        private const string ApoInstallerUrl =
            "https://sourceforge.net/projects/equalizerapo/files/latest/download";

        /// <summary>
        /// Downloads Equalizer APO's own installer and runs it. One step cannot be automated:
        /// its installer asks which output device to attach to. Current versions normally take
        /// effect immediately, so a reboot is a fallback rather than a requirement.
        /// </summary>
        /// <summary>Returns true once Equalizer APO is present, so the caller can carry straight on.</summary>
        public static bool OfferApo(IWin32Window owner)
        {
            DialogResult r = MessageBox.Show(owner,
                "Equalizer APO is needed for the audio side. The display side works fine without it.\n\n"
                + "Download and run its installer now?\n\n"
                + "One step cannot be done for you: it asks which output device to attach to, "
                + "so tick your headphones there. Setup carries on by itself once you finish it.",
                "Display Profile Switcher - Audio setup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (r != DialogResult.Yes) return false;

            string installer = Path.Combine(Path.GetTempPath(), "EqualizerAPO-setup.exe");
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // Tls12
                using (WebClient wc = new WebClient())
                {
                    // Deliberately no User-Agent. SourceForge answers 403 to requests that
                    // claim to be a browser but are not one; the header-less request is
                    // allowed through. Adding a "realistic" UA here breaks the download.
                    wc.DownloadFile(ApoInstallerUrl, installer);
                }

                if (new FileInfo(installer).Length < 1000000)
                    throw new Exception("The downloaded file is too small to be the installer.");

                Config.Log("Downloaded the Equalizer APO installer, launching it.");

                ProcessStartInfo psi = new ProcessStartInfo(installer);
                psi.UseShellExecute = true;

                // Wait for it rather than returning, so the rest of the audio setup can run
                // in the same sitting instead of making the user come back to the tray menu.
                // We are already elevated, so the installer does not spawn a separate
                // elevated process that would leave us waiting on the wrong handle.
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                }

                // The registry entry can lag slightly behind the installer exiting.
                for (int i = 0; i < 10 && !ApoInstalled; i++) System.Threading.Thread.Sleep(300);

                if (!ApoInstalled)
                {
                    Config.Log("Equalizer APO still not detected after its installer closed.");
                    MessageBox.Show(owner,
                        "Equalizer APO still is not detected.\n\n"
                        + "If you cancelled its installer, run \"Audio setup...\" from the tray menu "
                        + "to try again. If you did complete it, reboot and run that once more.",
                        "Display Profile Switcher - Audio setup",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                Config.Log("Equalizer APO detected after installation.");
                return true;
            }
            catch (Exception ex)
            {
                Config.Log("Equalizer APO download failed: " + ex.Message);
                DialogResult fb = MessageBox.Show(owner,
                    "Could not download Equalizer APO automatically:\n" + ex.Message + "\n\n"
                    + "Open its download page instead?",
                    "Display Profile Switcher - Audio setup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (fb == DialogResult.Yes) OpenUrl(ApoSite);
                return false;
            }
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
                    // No User-Agent here either, for the same reason as the APO download.
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
