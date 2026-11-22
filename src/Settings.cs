using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Keys = System.Windows.Forms.Keys;

namespace Kadr
{
    /// <summary>A user-defined upload service. Field names follow ShareX's .sxcu files so those import as they are.</summary>
    public class CustomUploader
    {
        public string Name { get; set; }
        public string RequestMethod { get; set; }
        public string RequestURL { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string Body { get; set; }
        public Dictionary<string, string> Arguments { get; set; }
        public string FileFormName { get; set; }
        public string URL { get; set; }
        public string DeletionURL { get; set; }

        public CustomUploader()
        {
            RequestMethod = "POST";
            Body = "MultipartFormData";
            FileFormName = "file";
            Parameters = new Dictionary<string, string>();
            Headers = new Dictionary<string, string>();
            Arguments = new Dictionary<string, string>();
        }

        public CustomUploader Clone()
        {
            var c = (CustomUploader)MemberwiseClone();
            c.Parameters = new Dictionary<string, string>(Parameters ?? new Dictionary<string, string>());
            c.Headers = new Dictionary<string, string>(Headers ?? new Dictionary<string, string>());
            c.Arguments = new Dictionary<string, string>(Arguments ?? new Dictionary<string, string>());
            return c;
        }

        public void Normalize()
        {
            if (Parameters == null) Parameters = new Dictionary<string, string>();
            if (Headers == null) Headers = new Dictionary<string, string>();
            if (Arguments == null) Arguments = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(RequestMethod)) RequestMethod = "POST";
            if (string.IsNullOrEmpty(Body)) Body = "MultipartFormData";
            if (string.IsNullOrEmpty(FileFormName)) FileFormName = "file";
            if (string.IsNullOrEmpty(Name)) Name = "Мой сервис";
        }
    }

    public class Settings
    {
        public const string Imgur = "Imgur";
        public const string Catbox = "Catbox";

        /// <summary>catbox.moe: free, no account or key, permanent links; the reply is the bare link.</summary>
        public static CustomUploader CatboxConfig()
        {
            var u = new CustomUploader { Name = Catbox, RequestURL = "https://catbox.moe/user/api.php", FileFormName = "fileToUpload" };
            u.Arguments["reqtype"] = "fileupload";
            return u;
        }

        public string DefaultDestination { get; set; }
        public string ImgurClientId { get; set; }
        public bool CopyLink { get; set; }
        public bool OpenInBrowser { get; set; }
        public bool AskFileName { get; set; }
        public string SaveFolder { get; set; }
        /// <summary>Whether the capture hotkey is on (name kept from the PrtScn-only version).</summary>
        public bool HookPrintScreen { get; set; }
        /// <summary>System.Windows.Forms.Keys value: key code plus Shift/Control/Alt bits.</summary>
        public int Hotkey { get; set; }
        public int PenColor { get; set; }
        public int PenSize { get; set; }
        public List<CustomUploader> Uploaders { get; set; }

        public Settings()
        {
            DefaultDestination = Catbox;
            ImgurClientId = "";
            CopyLink = true;
            AskFileName = true;
            HookPrintScreen = true;
            Hotkey = (int)(Keys.Shift | Keys.PageUp);
            SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Кадр");
            PenColor = Color.FromArgb(229, 57, 53).ToArgb();
            PenSize = 4;
            Uploaders = new List<CustomUploader>();
        }

        public static string Dir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kadr");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }
        public static bool FirstRun { get; private set; }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = Json.Ser.Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s.Uploaders == null) s.Uploaders = new List<CustomUploader>();
                    foreach (var u in s.Uploaders) u.Normalize();
                    if (s.PenSize < 1 || s.PenSize > 20) s.PenSize = 4;
                    if (string.IsNullOrEmpty(s.SaveFolder)) s.SaveFolder = new Settings().SaveFolder;
                    if (!HotkeyAllowed(s.Hotkey)) s.Hotkey = new Settings().Hotkey;
                    return s;
                }
            }
            catch { }
            FirstRun = true;
            return new Settings();
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, Json.Ser.Serialize(this), Encoding.UTF8); } catch { }
        }

        public static string KeyText(int hotkey)
        {
            var k = (Keys)hotkey;
            var code = k & Keys.KeyCode;
            var parts = new List<string>();
            if ((k & Keys.Control) != 0) parts.Add("Ctrl");
            if ((k & Keys.Alt) != 0) parts.Add("Alt");
            if ((k & Keys.Shift) != 0) parts.Add("Shift");
            string n;
            if (code == Keys.PageUp) n = "PageUp";
            else if (code == Keys.PageDown) n = "PageDown";
            else if (code == Keys.PrintScreen) n = "PrtScn";
            else if (code == Keys.Scroll) n = "ScrollLock";
            else if (code == Keys.Return) n = "Enter";
            else if (code >= Keys.D0 && code <= Keys.D9) n = ((char)('0' + (code - Keys.D0))).ToString();
            else if (code >= Keys.NumPad0 && code <= Keys.NumPad9) n = "Num " + (int)(code - Keys.NumPad0);
            else if (code >= Keys.A && code <= Keys.Z) n = ((char)code).ToString();
            else n = code.ToString();
            parts.Add(n);
            return string.Join("+", parts);
        }

        /// <summary>Keys that type text must come with Ctrl, Alt or Shift, or typing would take screenshots.</summary>
        public static bool HotkeyAllowed(int hotkey)
        {
            var k = (Keys)hotkey;
            var code = k & Keys.KeyCode;
            if (code == Keys.None || code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu) return false;
            if ((k & (Keys.Control | Keys.Alt | Keys.Shift)) != 0) return true;
            return (code >= Keys.F1 && code <= Keys.F24)
                || code == Keys.PrintScreen || code == Keys.Pause || code == Keys.Scroll
                || code == Keys.Insert || code == Keys.PageUp || code == Keys.PageDown;
        }

        public List<string> Destinations()
        {
            var list = new List<string> { Catbox, Imgur };
            foreach (var u in Uploaders) if (!list.Contains(u.Name)) list.Add(u.Name);
            return list;
        }

        public string Default()
        {
            return Destinations().Contains(DefaultDestination) ? DefaultDestination : Catbox;
        }
    }

    public class HistoryItem
    {
        public DateTime Time { get; set; }
        public string Destination { get; set; }
        public string Url { get; set; }
        public string DeleteUrl { get; set; }
        public string ImgurDeleteHash { get; set; }
        public string Thumb { get; set; }
    }

    static class History
    {
        static string FilePath { get { return Path.Combine(Settings.Dir, "history.json"); } }
        public static string ThumbDir
        {
            get { string d = Path.Combine(Settings.Dir, "thumbs"); Directory.CreateDirectory(d); return d; }
        }

        public static List<HistoryItem> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return Json.Ser.Deserialize<List<HistoryItem>>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new List<HistoryItem>();
            }
            catch { }
            return new List<HistoryItem>();
        }

        public static void Save(List<HistoryItem> items)
        {
            try { File.WriteAllText(FilePath, Json.Ser.Serialize(items), Encoding.UTF8); } catch { }
        }

        public static void Add(HistoryItem item, Image thumbSource)
        {
            var items = Load();
            if (thumbSource != null)
            {
                try
                {
                    string name = Guid.NewGuid().ToString("N") + ".png";
                    using (var t = Imaging.Fit(thumbSource, 160, 100)) t.Save(Path.Combine(ThumbDir, name), System.Drawing.Imaging.ImageFormat.Png);
                    item.Thumb = name;
                }
                catch { }
            }
            items.Insert(0, item);
            while (items.Count > 500)
            {
                DeleteThumb(items[items.Count - 1]);
                items.RemoveAt(items.Count - 1);
            }
            Save(items);
        }

        public static void DeleteThumb(HistoryItem it)
        {
            if (string.IsNullOrEmpty(it.Thumb)) return;
            try { File.Delete(Path.Combine(ThumbDir, it.Thumb)); } catch { }
        }
    }

    static class Json
    {
        public static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        /// <summary>Walks a parsed JSON value by a path like "data.link" or "files[0].url".</summary>
        public static object Path(object root, string path)
        {
            object cur = root;
            foreach (var rawPart in path.Split('.'))
            {
                string part = rawPart.Trim();
                if (part.Length == 0) continue;
                string name = part;
                var indexes = new List<int>();
                int br = part.IndexOf('[');
                if (br >= 0)
                {
                    name = part.Substring(0, br);
                    foreach (var piece in part.Substring(br).Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int i;
                        if (int.TryParse(piece, out i)) indexes.Add(i);
                    }
                }
                if (name.Length > 0)
                {
                    var dict = cur as IDictionary<string, object>;
                    if (dict == null || !dict.TryGetValue(name, out cur)) return null;
                }
                foreach (int i in indexes)
                {
                    var arr = cur as IList;
                    if (arr == null || i < 0 || i >= arr.Count) return null;
                    cur = arr[i];
                }
            }
            return cur;
        }

        public static string Str(object root, string path)
        {
            object v = Path(root, path);
            if (v == null) return null;
            if (v is IDictionary<string, object> || v is IList) return Ser.Serialize(v);
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static object TryParse(string text)
        {
            try { return Ser.DeserializeObject(text); } catch { return null; }
        }
    }
}
