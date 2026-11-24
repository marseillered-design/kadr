using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Kadr
{
    class ReleaseInfo
    {
        public string Version;
        public string Page;
        public string AssetName;
        public string AssetUrl;
        public string Sha256;
        public long Size;
    }

    /// <summary>
    /// Self-update from the GitHub Releases of this project: a newer release is downloaded, checked against the
    /// SHA-256 GitHub publishes for it, then installed — the setup runs silently for installed copies, a portable
    /// Kadr.exe swaps itself — and Kadr starts again.
    /// </summary>
    static class Updater
    {
        /// <summary>
        /// The GitHub repository this build came from (set by build.ps1 from GITHUB_REPOSITORY), so a fork's
        /// builds update from the fork and never overwrite its changes with the original's releases.
        /// </summary>
        public static string Repo
        {
            get
            {
                foreach (AssemblyMetadataAttribute m in Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                    if (m.Key == "Repo" && !string.IsNullOrEmpty(m.Value)) return m.Value;
                return "marseillered-design/kadr";
            }
        }
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Kadr-Updater");
            return c;
        }

        /// <summary>Set by build.ps1 from the release tag; "0.0.0-dev" for local builds.</summary>
        public static string Version
        {
            get
            {
                var a = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                return a.Length > 0 ? ((AssemblyInformationalVersionAttribute)a[0]).InformationalVersion : "0.0.0-dev";
            }
        }

        /// <summary>Builds made on a computer (not by GitHub for a release) never update themselves.</summary>
        public static bool IsDev { get { return Version.StartsWith("0.0.0"); } }

        public static bool InstalledWithSetup
        {
            get { return File.Exists(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "unins000.exe")); }
        }

        public static int Compare(string a, string b)
        {
            Func<string, int[]> parse = v => (v ?? "").TrimStart('v').Split('-')[0].Split('.')
                .Select(x => { int n; return int.TryParse(x, out n) ? n : 0; }).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
            var x1 = parse(a);
            var x2 = parse(b);
            for (int i = 0; i < 3; i++)
                if (x1[i] != x2[i]) return x1[i].CompareTo(x2[i]);
            return 0;
        }

        public static async Task<ReleaseInfo> Latest()
        {
            string json = await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest");
            var d = Json.TryParse(json) as IDictionary<string, object>;
            if (d == null || !d.ContainsKey("tag_name")) throw new Exception("no release information");
            string want = InstalledWithSetup ? "Kadr-Setup-Windows.exe" : "Kadr.exe";
            var info = new ReleaseInfo
            {
                Version = Convert.ToString(d["tag_name"]).TrimStart('v'),
                Page = d.ContainsKey("html_url") ? Convert.ToString(d["html_url"]) : "https://github.com/" + Repo + "/releases",
                AssetName = want
            };
            var assets = d.ContainsKey("assets") ? d["assets"] as IList : null;
            foreach (var o in assets ?? new ArrayList())
            {
                var a = o as IDictionary<string, object>;
                if (a == null || Convert.ToString(a["name"]) != want) continue;
                info.AssetUrl = Convert.ToString(a["browser_download_url"]);
                info.Size = Convert.ToInt64(a["size"]);
                string digest = a.ContainsKey("digest") ? Convert.ToString(a["digest"]) : null;
                if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:")) info.Sha256 = digest.Substring(7).ToLowerInvariant();
            }
            return info;
        }

        public static async Task<string> Download(ReleaseInfo r)
        {
            if (string.IsNullOrEmpty(r.AssetUrl) || !r.AssetUrl.StartsWith("https://github.com/" + Repo + "/releases/download/"))
                throw new Exception("no download for this system in the release");
            byte[] data = await Http.GetByteArrayAsync(r.AssetUrl);
            if (r.Size > 0 && data.LongLength != r.Size) throw new Exception("the download is incomplete");
            if (r.Sha256 != null)
            {
                using (var sha = SHA256.Create())
                {
                    string got = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
                    if (got != r.Sha256) throw new Exception("the download does not match the release (SHA-256)");
                }
            }
            string dir = Path.Combine(Path.GetTempPath(), "KadrUpdate");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, r.Version + "-" + r.AssetName);
            File.WriteAllBytes(path, data);
            return path;
        }

        /// <summary>Starts the new version; the caller then quits so the old one is gone.</summary>
        public static void Install(string file)
        {
            if (InstalledWithSetup)
            {
                // the setup closes Kadr, replaces it and starts it again (its [Run] entry for silent installs)
                Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS") { UseShellExecute = true });
                return;
            }
            // portable: a running .exe can be renamed but not overwritten
            string exe = Application.ExecutablePath;
            string old = Path.ChangeExtension(exe, ".old.exe");
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(file, exe); }
            catch { File.Move(old, exe); throw; }
            Process.Start(new ProcessStartInfo(exe, "--tray --updated") { UseShellExecute = true });
        }

        /// <summary>After an update: remove the previous .exe and the downloaded files.</summary>
        public static void Cleanup()
        {
            try
            {
                string old = Path.ChangeExtension(Application.ExecutablePath, ".old.exe");
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "KadrUpdate");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}
