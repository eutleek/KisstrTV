// =====================================================================
//  服务层：B站 / 网易云 / 曲库 / 登录窗口
//
//  与 Electron 版的三点差异（都是改进）：
//   1) 网易云不再需要「隐藏窗口 + executeJavaScript 同源 fetch」——
//      登录时把 cookie 落盘，之后一律走 HttpWebRequest + Referer。
//      省掉一个常驻隐藏 WebView2，也顺带根治了原版
//      window-all-closed 永不触发导致进程残留的老问题（档案 v28.2/v28.3）。
//   2) 所有 API 请求在宿主侧发起，不经过页面，绕开了 file:// opaque origin
//      的 CORS 限制。
//   3) 登录窗口用独立 UserDataFolder 的 WebView2，登录完立即释放。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KisstrTV
{
    internal static class Json
    {
        public static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        /// <summary>
        /// JavaScriptSerializer 把 JSON 数组反序列化成 ArrayList，不是 List&lt;object&gt;。
        /// 直接 `as List&lt;object&gt;` 会静默返回 null —— 这会让 JS 调用参数、
        /// playurl 的 durl 直链、曲库 items 全部丢失（页面表现为黑屏）。
        /// 这里统一按 IEnumerable 收。
        /// </summary>
        public static List<object> AsList(object o)
        {
            if (o == null) return null;

            List<object> l = o as List<object>;
            if (l != null) return l;

            if (o is string) return null;
            if (o is System.Collections.IDictionary) return null;

            System.Collections.IEnumerable en = o as System.Collections.IEnumerable;
            if (en != null)
            {
                List<object> r = new List<object>();
                foreach (object x in en) r.Add(x);
                return r;
            }
            return null;
        }

        public static object Get(object o, string key)
        {
            Dictionary<string, object> d = AsDict(o);
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        // DeserializeObject 对 JSON number 给出 int/long/decimal，这里统一成 double
        public static double Num(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToDouble(o); } catch { return 0; }
        }

        public static long Int(object o)
        {
            return (long)Math.Round(Num(o));
        }

        public static string Str(object o)
        {
            return o == null ? "" : Convert.ToString(o);
        }
    }

    internal static class Bili
    {
        public const string Referer = "https://www.bilibili.com/";
        public const string LoginUrl = "https://passport.bilibili.com/login";

        public const string ApiView    = "https://api.bilibili.com/x/web-interface/view";
        public const string ApiPlay    = "https://api.bilibili.com/x/player/playurl";
        public const string ApiNav     = "https://api.bilibili.com/x/web-interface/nav";
        public const string ApiSearch  = "https://api.bilibili.com/x/web-interface/search/type";
        public const string ApiRelated = "https://api.bilibili.com/x/web-interface/archive/related";
        public const string ApiPopular = "https://api.bilibili.com/x/web-interface/popular";

        public static string Cookie = "";
        private static int _qn = 0;
        private static Dictionary<string, object> _user = null;

        public static int Quality { get { return _qn; } }

        static Bili()
        {
            string saved = Store.ReadText("bili_cookies.json");
            if (!string.IsNullOrEmpty(saved))
            {
                try
                {
                    Dictionary<string, object> d = Json.AsDict(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(saved));
                    if (d != null)
                    {
                        Cookie = Json.Str(Json.Get(d, "cookie"));
                        _user = Json.AsDict(Json.Get(d, "user"));
                    }
                }
                catch { }
            }
            try
            {
                string q = Store.ReadText("bili_qual.txt");
                if (q != null) _qn = (int)Math.Round(double.Parse(q.Trim()));
            }
            catch { }
            if (_qn != 0 && _qn != 16 && _qn != 32 && _qn != 64 && _qn != 80) _qn = 0;
        }

        public static bool IsLoginCookie(IReadOnlyList<CoreWebView2Cookie> cs)
        {
            foreach (CoreWebView2Cookie c in cs)
                if (c.Name == "SESSDATA" && !string.IsNullOrEmpty(c.Value)) return true;
            return false;
        }

        /// <summary>登录成功后把 WebView2 会话里的 cookie 吸收进来，供 HttpClient 使用。</summary>
        public static void Absorb(IReadOnlyList<CoreWebView2Cookie> cs)
        {
            StringBuilder sb = new StringBuilder();
            foreach (CoreWebView2Cookie c in cs)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(c.Name).Append('=').Append(c.Value);
            }
            Cookie = sb.ToString();
            Save();
        }

        private static void Save()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["cookie"] = Cookie;
            d["user"] = _user;
            try
            {
                Store.WriteText("bili_cookies.json",
                    new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }

        public static object Logout()
        {
            Cookie = ""; _user = null; Save();
            return true;
        }

        public static void SetQuality(int qn)
        {
            _qn = (qn == 0 || qn == 16 || qn == 32 || qn == 64 || qn == 80) ? qn : 0;
            Store.WriteText("bili_qual.txt", _qn.ToString());
        }

        private static int[] QnOrder()
        {
            if (_qn > 0)
            {
                List<int> l = new List<int>();
                foreach (int q in new int[] { _qn, 80, 64, 32 }) if (!l.Contains(q)) l.Add(q);
                return l.ToArray();
            }
            return string.IsNullOrEmpty(Cookie)
                ? new int[] { 32, 64, 16 }
                : new int[] { 80, 64, 32 };
        }

        /// <summary>
        /// 拿不到结果时（网络失败 / 被限流 / 断路器打开）一律保留登录态，绝不自动登出。
        /// 老逻辑会在限流期把 cookie 清掉 → 页面显示未登录 → 用户反复重登，
        /// 每登一次就等于往风控上再撞一次，这才是真正会连累账号的行为。
        /// </summary>
        private static object KeepLogin()
        {
            if (_user != null && _user.ContainsKey("name"))
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["loggedIn"] = true;
                r["name"] = _user["name"];
                r["level"] = _user.ContainsKey("level") ? _user["level"] : 0;
                r["face"] = _user.ContainsKey("face") ? _user["face"] : "";
                r["stale"] = true;   // 缓存值，页面别据此切换清晰度
                return r;
            }
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["loggedIn"] = !string.IsNullOrEmpty(Cookie);
            p["stale"] = true;
            return p;
        }

        public static object Status(HttpClient2 http)
        {
            if (string.IsNullOrEmpty(Cookie)) return Plain(false);

            // 冷却期不探活：直接信本地缓存，不要带着账号去撞风控
            if (HttpClient2.Blocked) return KeepLogin();

            object o = http.GetJson(ApiNav, Referer, Cookie);
            if (o == null) return KeepLogin();                       // 传输失败 ≠ 凭据失效

            double code = Json.Num(Json.Get(o, "code"));
            if (code != 0) return KeepLogin();                       // -509 频繁 / -412 拦截 都不是登出

            Dictionary<string, object> data = Json.AsDict(Json.Get(o, "data"));
            if (data == null) return KeepLogin();                    // 空响应当网络问题处理

            // 只有「成功返回且明确说未登录」才是真登出，这时才清
            if (Json.Num(Json.Get(data, "isLogin")) == 0)
            {
                Diag.Log("bili: nav says not logged in -> clear cookie");
                Cookie = ""; _user = null; Save();
                return Plain(false);
            }

            Dictionary<string, object> li = Json.AsDict(Json.Get(data, "level_info"));
            Dictionary<string, object> u = new Dictionary<string, object>();
            u["name"] = Json.Str(Json.Get(data, "uname"));
            u["face"] = Json.Str(Json.Get(data, "face"));
            u["level"] = (int)Json.Num(Json.Get(li, "current_level"));
            _user = u;
            Save();

            Dictionary<string, object> r = new Dictionary<string, object>();
            r["loggedIn"] = true;
            r["name"] = u["name"];
            r["level"] = u["level"];
            r["face"] = u["face"];
            return r;
        }

        private static Dictionary<string, object> Plain(bool loggedIn)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["loggedIn"] = loggedIn;
            return r;
        }

        private static object Fail(string msg)
        {
            return Fail(msg, false);
        }

        /// <summary>
        /// net=true 表示失败发生在网络/风控层（412 限流、断网、超时、断路器打开、清晰度受限），
        /// 而不是这条视频本身失效。UI 收到 net 标记时只跳过、绝不从曲库删除 ——
        /// 否则一次 412 限流就能把整个曲库删空。
        /// 只有 net=false（服务端明确回答「这个稿件不存在/不可播」）才允许真删。
        /// </summary>
        private static object Fail(string msg, bool net)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = false;
            r["msg"] = msg;
            if (net) r["net"] = true;
            return r;
        }

        public static object View(HttpClient2 http, string bvid)
        {
            object o = http.GetJson(ApiView + "?bvid=" + Uri.EscapeDataString(bvid), Referer, Cookie);
            Dictionary<string, object> data = Json.AsDict(Json.Get(o, "data"));

            if (o == null || Json.Num(Json.Get(o, "code")) != 0 || data == null)
            {
                // 拿不到元数据时的兜底：
                //   ① 有历史缓存 → 用缓存封面（页面只认 ok && pic）
                //   ② 正被风控 → 返回占位图，让页面停止 2s 一轮的重试雪崩
                string cached = CoverGet(bvid);
                if (!string.IsNullOrEmpty(cached)) return Ok(bvid, cached, "", "");
                if (HttpClient2.Blocked) return Ok(bvid, PlaceholderPic, "", "");
                string msg = o == null ? "" : Json.Str(Json.Get(o, "message"));
                return Fail(string.IsNullOrEmpty(msg) ? "视频不存在" : msg);
            }

            Dictionary<string, object> owner = Json.AsDict(Json.Get(data, "owner"));
            string pic = Json.Str(Json.Get(data, "pic"));
            if (!string.IsNullOrEmpty(pic)) CoverPut(bvid, pic);
            return Ok(bvid, pic,
                Util.StripTags(Json.Str(Json.Get(data, "title"))),
                Json.Str(Json.Get(owner, "name")));
        }

        private static Dictionary<string, object> Ok(string bvid, string pic, string title, string author)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true;
            r["bvid"] = bvid;
            r["pic"] = pic;
            r["title"] = title;
            r["author"] = author;
            return r;
        }

        /// <summary>断路期间用的占位封面：纯色圆点，避免页面判定为失败而无限重试。</summary>
        private const string PlaceholderPic =
            "data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%22320%22 height=%22180%22%3E" +
            "%3Crect width=%22320%22 height=%22180%22 fill=%22%231c1c21%22/%3E" +
            "%3Ccircle cx=%22160%22 cy=%2290%22 r=%2232%22 fill=%22none%22 stroke=%22%234a4a52%22 stroke-width=%223%22/%3E" +
            "%3C/svg%3E";

        // ---------- 封面缓存（bvid → pic 落盘，下次启动不再打接口）----------
        private static readonly Dictionary<string, string> _covers = new Dictionary<string, string>();
        private static bool _coversDirty;

        static void LoadCovers()
        {
            try
            {
                string txt = Store.ReadText("bili_covers.json");
                if (string.IsNullOrEmpty(txt)) return;
                object o = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(txt);
                Dictionary<string, object> d = Json.AsDict(o);
                if (d == null) return;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    string v = kv.Value == null ? "" : Convert.ToString(kv.Value);
                    if (!string.IsNullOrEmpty(v)) _covers[kv.Key] = v;
                }
            }
            catch { }
        }

        public static string CoverGet(string bvid)
        {
            if (_covers.Count == 0) LoadCovers();
            string v;
            return _covers.TryGetValue(bvid, out v) ? v : "";
        }

        private static void CoverPut(string bvid, string pic)
        {
            if (string.IsNullOrEmpty(bvid) || string.IsNullOrEmpty(pic)) return;
            if (_covers.Count == 0) LoadCovers();
            if (_covers.ContainsKey(bvid)) return;
            _covers[bvid] = pic;
            _coversDirty = true;
            SaveCovers();
        }

        public static void SaveCovers()
        {
            if (!_coversDirty) return;
            _coversDirty = false;
            try
            {
                Store.WriteText("bili_covers.json",
                    new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(_covers));
            }
            catch { }
        }

        public static object Pages(HttpClient2 http, string bvid)
        {
            object o = http.GetJson(ApiView + "?bvid=" + Uri.EscapeDataString(bvid), Referer, Cookie);
            Dictionary<string, object> data = Json.AsDict(Json.Get(o, "data"));
            if (o == null || Json.Num(Json.Get(o, "code")) != 0 || data == null)
                return Fail(Json.Str(Json.Get(o, "message")) ?? "视频不存在");

            List<object> pages = new List<object>();
            List<object> src = Json.AsList(Json.Get(data, "pages"));
            if (src != null)
            {
                for (int i = 0; i < src.Count; i++)
                {
                    string part = Util.StripTags(Json.Str(Json.Get(src[i], "part")));
                    Dictionary<string, object> p = new Dictionary<string, object>();
                    p["p"] = i + 1;
                    p["cid"] = Json.Int(Json.Get(src[i], "cid"));
                    p["part"] = string.IsNullOrEmpty(part) ? ("第" + (i + 1) + "P") : part;
                    p["dur"] = Json.Int(Json.Get(src[i], "duration"));
                    pages.Add(p);
                }
            }

            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true;
            r["title"] = Util.StripTags(Json.Str(Json.Get(data, "title")));
            r["pages"] = pages;
            return r;
        }

        public static object Resolve(HttpClient2 http, string bvid)
        {
            object o = http.GetJson(ApiView + "?bvid=" + Uri.EscapeDataString(bvid), Referer, Cookie);
            // o == null = 请求根本没成功（412 限流 / 断网 / 断路器打开）。这是暂时性的，不能算源失效。
            if (o == null) return Fail("网络受限或超时，本条保留待重试", true);
            // code != 0 = 服务端明确回答稿件不存在/不可见 —— 这才是真正的源失效
            if (Json.Num(Json.Get(o, "code")) != 0) return Fail("视频不存在或不可播放");
            Dictionary<string, object> data = Json.AsDict(Json.Get(o, "data"));
            if (data == null) return Fail("接口返回结构异常，本条保留", true);

            List<object> pg = Json.AsList(Json.Get(data, "pages"));
            long cid = (pg != null && pg.Count > 0) ? Json.Int(Json.Get(pg[0], "cid")) : 0;
            if (cid == 0) return Fail("无法获取 cid，本条保留", true);

            foreach (int qn in QnOrder())
            {
                string url = ApiPlay + "?bvid=" + Uri.EscapeDataString(bvid) + "&cid=" + cid +
                             "&qn=" + qn + "&fnval=0&fnver=0";
                object p = http.GetJson(url, Referer, Cookie);
                Dictionary<string, object> pd = Json.AsDict(Json.Get(p, "data"));
                List<object> durl = pd != null ? Json.AsList(Json.Get(pd, "durl")) : null;
                if (p != null && Json.Num(Json.Get(p, "code")) == 0 && durl != null && durl.Count > 0)
                {
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["ok"] = true;
                    r["url"] = Json.Str(Json.Get(durl[0], "url"));
                    r["title"] = Util.StripTags(Json.Str(Json.Get(data, "title")));
                    r["dur"] = Json.Int(Json.Get(data, "duration"));
                    r["cid"] = cid;
                    r["qn"] = qn;
                    return r;
                }
            }
            // 清晰度受限 = 权限/登录问题，与源是否有效无关，绝不能据此删库
            return Fail(string.IsNullOrEmpty(Cookie)
                ? "未登录时仅提供低清晰度，建议登录B站获取高清"
                : "该视频清晰度受限", true);
        }

        public static object ResolveCid(HttpClient2 http, string bvid, long cid, string part)
        {
            if (cid == 0) return Fail("cid 无效", true);
            foreach (int qn in QnOrder())
            {
                string url = ApiPlay + "?bvid=" + Uri.EscapeDataString(bvid) + "&cid=" + cid +
                             "&qn=" + qn + "&fnval=0&fnver=0";
                object p = http.GetJson(url, Referer, Cookie);
                Dictionary<string, object> pd = Json.AsDict(Json.Get(p, "data"));
                List<object> durl = pd != null ? Json.AsList(Json.Get(pd, "durl")) : null;
                if (p != null && Json.Num(Json.Get(p, "code")) == 0 && durl != null && durl.Count > 0)
                {
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["ok"] = true;
                    r["url"] = Json.Str(Json.Get(durl[0], "url"));
                    r["part"] = part ?? "";
                    r["qn"] = qn;
                    return r;
                }
            }
            return Fail(string.IsNullOrEmpty(Cookie) ? "未登录时仅提供低清晰度" : "该分P清晰度受限", true);
        }

        public static object Search(HttpClient2 http, string keyword, int page)
        {
            int pn = Math.Max(1, page);
            string url = ApiSearch + "?search_type=video&keyword=" + Uri.EscapeDataString(keyword) +
                         "&ps=50&pn=" + pn;
            object o = http.GetJson(url, Referer, Cookie);
            if (o == null || Json.Num(Json.Get(o, "code")) != 0)
                return Fail("搜索失败：" + (Json.Str(Json.Get(o, "message")) ?? "请稍后再试或先登录"));

            List<object> list = new List<object>();
            List<object> src = Json.AsList(Json.Get(Json.Get(o, "data"), "result"));
            if (src != null)
            {
                foreach (object it in src)
                {
                    string bvid = Json.Str(Json.Get(it, "bvid"));
                    if (string.IsNullOrEmpty(bvid)) continue;
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["bvid"] = bvid;
                    r["title"] = Util.StripTags(Json.Str(Json.Get(it, "title")));
                    r["duration"] = Json.Str(Json.Get(it, "duration"));
                    r["author"] = Json.Str(Json.Get(it, "author"));
                    r["play"] = Json.Int(Json.Get(it, "play"));
                    list.Add(r);
                }
            }

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ok"] = true;
            res["list"] = list;
            return res;
        }

        /// <summary>相关视频链：匿名稳定，曲库扩散的主力来源。</summary>
        public static List<object> Related(HttpClient2 http, string bvid)
        {
            List<object> outList = new List<object>();
            object o = http.GetJson(ApiRelated + "?bvid=" + Uri.EscapeDataString(bvid), Referer, Cookie);
            if (o == null || Json.Num(Json.Get(o, "code")) != 0) return outList;
            List<object> src = Json.AsList(Json.Get(o, "data"));
            if (src == null) return outList;

            foreach (object it in src)
            {
                string id = Json.Str(Json.Get(it, "bvid"));
                string title = Util.StripTags(Json.Str(Json.Get(it, "title")));
                double dur = Json.Num(Json.Get(it, "duration"));
                string tname = Json.Str(Json.Get(it, "tname"));
                if (string.IsNullOrEmpty(id)) continue;
                if (!Vault.IsMusicLike(tname, title)) continue;
                if (dur < 45 || dur > 1200) continue;

                string author = Json.Str(Json.Get(it, "author"));
                int sp = author.IndexOf('"');
                if (sp >= 0) author = author.Substring(0, sp);

                Dictionary<string, object> r = new Dictionary<string, object>();
                r["bvid"] = id;
                r["title"] = title;
                r["author"] = author;
                r["dur"] = (int)dur;
                outList.Add(r);
            }
            return outList;
        }
    }

    internal static class Netease
    {
        public const string Referer = "https://music.163.com/";
        public const string LoginUrl = "https://music.163.com/#/login";
        private const string ApiProfile = "https://music.163.com/api/nuser/account/get";
        private const string ApiSearch  = "https://music.163.com/api/search/get/web";
        private const string ApiMvDetail = "https://music.163.com/api/mv/detail";

        public static string Cookie = "";
        private static Dictionary<string, object> _user = null;

        static Netease()
        {
            Cookie = Store.ReadText("netease_cookies.json") ?? "";
            // 兼容旧结构 {"cookie": "..."}
            if (Cookie.TrimStart().StartsWith("{"))
            {
                try
                {
                    Dictionary<string, object> d = Json.AsDict(
                        new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Cookie));
                    if (d != null) Cookie = Json.Str(Json.Get(d, "cookie")) ?? "";
                }
                catch { Cookie = ""; }
            }
        }

        public static bool IsLoginCookie(IReadOnlyList<CoreWebView2Cookie> cs)
        {
            foreach (CoreWebView2Cookie c in cs)
                if (c.Name == "MUSIC_U" && !string.IsNullOrEmpty(c.Value)) return true;
            return false;
        }

        public static void Absorb(IReadOnlyList<CoreWebView2Cookie> cs)
        {
            StringBuilder sb = new StringBuilder();
            foreach (CoreWebView2Cookie c in cs)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(c.Name).Append('=').Append(c.Value);
            }
            Cookie = sb.ToString();
            _user = null;
            Store.WriteText("netease_cookies.json", Cookie);
        }

        public static object Logout()
        {
            Cookie = ""; _user = null;
            Store.WriteText("netease_cookies.json", "");
            return true;
        }

        private static string Csrf()
        {
            foreach (string part in Cookie.Split(';'))
            {
                string t = part.Trim();
                if (t.StartsWith("__csrf=", StringComparison.Ordinal)) return t.Substring(7);
            }
            return "";
        }

        public static object Status(HttpClient2 http)
        {
            if (string.IsNullOrEmpty(Cookie)) return No();
            object o = http.GetJson(ApiProfile, Referer, Cookie);
            if (o == null) return No();   // 传输失败 ≠ 凭据失效，不要清 cookie

            Dictionary<string, object> profile = Json.AsDict(Json.Get(o, "profile"));
            if (profile == null) return No();

            _user = new Dictionary<string, object>();
            string face = Json.Str(Json.Get(profile, "avatarUrl"));
            if (string.IsNullOrEmpty(face))
            {
                Dictionary<string, object> det = Json.AsDict(Json.Get(profile, "avatarDetail"));
                face = Json.Str(Json.Get(det, "url"));
            }
            _user["name"] = Json.Str(Json.Get(profile, "nickname"));
            _user["face"] = face;

            Dictionary<string, object> r = new Dictionary<string, object>();
            r["loggedIn"] = true;
            r["name"] = _user["name"];
            r["face"] = _user["face"];
            return r;
        }

        private static Dictionary<string, object> No()
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["loggedIn"] = false;
            return r;
        }

        public static object Search(HttpClient2 http, string kw)
        {
            if (string.IsNullOrEmpty(Cookie))
                return Mes("请先登录网易云音乐");

            string body = "s=" + Uri.EscapeDataString(kw) +
                          "&type=1004&offset=0&total=true&limit=15&csrf_token=" + Csrf();
            object o = http.PostForm(ApiSearch, Referer, Cookie, body);
            if (o == null) return Mes("搜索失败：网络错误");
            if (Json.Num(Json.Get(o, "code")) != 200)
                return Mes("搜索失败（" + Json.Str(Json.Get(o, "code")) + "），请先登录网易云");

            List<object> list = new List<object>();
            List<object> mvs = Json.AsList(Json.Get(Json.Get(o, "result"), "mvs"));
            if (mvs != null)
            {
                int n = Math.Min(mvs.Count, 12);
                for (int i = 0; i < n; i++)
                {
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["id"] = Json.Int(Json.Get(mvs[i], "id"));
                    r["name"] = Json.Str(Json.Get(mvs[i], "name"));
                    r["artist"] = Json.Str(Json.Get(mvs[i], "artistName"));
                    r["play"] = Json.Int(Json.Get(mvs[i], "playCount"));
                    r["dur"] = Json.Int(Json.Get(mvs[i], "duration"));
                    list.Add(r);
                }
            }

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ok"] = true;
            res["list"] = list;
            return res;
        }

        public static object Resolve(HttpClient2 http, string mid)
        {
            if (string.IsNullOrEmpty(Cookie)) return Mes("请先登录网易云音乐", true);
            string url = ApiMvDetail + "?id=" + Uri.EscapeDataString(mid) + "&type=mp4";
            object o = http.GetJson(url, Referer, Cookie);
            if (o == null) return Mes("MV解析失败：网络错误", true);
            if (Json.Num(Json.Get(o, "code")) != 200) return Mes("MV解析失败（可能版权受限或未登录）", true);

            Dictionary<string, object> data = Json.AsDict(Json.Get(o, "data"));
            Dictionary<string, object> brs = Json.AsDict(Json.Get(data, "brs"));
            string pick = null;
            foreach (string br in new string[] { "1080", "720", "480", "240" })
            {
                string v = Json.Str(Json.Get(brs, br));
                if (!string.IsNullOrEmpty(v)) { pick = v; break; }
            }
            if (pick == null) return Mes("未找到可播放的 MV 地址", true);

            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true;
            r["url"] = pick;
            r["title"] = Json.Str(Json.Get(data, "name"));
            return r;
        }

        private static Dictionary<string, object> Mes(string m)
        {
            return Mes(m, false);
        }

        /// <summary>net=true：暂时性失败（限流/断网/未登录/版权受限），UI 只跳过不删。语义同 Bili.Fail。</summary>
        private static Dictionary<string, object> Mes(string m, bool net)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = false;
            r["msg"] = m;
            if (net) r["net"] = true;
            return r;
        }
    }

#if KISSTR_VAULT
    // 真实曲库实现在 vault.impl.cs（私有文件，不进 git）
#else
    /// <summary>
    /// 曲库占位实现 —— 开源仓库默认编译这一份。
    /// 曲库的挖掘与筛选策略是本项目未公开的部分，这里只保留与 vault.impl.cs
    /// 完全一致的公开签名，保证 UI 层与宿主调用照常编译通过。
    /// 想接入自己的数据源，实现 Items / Flush / Pull / Explore / Popular / IsMusicLike 即可。
    /// </summary>
    internal static class Vault
    {
        public static List<Dictionary<string, object>> Items
        {
            get { return new List<Dictionary<string, object>>(); }
        }

        /// <summary>占位实现永远没有加载错误。签名与 vault.impl.cs 保持一致。</summary>
        public static string LastError
        {
            get { return ""; }
        }

        public static void Flush() { }

        public static List<object> Pull(string theme, int limit)
        {
            return new List<object>();
        }

        public static async Task<object> Explore(HttpClient2 http, string theme, List<string> seeds, int maxNodes)
        {
            await Task.FromResult(0);
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["busy"] = false;
            r["added"] = 0;
            r["total"] = 0;
            r["stub"] = true;
            r["msg"] = "曲库策略未开源，请在 vault.impl.cs 中接入自己的数据源。";
            return r;
        }

        public static async Task<object> Popular(HttpClient2 http)
        {
            await Task.FromResult(0);
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["added"] = 0;
            r["total"] = 0;
            r["stub"] = true;
            r["msg"] = "曲库策略未开源，请在 vault.impl.cs 中接入自己的数据源。";
            return r;
        }

        public static bool IsMusicLike(string tname, string title)
        {
            return false;
        }

        /// <summary>占位实现无从可删（曲库本来就是空的）。签名与 vault.impl.cs 保持一致。</summary>
        public static int Remove(List<string> ids)
        {
            return 0;
        }
    }
#endif

    /// <summary>登录窗口：独立 UserDataFolder 的 WebView2，轮询到登录 cookie 即返回结果。</summary>
    internal sealed class LoginForm : Form
    {
        private readonly string _url, _profile, _checkUrl;
        private readonly Func<IReadOnlyList<CoreWebView2Cookie>, bool> _probe;
        private WebView2 _wv;
        private Timer _timer;
        private bool _done;

        public LoginForm(string url, string profile, Func<IReadOnlyList<CoreWebView2Cookie>, bool> probe)
        {
            _url = url;
            _profile = profile;
            _probe = probe;
            _checkUrl = (profile == "bilibili") ? Bili.Referer : Netease.Referer;

            Text = (profile == "bilibili") ? "登录B站 - KisstrTV" : "登录网易云音乐 - KisstrTV";
            ClientSize = new Size(profile == "bilibili" ? 1000 : 960, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;

            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KisstrTV.app.ico"))
                { if (s != null) Icon = new Icon(s); }
            }
            catch { }

            _wv = new WebView2();
            _wv.Dock = DockStyle.Fill;
            Controls.Add(_wv);

            Load += async delegate { await InitAsync(); };
            FormClosing += delegate { Stop(); };
        }

        private async Task InitAsync()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KisstrTV", _profile);
                Directory.CreateDirectory(dir);
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dir, null);
                await _wv.EnsureCoreWebView2Async(env);
                _wv.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("登录窗口初始化失败：\n" + ex.Message,
                    "KisstrTV", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _done = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer = new Timer();
            _timer.Interval = 1200;
            _timer.Tick += async delegate { await Poll(); };
            _timer.Start();
        }

        private async Task Poll()
        {
            if (_done || _wv == null || _wv.CoreWebView2 == null) return;
            try
            {
                IReadOnlyList<CoreWebView2Cookie> cs =
                    await _wv.CoreWebView2.CookieManager.GetCookiesAsync(_checkUrl);
                if (cs != null && _probe(cs))
                {
                    if (_profile == "bilibili") Bili.Absorb(cs);
                    else Netease.Absorb(cs);
                    _done = true;
                    Stop();
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch { }
        }

        private void Stop()
        {
            try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; } } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            Stop();
            try { if (_wv != null) { _wv.Dispose(); _wv = null; } } catch { }
            base.Dispose(disposing);
        }
    }
}
