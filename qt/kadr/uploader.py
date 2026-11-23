"""Uploads: Catbox, Imgur and any HTTP service (ShareX .sxcu compatible)."""
import json
import re
import unicodedata
from urllib.parse import quote, urlparse

import requests

from . import i18n
from .i18n import T, F
from .settings import CATBOX, IMGUR

TIMEOUT = 120
UA = "Kadr/1.0"


class UserError(Exception):
    """Shown to the user as is."""


class Result:
    def __init__(self, url, delete_url=None, imgur_hash=None):
        self.url = url
        self.delete_url = delete_url
        self.imgur_hash = imgur_hash


def catbox_config() -> dict:
    return {"Name": CATBOX, "RequestMethod": "POST", "RequestURL": "https://catbox.moe/user/api.php",
            "Body": "MultipartFormData", "FileFormName": "fileToUpload", "Arguments": {"reqtype": "fileupload"},
            "Headers": {}, "Parameters": {}, "URL": "", "DeletionURL": ""}


def proxy_url(address: str):
    """'10.0.0.1:8080' -> 'http://10.0.0.1:8080'; None if unusable."""
    a = (address or "").strip()
    if not a:
        return None
    if "://" not in a:
        a = "http://" + a
    u = urlparse(a)
    if u.scheme not in ("http", "https") or not u.hostname:
        return None
    return a


def session(settings) -> requests.Session:
    s = requests.Session()
    s.headers["User-Agent"] = UA
    if settings.ProxyMode == "none":
        s.trust_env = False
        s.proxies = {}
    elif settings.ProxyMode == "custom":
        p = proxy_url(settings.ProxyAddress)
        if p:
            user = settings.ProxyUser
            if user:
                u = urlparse(p)
                p = f"{u.scheme}://{quote(user, safe='')}:{quote(settings.proxy_password(), safe='')}@{u.hostname}" + (f":{u.port}" if u.port else "")
            s.trust_env = False
            s.proxies = {"http": p, "https": p}
    # "system": requests reads the environment and, on macOS, the system proxy settings.
    return s


def safe_name(name: str) -> str:
    """'Снимок 2026-11-22 14-30-05.png' -> 'snimok-2026-11-22-14-30-05.png'."""
    ru = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяієїґ"
    lat = ["a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f",
           "h", "ts", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya", "i", "ie", "i", "g"]
    plain = unicodedata.normalize("NFD", name)
    plain = "".join(c for c in plain if unicodedata.category(c) != "Mn").replace("ß", "ss")
    out = []
    for ch in unicodedata.normalize("NFC", plain).lower():
        i = ru.find(ch)
        if i >= 0:
            out.append(lat[i])
        elif ch.isascii() and (ch.isalnum() or ch in ".-_"):
            out.append(ch)
        else:
            out.append("-")
    r = re.sub("-{2,}", "-", "".join(out)).strip("-")
    if not r or r.startswith("."):
        r = "image" + r
    return r


def content_type(name: str) -> str:
    n = name.lower()
    for ext, ct in ((".jpg", "image/jpeg"), (".jpeg", "image/jpeg"), (".gif", "image/gif"), (".webp", "image/webp"),
                    (".bmp", "image/bmp"), (".tif", "image/tiff"), (".tiff", "image/tiff"), (".mp4", "video/mp4")):
        if n.endswith(ext):
            return ct
    return "image/png"


def _short(text: str, code: int = 0) -> str:
    t = re.sub(r"<[^>]+>", " ", text or "")
    t = re.sub(r"\s+", " ", t).strip()
    if len(t) > 220:
        t = t[:220] + "…"
    if code:
        t = F("ошибка {0}", code) + (" — " + t if t else "")
    return t or T("пустой ответ сервера")


def upload(settings, destination: str, data: bytes, file_name: str) -> Result:
    if destination == IMGUR:
        return imgur(settings, data, file_name)
    if destination == CATBOX:
        return custom(settings, catbox_config(), data, file_name)
    for u in settings.Uploaders:
        if u["Name"] == destination:
            return custom(settings, u, data, file_name)
    raise UserError(F("Сервис «{0}» не найден в настройках.", destination))


def imgur(settings, data: bytes, file_name: str) -> Result:
    cid = (settings.ImgurClientId or "").strip()
    if not cid:
        raise UserError(T("Для Imgur нужен Client ID (Настройки в меню значка Кадра). Без регистрации работает Catbox: стрелка ▾ рядом с кнопкой загрузки."))
    ct = content_type(file_name)
    field = "video" if ct.startswith("video/") else "image"
    r = session(settings).post("https://api.imgur.com/3/image", headers={"Authorization": "Client-ID " + cid},
                               files={field: (safe_name(file_name), data, ct)}, data={"type": "file"}, timeout=TIMEOUT)
    try:
        j = r.json()
    except Exception:
        j = None
    link = json_path(j, "data.link") if j else None
    if link:
        return Result(link, imgur_hash=json_path(j, "data.deletehash"))
    err = None
    if j:
        err = json_path(j, "data.error.message") or json_path(j, "data.error") or json_path(j, "errors[0].detail")
    if r.status_code == 429:
        err = T("Imgur просит подождать: слишком много загрузок. Попробуй через час.")
    elif r.status_code in (401, 403):
        err = F("Imgur не принял Client ID ({0}). Проверь его в настройках.", err or T("доступ запрещён"))
    raise UserError("Imgur: " + (str(err) if err else _short(r.text, r.status_code)))


def imgur_delete(settings, delete_hash: str):
    r = session(settings).delete("https://api.imgur.com/3/image/" + quote(delete_hash, safe=""),
                                 headers={"Authorization": "Client-ID " + (settings.ImgurClientId or "").strip()}, timeout=TIMEOUT)
    if not r.ok:
        raise UserError("Imgur: " + _short(r.text, r.status_code))


def _fill(s, file_name):
    return (s or "").replace("{filename}", file_name).replace("$filename$", file_name)


def custom(settings, u: dict, data: bytes, file_name: str) -> Result:
    name = u.get("Name") or "?"
    url = _fill(u.get("RequestURL"), file_name).strip()
    if not url:
        raise UserError(F("У сервиса «{0}» не указан адрес.", name))
    params = {k: _fill(v, file_name) for k, v in (u.get("Parameters") or {}).items()}
    headers = {k: _fill(v, file_name) for k, v in (u.get("Headers") or {}).items()}
    method = (u.get("RequestMethod") or "POST").upper()
    body = (u.get("Body") or "MultipartFormData")
    ct = content_type(file_name)
    s = session(settings)
    if body.lower() == "binary":
        headers.setdefault("Content-Type", ct)
        r = s.request(method, url, params=params, headers=headers, data=data, timeout=TIMEOUT)
    elif body.lower() == "multipartformdata":
        fields = {k: _fill(v, file_name) for k, v in (u.get("Arguments") or {}).items()}
        files = {u.get("FileFormName") or "file": (safe_name(file_name), data, ct)}
        r = s.request(method, url, params=params, headers=headers, data=fields, files=files, timeout=TIMEOUT)
    else:
        raise UserError(F("Тип тела «{0}» не поддерживается: нужен MultipartFormData или Binary.", body))
    text = r.text
    if not r.ok:
        raise UserError(name + ": " + _short(text, r.status_code))
    link = text.strip() if not (u.get("URL") or "").strip() else parse_template(u["URL"], text)
    if not link or not link.startswith(("http://", "https://")):
        raise UserError(name + ": " + F("в ответе не нашлась ссылка. Ответ сервера: {0}", _short(text)))
    dele = parse_template(u["DeletionURL"], text).strip() if (u.get("DeletionURL") or "").strip() else None
    return Result(link.strip(), delete_url=dele or None)


# ---------------- templates: {json:data.link}, $json:files[0].url$, {response}, {regex:...} ----------------

_TOKEN = re.compile(r"\{(json|regex|response)(?::((?:[^{}]|\{\d*,?\d*\})*))?\}|\$(json|regex|response)(?::([^$]*))?\$")


def parse_template(template: str, response: str) -> str:
    cache = {}

    def repl(m):
        kind = m.group(1) or m.group(3)
        arg = m.group(2) if m.group(1) else m.group(4)
        arg = arg or ""
        if kind == "response":
            return response.strip()
        if kind == "json":
            if "j" not in cache:
                try:
                    cache["j"] = json.loads(response)
                except Exception:
                    cache["j"] = None
            v = json_path(cache["j"], arg) if cache["j"] is not None else None
            return "" if v is None else (v if isinstance(v, str) else json.dumps(v))
        if kind == "regex":
            pattern, group = arg, None
            if "|" in arg:
                head, tail = arg.rsplit("|", 1)
                if tail.isdigit():
                    pattern, group = head, int(tail)
            try:
                mm = re.search(pattern, response)
            except re.error:
                return ""
            if not mm:
                return ""
            if group is None:
                group = 1 if mm.re.groups else 0
            return mm.group(group) or ""
        return m.group(0)

    return _TOKEN.sub(repl, template)


def json_path(root, path: str):
    cur = root
    for part in (path or "").split("."):
        part = part.strip()
        if not part:
            continue
        name = part.split("[", 1)[0]
        idxs = [int(x) for x in re.findall(r"\[(\d+)\]", part)]
        if name:
            if not isinstance(cur, dict) or name not in cur:
                return None
            cur = cur[name]
        for i in idxs:
            if not isinstance(cur, list) or i >= len(cur):
                return None
            cur = cur[i]
    if isinstance(cur, (int, float)) and not isinstance(cur, bool):
        return str(cur)
    return cur
