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
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Kadr/1.0 (Windows)");
            return c;
        }

        public static Task<UploadResult> Upload(Settings s, string destination, byte[] data, string fileName, string contentType)
        {
            if (destination == Settings.Imgur) return Imgur(s.ImgurClientId, data, fileName, contentType);
            var u = s.Uploaders.FirstOrDefault(x => x.Name == destination);
            if (u == null) throw new UserError("Сервис «" + destination + "» не найден в настройках.");
            return Custom(u, data, fileName, contentType);
        }

        // ---------- Imgur ----------

        public static async Task<UploadResult> Imgur(string clientId, byte[] data, string fileName, string contentType)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new UserError("Для Imgur нужен Client ID. Открой «Настройки» в меню значка Кадра.");

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
            if ((int)code == 429) err = "Imgur просит подождать: слишком много загрузок. Попробуй через час.";
            else if ((int)code == 401 || (int)code == 403) err = "Imgur не принял Client ID (" + (err ?? "доступ запрещён") + "). Проверь его в настройках.";
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
            if (string.IsNullOrWhiteSpace(u.RequestURL)) throw new UserError("У сервиса «" + u.Name + "» не указан адрес.");

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
            else throw new UserError("Тип тела «" + u.Body + "» не поддерживается: нужен MultipartFormData или Binary.");

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
                throw new UserError(u.Name + ": в ответе не нашлась ссылка. Ответ сервера: " + Short(text, 0));
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
            foreach (char ch in fileName.ToLowerInvariant())
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
            if (code > 0) t = "ошибка " + code + (t.Length > 0 ? " — " + t : "");
            return t.Length == 0 ? "пустой ответ сервера" : t;
        }
    }
}
