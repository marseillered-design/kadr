using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Kadr
{
    class UploadResult
    {
        public string Url;
        public string DeleteUrl;
        public string ImgurDeleteHash;
    }

    /// <summary>An error whose text is shown to the user as is.</summary>
    class UserError : Exception
    {
        public UserError(string message) : base(message) { }
    }

    static class Uploader
    {
        static HttpClient Http = CreateClient(null);

        /// <summary>Applies the proxy choice; uploads already running keep their old connection.</summary>
        public static void Configure(Settings s) { Http = CreateClient(s); }

        /// <summary>"10.0.0.1:8080" → http://10.0.0.1:8080; null if it is not a usable address.</summary>
        public static Uri ProxyUri(string address)
        {
            string a = (address ?? "").Trim();
            if (a.Length == 0) return null;
            if (!a.Contains("://")) a = "http://" + a;
            Uri u;
            if (!Uri.TryCreate(a, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https") || string.IsNullOrEmpty(u.Host)) return null;
            return u;
        }

        static HttpClient CreateClient(Settings s)
        {
            var handler = new HttpClientHandler();
            if (s != null && s.ProxyMode == "none") handler.UseProxy = false;
            else if (s != null && s.ProxyMode == "custom")
            {
                var uri = ProxyUri(s.ProxyAddress);
                if (uri != null)
                {
                    var proxy = new WebProxy(uri) { BypassProxyOnLocal = true };
                    if (!string.IsNullOrEmpty(s.ProxyUser)) proxy.Credentials = new NetworkCredential(s.ProxyUser, Secret.Unprotect(s.ProxyPassword));
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }
            }
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Kadr/1.0 (Windows)");
            return c;
        }

        public static Task<UploadResult> Upload(Settings s, string destination, byte[] data, string fileName, string contentType)
        {
            if (destination == Settings.Imgur) return Imgur(ImgurId(s), data, fileName, contentType);
            if (destination == Settings.Catbox) return Custom(Settings.CatboxConfig(), data, fileName, contentType);
            if (destination == Settings.ImgBB) return Imgbb(ImgbbKey(s), data, fileName, contentType);
            var u = s.Uploaders.FirstOrDefault(x => x.Name == destination);
            if (u == null) throw new UserError(L.F("Сервис «{0}» не найден в настройках.", destination));
            return Custom(u, data, fileName, contentType);
        }

        // ---------- Imgur ----------

        /// <summary>
        /// The Client ID built into release builds (from the KADR_IMGUR_CLIENT_ID secret on GitHub, never in the
        /// source), so Imgur works without any setup. A Client ID typed in Settings takes its place.
        /// </summary>
        public static string BuiltInImgurId
        {
            get
            {
                foreach (System.Reflection.AssemblyMetadataAttribute m in System.Reflection.Assembly.GetExecutingAssembly()
                         .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                    if (m.Key == "ImgurClientId" && !string.IsNullOrWhiteSpace(m.Value)) return m.Value.Trim();
                return "";
            }
        }

        public static string ImgurId(Settings s)
        {
            return string.IsNullOrWhiteSpace(s.ImgurClientId) ? BuiltInImgurId : s.ImgurClientId.Trim();
        }

        // ---------- ImgBB ----------

        /// <summary>The ImgBB key built into release builds (GitHub secret KADR_IMGBB_KEY, never in the source).</summary>
        public static string BuiltInImgbbKey { get { return Meta("ImgbbKey"); } }

        public static string ImgbbKey(Settings s)
        {
            return string.IsNullOrWhiteSpace(s.ImgbbKey) ? BuiltInImgbbKey : s.ImgbbKey.Trim();
        }

        static string Meta(string key)
        {
            foreach (System.Reflection.AssemblyMetadataAttribute m in System.Reflection.Assembly.GetExecutingAssembly()
                     .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (m.Key == key && !string.IsNullOrWhiteSpace(m.Value)) return m.Value.Trim();
            return "";
        }

        /// <summary>
        /// api.imgbb.com: pictures up to 32 MB, no videos. The link is the picture itself (i.ibb.co/…),
        /// the delete link opens ImgBB's own deletion page.
        /// </summary>
        public static async Task<UploadResult> Imgbb(string key, byte[] data, string fileName, string contentType)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new UserError(L.T("Для ImgBB нужен API-ключ (Настройки → «Где его взять?»). Без ключа работает Catbox: стрелка ▾ рядом с кнопкой загрузки."));
            if (contentType.StartsWith("video/"))
                throw new UserError(L.T("ImgBB принимает только картинки. Видео загрузи в Catbox."));
            if (data.Length > 32 * 1024 * 1024)
                throw new UserError(L.T("ImgBB принимает картинки до 32 МБ."));

            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(data);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            AddFile(form, file, "image", fileName);
            var name = new StringContent(System.IO.Path.GetFileNameWithoutExtension(SafeName(fileName)));
            name.Headers.ContentType = null;
            name.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"name\"" };
            form.Add(name);

            string api = Environment.GetEnvironmentVariable("KADR_IMGBB_TEST") ?? "https://api.imgbb.com/1/upload";
            var req = new HttpRequestMessage(HttpMethod.Post, api + "?key=" + Uri.EscapeDataString(key.Trim())) { Content = form };
            string text; HttpStatusCode code;
            using (var resp = await Http.SendAsync(req))
            {
                code = resp.StatusCode;
                text = await resp.Content.ReadAsStringAsync();
            }
            object json = Json.TryParse(text);
            string link = json == null ? null : Json.Str(json, "data.url");
            if (!string.IsNullOrEmpty(link))
                return new UploadResult { Url = link, DeleteUrl = Json.Str(json, "data.delete_url") };
            string err = json == null ? null : (Json.Str(json, "error.message") ?? Json.Str(json, "status_txt"));
            if ((int)code == 400 && err != null && err.ToLowerInvariant().Contains("key")) err = L.T("ImgBB не принял API-ключ. Проверь его в настройках.");
            if ((int)code == 429) err = L.T("ImgBB просит подождать: слишком много загрузок. Попробуй позже.");
            throw new UserError("ImgBB: " + (err ?? Short(text, (int)code)));
        }


        public static async Task<UploadResult> Imgur(string clientId, byte[] data, string fileName, string contentType)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new UserError(L.T("Для Imgur нужен Client ID (Настройки в меню значка Кадра). Без регистрации работает Catbox: стрелка ▾ рядом с кнопкой загрузки."));

            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(data);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            AddFile(form, file, contentType.StartsWith("video/") ? "video" : "image", fileName);
            var type = new StringContent("file");
            type.Headers.ContentType = null;
            type.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"type\"" };
            form.Add(type);

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.imgur.com/3/image") { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", clientId.Trim());

            string text; HttpStatusCode code;
            using (var resp = await Http.SendAsync(req))
            {
                code = resp.StatusCode;
                text = await resp.Content.ReadAsStringAsync();
            }
            object json = Json.TryParse(text);
            string link = json == null ? null : Json.Str(json, "data.link");
            if (!string.IsNullOrEmpty(link))
            {
                string hash = Json.Str(json, "data.deletehash");
                return new UploadResult { Url = link, ImgurDeleteHash = hash };
            }
            string err = json == null ? null : (Json.Str(json, "data.error.message") ?? Json.Str(json, "data.error") ?? Json.Str(json, "errors[0].detail"));
            if ((int)code == 429) err = L.T("Imgur просит подождать: слишком много загрузок. Попробуй через час.");
            else if ((int)code == 401 || (int)code == 403) err = L.F("Imgur не принял Client ID ({0}). Проверь его в настройках.", err ?? L.T("доступ запрещён"));
            throw new UserError("Imgur: " + (err ?? Short(text, (int)code)));
        }

        public static async Task ImgurDelete(string clientId, string deleteHash)
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, "https://api.imgur.com/3/image/" + Uri.EscapeDataString(deleteHash));
            req.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", (clientId ?? "").Trim());
            using (var resp = await Http.SendAsync(req))
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) throw new UserError("Imgur: " + Short(text, (int)resp.StatusCode));
            }
        }

        // ---------- Any HTTP service (ShareX-compatible settings) ----------

        public static async Task<UploadResult> Custom(CustomUploader u, byte[] data, string fileName, string contentType)
        {
            u.Normalize();
            if (string.IsNullOrWhiteSpace(u.RequestURL)) throw new UserError(L.F("У сервиса «{0}» не указан адрес.", u.Name));

            string url = Fill(u.RequestURL, fileName);
            if (u.Parameters.Count > 0)
            {
                string q = string.Join("&", u.Parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Fill(p.Value, fileName))));
                url += (url.Contains("?") ? "&" : "?") + q;
            }

            var method = new HttpMethod(u.RequestMethod.ToUpperInvariant());
            var req = new HttpRequestMessage(method, url);

            if (u.Body.Equals("Binary", StringComparison.OrdinalIgnoreCase))
            {
                var c = new ByteArrayContent(data);
                c.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                req.Content = c;
            }
            else if (u.Body.Equals("MultipartFormData", StringComparison.OrdinalIgnoreCase))
            {
                var form = new MultipartFormDataContent();
                foreach (var a in u.Arguments)
                {
                    var field = new StringContent(Fill(a.Value, fileName));
                    field.Headers.ContentType = null;
                    field.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + a.Key + "\"" };
                    form.Add(field);
                }
                var file = new ByteArrayContent(data);
                file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                AddFile(form, file, u.FileFormName, fileName);
                req.Content = form;
            }
            else throw new UserError(L.F("Тип тела «{0}» не поддерживается: нужен MultipartFormData или Binary.", u.Body));

            foreach (var h in u.Headers)
            {
                string v = Fill(h.Value, fileName);
                if (!req.Headers.TryAddWithoutValidation(h.Key, v) && req.Content != null)
                {
                    req.Content.Headers.Remove(h.Key);
                    req.Content.Headers.TryAddWithoutValidation(h.Key, v);
                }
            }

            string text; HttpResponseMessage resp;
            using (resp = await Http.SendAsync(req))
            {
                text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new UserError(u.Name + ": " + Short(text, (int)resp.StatusCode));
            }

            string link = string.IsNullOrWhiteSpace(u.URL) ? text.Trim() : Parse(u.URL, text);
            if (string.IsNullOrWhiteSpace(link) || !(link.StartsWith("http://") || link.StartsWith("https://")))
                throw new UserError(u.Name + ": " + L.F("в ответе не нашлась ссылка. Ответ сервера: {0}", Short(text, 0)));
            string del = string.IsNullOrWhiteSpace(u.DeletionURL) ? null : Parse(u.DeletionURL, text);
            return new UploadResult { Url = link.Trim(), DeleteUrl = string.IsNullOrWhiteSpace(del) ? null : del.Trim() };
        }

        /// <summary>Adds the file with a quoted, ASCII-only name: some servers choke on .NET's default unquoted or encoded names.</summary>
        static void AddFile(MultipartFormDataContent form, HttpContent file, string field, string fileName)
        {
            file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "\"" + field + "\"",
                FileName = "\"" + SafeName(fileName) + "\""
            };
            form.Add(file);
        }

        /// <summary>"Снимок 2026-11-22 14-30-05.png" → "snimok-2026-11-22-14-30-05.png".</summary>
        public static string SafeName(string fileName)
        {
            const string ru = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяієїґ";
            string[] lat = { "a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya","i","ie","i","g" };
            var sb = new StringBuilder();
            string plain = new string(fileName.Normalize(NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
                .Normalize(NormalizationForm.FormC).Replace("ß", "ss");
            foreach (char ch in plain.ToLowerInvariant())
            {
                int i = ru.IndexOf(ch);
                if (i >= 0) sb.Append(lat[i]);
                else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '-' || ch == '_') sb.Append(ch);
                else sb.Append('-');
            }
            string r = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (r.Length == 0 || r.StartsWith(".")) r = "image" + r;
            return r;
        }

        static string Fill(string s, string fileName)
        {
            if (s == null) return "";
            return s.Replace("{filename}", fileName).Replace("$filename$", fileName);
        }

        static readonly Regex Token = new Regex(@"\{(json|regex|response)(?::((?:[^{}]|\{\d*,?\d*\})*))?\}|\$(json|regex|response)(?::([^$]*))?\$", RegexOptions.Compiled);

        /// <summary>Fills a link template such as "{json:data.link}", "$json:files[0].url$", "{response}" or "{regex:https?://\S+}".</summary>
        public static string Parse(string template, string response)
        {
            object json = null; bool parsed = false;
            return Token.Replace(template, m =>
            {
                string kind = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
                string arg = m.Groups[1].Success ? m.Groups[2].Value : m.Groups[4].Value;
                switch (kind)
                {
                    case "response": return response.Trim();
                    case "json":
                        if (!parsed) { json = Json.TryParse(response); parsed = true; }
                        return json == null ? "" : (Json.Str(json, arg) ?? "");
                    case "regex":
                        string pattern = arg; int group = -1;
                        int bar = arg.LastIndexOf('|');
                        int g;
                        if (bar > 0 && int.TryParse(arg.Substring(bar + 1), out g)) { pattern = arg.Substring(0, bar); group = g; }
                        try
                        {
                            var mm = Regex.Match(response, pattern);
                            if (!mm.Success) return "";
                            if (group < 0) group = mm.Groups.Count > 1 ? 1 : 0;
                            return mm.Groups[group].Value;
                        }
                        catch { return ""; }
                }
                return m.Value;
            });
        }

        static string Short(string text, int code)
        {
            string t = Regex.Replace(text ?? "", "<[^>]+>", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            if (t.Length > 220) t = t.Substring(0, 220) + "…";
            if (code > 0) t = L.F("ошибка {0}", code) + (t.Length > 0 ? " — " + t : "");
            return t.Length == 0 ? L.T("пустой ответ сервера") : t;
        }
    }
}
