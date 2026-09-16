// =====================================================================
//  基础设施：路径 / 编码 / HTTP / 注入桥
// =====================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace KisstrTV
{
    /// <summary>诊断日志：写到 %TEMP%\KisstrTV\debug.log。用于定位播放/网络问题。</summary>
    internal static class Diag
    {
        private static readonly object _lock = new object();
        private static string _path;

        public static string Path_()
        {
            if (_path == null)
            {
                try
                {
                    string d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KisstrTV");
                    System.IO.Directory.CreateDirectory(d);
                    _path = System.IO.Path.Combine(d, "debug.log");
                }
                catch { _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KisstrTV-debug.log"); }
            }
            return _path;
        }

        /// <summary>
        /// 默认关闭。在 %APPDATA%\KisstrTV\ 下放一个名为 debug.on 的空文件即开启。
        /// （封面风暴时日志能到 1.6 MB / 30s，不能默认写。）
        /// </summary>
        private static bool? _on;

        public static bool On
        {
            get
            {
                if (_on == null)
                {
                    try { _on = System.IO.File.Exists(System.IO.Path.Combine(Store.Dir(), "debug.on")); }
                    catch { _on = false; }
                }
                return _on.Value;
            }
        }

        public static void Log(string msg)
        {
            if (!On) return;
            try
            {
                lock (_lock)
                {
                    using (System.IO.StreamWriter w = System.IO.File.AppendText(Path_()))
                    {
                        w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg);
                    }
                }
            }
            catch { }
        }

        public static void Clear()
        {
            if (!On) return;
            try { lock (_lock) { System.IO.File.WriteAllText(Path_(), "=== KisstrTV diag " + DateTime.Now.ToString("s") + " ===\n"); } }
            catch { }
        }
    }

    internal static class Util
    {
        public static string B64UrlEncode(string plain)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        public static string B64UrlDecode(string s)
        {
            string t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
        }

        public static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            char[] buf = new char[s.Length];
            int n = 0;
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) buf[n++] = c;
            }
            return new string(buf, 0, n).Trim();
        }
    }

    /// <summary>
    /// 请求头规则：与 Electron 版 main.js 的 setupBiliHeadersInjection() 完全等价。
    /// file:// 页面是 opaque origin，浏览器会剥掉 Referer，第三方 CDN 直接 403；
    /// 唯一可靠解是在宿主这一层补头（不设置 Response = 只补头、不改写响应）。
    /// </summary>
    internal static class Refs
    {
        public const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

        private static readonly string[] BiliHosts =
        {
            "bilivideo.com", "hdslb.com", "biliapi.net", "bilibili.com",
            "mcdn.bilivideo.cn", "biliapi.com", "bilivideo.cn"
        };

        private static readonly string[] NeteaseHosts =
        {
            "music.163.com", "music.126.net", "nosdn.127.net", "netease.im", "163cn.tv"
        };

        public static bool IsBili(string host)
        {
            return EndsWithAny(host, BiliHosts);
        }

        public static bool IsNetease(string host)
        {
            return EndsWithAny(host, NeteaseHosts);
        }

        private static bool EndsWithAny(string host, string[] suffixes)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.ToLowerInvariant();
            foreach (string suf in suffixes)
            {
                if (host.Length >= suf.Length &&
                    string.Compare(host, host.Length - suf.Length, suf, 0, suf.Length, StringComparison.Ordinal) == 0)
                    return true;
            }
            return false;
        }
    }

    internal sealed class Store
    {
        public static string Dir()
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KisstrTV");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        public static string Path_(string file)
        {
            return Path.Combine(Dir(), file);
        }

        /// <summary>
        /// 无 BOM 的 UTF-8。.NET 的 Encoding.UTF8 写文件时会带 BOM（EF BB BF），
        /// 而本项目的 vault.json 是「C# 宿主」与「Electron 宿主」共用同一份 ——
        /// Electron 侧的 JSON.parse 不认 BOM，会直接抛 "Unexpected token" 导致曲库读不出来。
        /// 所以落盘一律用不带 BOM 的编码，读取时再对历史 BOM 文件做兼容。
        /// </summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string ReadText(string file)
        {
            try
            {
                // File.ReadAllText(path, enc) 内部 detectEncodingFromByteOrderMarks=true，会自动吞掉 BOM
                string s = File.ReadAllText(Path_(file), Encoding.UTF8);
                if (!string.IsNullOrEmpty(s) && s[0] == '\uFEFF') s = s.Substring(1);   // 双保险
                return s;
            }
            catch { return null; }
        }

        public static void WriteText(string file, string content)
        {
            try { File.WriteAllText(Path_(file), content, Utf8NoBom); } catch { }
        }
    }

    internal sealed class HttpClient2
    {
        private readonly JavaScriptSerializer _js = new JavaScriptSerializer();

        static HttpClient2()
        {
            // 默认连接上限是 2，遇到页面批量拉封面会直接堵死整个进程
            try { System.Net.ServicePointManager.DefaultConnectionLimit = 32; } catch { }
            try { System.Net.ServicePointManager.Expect100Continue = false; } catch { }
            try { System.Net.ServicePointManager.MaxServicePointIdleTime = 30000; } catch { }
        }

        // ---------- 请求合并：同一 URL 并发时只发一次，其余等结果 ----------
        private sealed class Flight
        {
            public string Result;
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
        }

        private sealed class CacheItem { public DateTime At; public string Body; }

        private static readonly object _sync = new object();
        private static readonly Dictionary<string, Flight> _flights = new Dictionary<string, Flight>();
        private static readonly Dictionary<string, CacheItem> _cache = new Dictionary<string, CacheItem>();

        /// <summary>
        /// playurl 的直链有时效且带会话绑定，绝不能缓存；
        /// nav / account 是登录态，缓存会让「刚登录却显示未登录」。
        /// 其余（view / related / popular / search）都是元数据，缓存 5 分钟无副作用。
        /// </summary>
        private static bool Cacheable(string url)
        {
            if (url == null) return false;
            if (url.IndexOf("playurl", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (url.IndexOf("/x/web-interface/nav", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (url.IndexOf("account/get", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        /// <summary>GET 一个 B站 JSON 接口；失败返回 null。</summary>
        public object GetJson(string url, string referer, string cookie)
        {
            string body = GetRaw(url, referer, cookie);
            if (body == null) return null;
            try { return _js.DeserializeObject(body); }
            catch { return null; }
        }

        public string GetRaw(string url, string referer, string cookie)
        {
            if (Cacheable(url))
            {
                lock (_sync)
                {
                    CacheItem ci;
                    if (_cache.TryGetValue(url, out ci) &&
                        (DateTime.Now - ci.At).TotalSeconds < 300)
                        return ci.Body;
                }
            }

            // 断路器打开期间一律快速失败：不建连、不排队，避免继续撞风控
            if (Blocked) return null;

            Flight f;
            bool mine = false;
            lock (_sync)
            {
                if (!_flights.TryGetValue(url, out f))
                {
                    f = new Flight();
                    _flights[url] = f;
                    mine = true;
                }
            }

            if (!mine)
            {
                // 同 URL 已在飞行中：等它，别再发第二份
                try { f.Gate.Wait(20000); } catch { }
                return f.Result;
            }

            string res;
            try { res = GetRawCore(url, referer, cookie); }
            catch (Exception ex) { Diag.Log("HTTP EX " + Cut(url, 90) + " :: " + ex.Message); res = null; }

            if (res != null && Cacheable(url))
            {
                lock (_sync) { _cache[url] = new CacheItem { At = DateTime.Now, Body = res }; }
            }

            f.Result = res;
            try { f.Gate.Set(); } catch { }
            lock (_sync) { _flights.Remove(url); }
            return res;
        }

        private static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        // ---------- 断路器 + 节流 ----------
        // 页面的封面逻辑在失败时会按 2s 重排重试，失败越多久越滚雪球
        //（实测 12 → 144 → 1728 → 13824，四代就上万次）。一旦 B 站返回 412，
        // 若宿主不限流，几秒内就能把自己的 IP 打进风控。这里做三层保护：
        //   ① 请求合并（同 URL 只发一次） ② 最小间隔节流 ③ 412 断路器
        private static readonly object _rate = new object();
        private static long _lastReqTick = 0;
        private static int _consec412 = 0;
        private static int _blockUntilTick = 0;

        /// <summary>断路器是否打开（正在被风控，暂时不要发请求）。</summary>
        public static bool Blocked
        {
            get
            {
                if (_blockUntilTick == 0) return false;
                if (Environment.TickCount < _blockUntilTick) return true;
                _blockUntilTick = 0;
                _consec412 = 0;
                Diag.Log("CIRCUIT closed (cooldown over)");
                return false;
            }
        }

        /// <summary>冷却剩余秒数（未冷却时为 0）。用于给用户一个「还要等多久」的明确提示。</summary>
        public static int BlockSecondsLeft
        {
            get
            {
                if (_blockUntilTick == 0) return 0;
                int left = (int)((_blockUntilTick - Environment.TickCount) / 1000);
                return left > 0 ? left : 0;
            }
        }

        private static void Note412()
        {
            _consec412++;
            if (_consec412 >= 3)
            {
                int sec = Math.Min(600, 60 * (_consec412 - 2));   // 60 → 120 → 180 … 上限 10 分钟
                _blockUntilTick = Environment.TickCount + sec * 1000;
                Diag.Log("CIRCUIT open " + sec + "s  (consecutive 412 = " + _consec412 + ")");
            }
        }

        private static void NoteOk()
        {
            if (_consec412 != 0) { _consec412 = 0; _blockUntilTick = 0; }
        }

        /// <summary>礼貌节流：两次请求之间至少隔 MIN_GAP 毫秒。</summary>
        private static void Throttle()
        {
            const int MIN_GAP = 150;
            lock (_rate)
            {
                long now = Environment.TickCount;
                long wait = MIN_GAP - (now - _lastReqTick);
                if (wait > 0) Thread.Sleep((int)Math.Min(wait, 1000));
                _lastReqTick = Environment.TickCount;
            }
        }

        private string GetRawCore(string url, string referer, string cookie)
        {
            Throttle();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                HttpWebRequest req = null;
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.UserAgent = Refs.UA;
                    req.Accept = "application/json, text/plain, */*";
                    req.Timeout = 15000;
                    req.ReadWriteTimeout = 20000;
                    req.AllowAutoRedirect = true;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
                    if (!string.IsNullOrEmpty(cookie)) req.Headers.Set("Cookie", cookie);
                    req.Headers.Set("Accept-Language", "zh-CN,zh;q=0.9");

                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        NoteOk();
                        return sr.ReadToEnd();
                    }
                }
                catch (WebException we)
                {
                    HttpWebResponse r = we.Response as HttpWebResponse;
                    int st = r != null ? (int)r.StatusCode : 0;
                    Diag.Log("HTTP " + (st != 0 ? ("status " + st) : "noresp")
                             + "  " + Cut(url, 90) + " :: " + we.Message);
                    if (st == 412) { Note412(); return null; }   // 风控：立刻停，不要重试
                    if (attempt == 1) return null;
                }
                catch (Exception ex)
                {
                    Diag.Log("HTTP ERR " + Cut(url, 90) + " :: " + ex.Message);
                    return null;
                }
            }
            return null;
        }

        /// <summary>表单 POST（网易云部分接口需要）。</summary>
        public object PostForm(string url, string referer, string cookie, string body)
        {
            if (string.IsNullOrEmpty(body)) body = "";
            byte[] raw = Encoding.UTF8.GetBytes(body);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                HttpWebRequest req = null;
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "POST";
                    req.ContentType = "application/x-www-form-urlencoded";
                    req.ContentLength = raw.Length;
                    req.UserAgent = Refs.UA;
                    req.Accept = "application/json, text/plain, */*";
                    req.Timeout = 15000;
                    req.ReadWriteTimeout = 20000;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
                    if (!string.IsNullOrEmpty(cookie)) req.Headers.Set("Cookie", cookie);
                    req.Headers.Set("Accept-Language", "zh-CN,zh;q=0.9");

                    using (Stream os = req.GetRequestStream()) os.Write(raw, 0, raw.Length);
                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return _js.DeserializeObject(sr.ReadToEnd());
                }
                catch (WebException)
                {
                    if (attempt == 1) return null;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        public T Get<T>(string url, string referer, string cookie) where T : class
        {
            string body = GetRaw(url, referer, cookie);
            if (body == null) return null;
            try { return _js.Deserialize<T>(body); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 注入到页面最前面的桥：定义 window.audioAPI（33 个方法，含新增的 vaultRemove），
    /// 通过 chrome.webview.postMessage 与宿主通信，返回 Promise。
    /// 这样 index.html 里所有 window.audioAPI.xxx 的调用都无需改动。
    /// </summary>
    internal static class Bridge
    {
        public const string Js = @"(function(){
  if (window.audioAPI) return;
  var seq = 0, pending = {}, ch = window.chrome.webview;
  ch.addEventListener('message', function(ev){
    var m = ev.data;
    if (!m || m.__k === undefined) return;
    var cb = pending[m.__k];
    if (cb) { delete pending[m.__k]; cb(m); }
  });
  function call(name, args){
    return new Promise(function(resolve, reject){
      var id = ++seq;
      pending[id] = function(m){ if (m.error) reject(new Error(m.error)); else resolve(m.result); };
      try { ch.postMessage({ __k: id, m: name, a: args || [] }); }
      catch(e){ delete pending[id]; reject(e); }
    });
  }
  var M = ['winMin','winMax','winClose','winPin','setMini','toggleFullscreen','setFullscreen',
           'pickVideos','ready','winDrag','winBounds','winSetBounds','videoRatio',
           'biliStatus','biliLogin','biliLogout','biliResolve','biliSearch','biliPages',
           'biliView','biliResolveCid','setBiliQuality','biliQualityGet',
           'neteaseStatus','neteaseLogin','neteaseLogout','neteaseSearch','neteaseResolve',
           'vaultStats','vaultPull','vaultExplore','vaultPopular','vaultRemove'];
  var api = {};
  for (var i = 0; i < M.length; i++) {
    (function(n){ api[n] = function(){ return call(n, Array.prototype.slice.call(arguments)); }; })(M[i]);
  }
  var pauseCb = null;
  window.addEventListener('kisstr-pause-media', function(){ try { if (pauseCb) pauseCb(); } catch(e){} });
  api.onPauseMedia = function(cb){ pauseCb = cb; };
  window.audioAPI = api;
})();";
    }
}
