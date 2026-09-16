// =====================================================================
//  KisstrTV · WebView2 / WinForms 宿主
//
//  背景：原实现为 Electron 31，交付物 70 MB（其中 98.9% 是 Chromium + Node
//  运行时，真正的产品代码只有 743 KB 的 index.html）。本文件用 Windows 自带的
//  WebView2 Runtime 替换 Electron 主进程，index.html 一行未改。
//
//  与 Electron 版的对应关系：
//    BrowserWindow(frame:false)      → Form(FormBorderStyle.None) + WM_SIZING 真比例锁
//    ipcMain.handle × 28             → window.audioAPI (#23 postMessage 桥)
//    onBeforeSendHeaders 补 Referer  → CoreWebView2.WebResourceRequested + SetHeader
//    protocol.handle('local')        → https://kisstr-file.local/<b64url> 拦截（支持 Range）
//    Tray / Menu                     → NotifyIcon + ContextMenuStrip
//    app.requestSingleInstanceLock   → Named Mutex
//    session.fromPartition(登录)     → 独立 UserDataFolder 的隐藏 WebView2
//
//  比 Electron 版改进的地方：
//    1) WM_SIZING 在 OS 提交前逐帧校正 → 真正的无卡顿 16:9 拖拽（Electron 版
//       在 setAspectRatio 与 will-resize 之间反复失败，见原档案 v28.4a/b）
//    2) pickVideos 返回 {t,a,u} 而非 {name,url} —— 修正了原版与
//       index.html 中 addLocalFiles() 字段不匹配的问题
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("KisstrTV")]
[assembly: AssemblyProduct("KisstrTV")]
[assembly: AssemblyCompany("KisstrTV")]
[assembly: AssemblyDescription("随机播放音乐 MV")]
[assembly: AssemblyFileVersion("1.0.3.0")]
[assembly: AssemblyVersion("1.0.3.0")]

namespace KisstrTV
{
    internal static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private const string MutexName   = "Local\\KisstrTV.SingleInstance";
        private const string EventName   = "Local\\KisstrTV.Activate";
        private const string WindowTitle = "KisstrTV";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static string _tmpDir;
        private static string _exeDir;

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ---------- 单实例 ----------
            Mutex mutex = null;
            bool createdNew = false;
            try { mutex = new Mutex(true, MutexName, out createdNew); }
            catch { return; }
            if (!createdNew)
            {
                try
                {
                    IntPtr hwnd = FindWindow(null, WindowTitle);
                    if (hwnd != IntPtr.Zero)
                    {
                        uint pid;
                        GetWindowThreadProcessId(hwnd, out pid);
                        AllowSetForegroundWindow(pid);
                    }
                }
                catch { }
                try { EventWaitHandle.OpenExisting(EventName).Set(); } catch { }
                try { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } } catch { }
                return;
            }

            string exeDir;
            try { exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { exeDir = null; }
            if (string.IsNullOrEmpty(exeDir)) exeDir = Environment.CurrentDirectory;
            _exeDir = exeDir;

            string tmp = Path.Combine(Path.GetTempPath(), "KisstrTV", Environment.UserName);
            try { Directory.CreateDirectory(tmp); } catch { tmp = Path.GetTempPath(); }
            _tmpDir = tmp;
            Diag.Clear();
            Diag.Log("start; tmp=" + tmp + " ; exe=" + _exeDir);

            AppDomain.CurrentDomain.AssemblyResolve += ResolveManagedDll;

            try
            {
                try { Extract("KisstrTV.WebView2Loader.dll", Path.Combine(tmp, "WebView2Loader.dll")); }
                catch { try { Extract("KisstrTV.WebView2Loader.dll", Path.Combine(exeDir, "WebView2Loader.dll")); } catch { } }
                SetDllDirectory(tmp);

                // 页面与配套图片释放到 %TEMP%（单文件分发，exe 目录保持干净）
                string assets = Path.Combine(tmp, "assets");
                Directory.CreateDirectory(assets);
                Extract("KisstrTV.index.html", Path.Combine(tmp, "index.html"), true);
                Extract("KisstrTV.icon128.png", Path.Combine(assets, "icon-128.png"), true);
                Extract("KisstrTV.icon.png", Path.Combine(assets, "icon.png"), true);

                EnsureManaged("Microsoft.Web.WebView2.Core", "KisstrTV.WvCore.dll");
                EnsureManaged("Microsoft.Web.WebView2.WinForms", "KisstrTV.WvWinForms.dll");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "程序初始化失败，无法释放运行所需的内置文件。\n\n" +
                    "常见原因：\n" +
                    "  • 杀毒软件拦截了本程序的临时文件写入\n" +
                    "  • 系统临时目录权限异常或磁盘已满\n\n" +
                    "建议：把本程序加入杀毒软件白名单，或换个目录再试一次。\n\n" +
                    "详细信息：" + ex.Message,
                    "KisstrTV - 初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MainForm form = new MainForm(Path.Combine(tmp, "index.html"));

            try
            {
                bool evtCreated;
                EventWaitHandle activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out evtCreated);
                ThreadPool.RegisterWaitForSingleObject(activate, delegate
                {
                    try
                    {
                        form.BeginInvoke((Action)delegate
                        {
                            if (form.WindowState == FormWindowState.Minimized)
                                form.WindowState = FormWindowState.Normal;
                            form.Show();
                            form.Activate();
                            SetForegroundWindow(form.Handle);
                        });
                    }
                    catch { }
                }, null, -1, false);
            }
            catch { }

            Application.Run(form);
            try { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } } catch { }
        }

        private static void EnsureManaged(string asmName, string res)
        {
            if (File.Exists(FindDll(asmName))) return;
            try { Extract(res, Path.Combine(_tmpDir, asmName + ".dll")); }
            catch { try { Extract(res, Path.Combine(_exeDir, asmName + ".dll")); } catch { } }
        }

        private static string FindDll(string asmName)
        {
            string p = Path.Combine(_tmpDir, asmName + ".dll");
            if (File.Exists(p)) return p;
            return Path.Combine(_exeDir, asmName + ".dll");
        }

        private static Assembly ResolveManagedDll(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string res = null;
            if (name == "Microsoft.Web.WebView2.Core") res = "KisstrTV.WvCore.dll";
            else if (name == "Microsoft.Web.WebView2.WinForms") res = "KisstrTV.WvWinForms.dll";
            else return null;

            string path = FindDll(name);
            if (!File.Exists(path))
            {
                try { Extract(res, Path.Combine(_tmpDir, name + ".dll")); }
                catch { try { Extract(res, Path.Combine(_exeDir, name + ".dll")); } catch { } }
                path = FindDll(name);
            }
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static void Extract(string resName, string path, bool force = false)
        {
            if (File.Exists(path) && !force) return;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
            {
                if (s == null) throw new FileNotFoundException("缺少内置资源: " + resName);
                using (FileStream f = File.Create(path)) s.CopyTo(f);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        // ---------- 常量 ----------
        private const double RATIO = 16.0 / 9.0;
        private static readonly Size SizeNormal = new Size(1152, 648);   // 1152÷16×9 = 648
        private static readonly Size MinNormal  = new Size(928, 522);    // 严格 16:9
        private static readonly Size SizeMini   = new Size(480, 270);
        private static readonly Size MinMini    = new Size(288, 162);
        private const string FileHost = "kisstr-file.local";

        // ---------- Win32 ----------
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_SIZING        = 0x0214;
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3,
                          WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5,
                          WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // ---------- 状态 ----------
        private readonly string _htmlPath;
        private WebView2 _wv;
        private NotifyIcon _tray;
        private bool _pinned, _mini, _quitting;
        private Rectangle _normalBounds = Rectangle.Empty;
        private Rectangle _miniBounds = Rectangle.Empty;
        private Point _dragOffset;
        private bool _dragging;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly HttpClient2 _http = new HttpClient2();

        public MainForm(string htmlPath)
        {
            _htmlPath = htmlPath;
            Text = "KisstrTV";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = SizeNormal;
            BackColor = Color.FromArgb(12, 12, 14);
            ShowIcon = true;

            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KisstrTV.app.ico"))
                { if (s != null) Icon = new Icon(s); }
            }
            catch { }

            WebView2 wv = new WebView2();
            _wv = wv;
            wv.Dock = DockStyle.Fill;
            wv.DefaultBackgroundColor = Color.FromArgb(12, 12, 14);
            Controls.Add(wv);

            Load += delegate { InitTray(); };
            Load += async delegate { await InitWebView(); };
        }

        private string UserDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KisstrTV", "WebView2");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private async Task InitWebView()
        {
            try
            {
                CoreWebView2EnvironmentOptions opts = null;
                try { opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"); }
                catch { opts = null; }

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, UserDataDir(), opts);
                await _wv.EnsureCoreWebView2Async(env);

                CoreWebView2 core = _wv.CoreWebView2;

                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = Diag.On;   // 仅在诊断模式下开放

                // 全局请求拦截：① 给 CDN 补 Referer/UA ② 本地视频文件落地
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnResourceRequested;

                // 只观察不改写：拿到真实 HTTP 状态码（403 / 206 一目了然）
                core.WebResourceResponseReceived += delegate(object s, CoreWebView2WebResourceResponseReceivedEventArgs ev)
                {
                    try { Diag.Log("RESP " + ev.Response.StatusCode + "  " + Cut(ev.Request.Uri, 110)); }
                    catch { }
                };

                core.NavigationCompleted += async delegate(object s, CoreWebView2NavigationCompletedEventArgs ev)
                {
                    Diag.Log("nav done, success=" + ev.IsSuccess + " (" + ev.WebErrorStatus + ")");
                    try { if (Diag.On) await _wv.CoreWebView2.ExecuteScriptAsync(DiagScript); }
                    catch (Exception ex) { Diag.Log("diag inject failed: " + ex.Message); }
                };

                // JS → C#
                _wv.WebMessageReceived += async delegate(object s, CoreWebView2WebMessageReceivedEventArgs e)
                {
                    try { await HandleMessage(e.WebMessageAsJson); }
                    catch { }
                };

                // target=_blank / window.open → 系统默认浏览器
                core.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    try { System.Diagnostics.Process.Start(e.Uri); } catch { }
                };

                _wv.CoreWebView2.Navigate(new Uri(PrepareRuntimeHtml()).AbsoluteUri);
            }
            catch (Exception ex)
            {
                const string url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
                DialogResult r = MessageBox.Show(
                    "缺少 Microsoft Edge WebView2 Runtime，程序无法显示界面。\n\n" +
                    "这是微软提供的免费系统组件（微信、钉钉等软件也在使用），" +
                    "系统较老的 Windows 10 可能没有预装。\n\n" +
                    "点击「是」将打开微软官方下载页，下载并安装后重新打开本程序即可。\n\n" +
                    "详细信息：" + ex.Message,
                    "KisstrTV - 缺少运行组件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(url); } catch { }
                }
                Close();
            }
        }

        /// <summary>
        /// 生成带桥接脚本的运行时副本，保证 02-src\index.html 源文件不被修改。
        ///
        /// 注入点必须在 DOCTYPE 与 &lt;html&gt; 之后：若把 script 放到 DOCTYPE 之前，
        /// HTML 解析器会判定「doctype 不是文档首项」从而强制进入 quirks 模式，
        /// 页面的百分比高度 / flex 布局会整体塌陷（表现为黑屏）。
        /// </summary>
        private string PrepareRuntimeHtml()
        {
            try
            {
                string orig = File.ReadAllText(_htmlPath, Encoding.UTF8);
                string head = "<script>" + Bridge.Js + "</script>";

                int at = IndexOf(orig, "<head");
                if (at < 0) at = IndexOf(orig, "<html");
                if (at < 0) at = IndexOf(orig, "<!doctype");
                if (at >= 0)
                {
                    int end = orig.IndexOf('>', at);
                    if (end > 0) at = end + 1; else at = -1;
                }
                if (at < 0) at = 0;     // 兜底：实在找不到就放最前（此时已顾不上 quirks）

                string merged = orig.Substring(0, at) + head + orig.Substring(at);
                string outPath = Path.Combine(Path.GetDirectoryName(_htmlPath), "index.run.html");
                File.WriteAllText(outPath, merged, new UTF8Encoding(false));
                Diag.Log("html: inject at " + at + " , compat-mode-safe=" + (at > 0));
                return outPath;
            }
            catch
            {
                return _htmlPath;   // 注入失败也别崩：页面仍能打开，只是桥不可用
            }
        }

        private static int IndexOf(string s, string needle)
        {
            return s.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }

        // =================================================================
        //  请求拦截：Referer 注入 + 本地文件服务
        //  不设置 e.Response = 请求按原样继续（只补头），不改写响应内容
        // =================================================================
        private void OnResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string uri = e.Request.Uri;
                Uri u;
                if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return;
                string host = u.Host;

                if (string.Equals(host, FileHost, StringComparison.OrdinalIgnoreCase))
                {
                    ServeLocalFile(e);
                    return;
                }

                if (Refs.IsBili(host))
                {
                    e.Request.Headers.SetHeader("Referer", Bili.Referer);
                    e.Request.Headers.SetHeader("User-Agent", Refs.UA);
                    Diag.Log("REQ  +referer bili     " + Cut(uri, 110));
                }
                else if (Refs.IsNetease(host))
                {
                    e.Request.Headers.SetHeader("Referer", Netease.Referer);
                    e.Request.Headers.SetHeader("User-Agent", Refs.UA);
                    Diag.Log("REQ  +referer netease  " + Cut(uri, 110));
                }
                else
                {
                    Diag.Log("REQ  (no patch)        " + Cut(uri, 110));
                }
            }
            catch { }
        }

        private static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        /// <summary>页面侧探针：把 video 元素状态与文档模式回传给宿主日志。</summary>
        private const string DiagScript = @"
(function(){
  try {
    window.__diagSent = 0;
    var ch = window.chrome.webview;
    function send(){
      var v = document.getElementById('video');
      var o = {
        compat: document.compatMode,
        hasAPI: !!window.audioAPI,
        hasVideo: !!v,
        src: v ? (v.currentSrc || v.src || '').slice(0,90) : '',
        rs: v ? v.readyState : -1,
        ns: v ? v.networkState : -1,
        err: (v && v.error) ? (v.error.code + ':' + (v.error.message||'')) : '',
        ct: v ? Math.round(v.currentTime*10)/10 : -1,
        dim: v ? (v.videoWidth + 'x' + v.videoHeight) : '',
        paused: v ? v.paused : null
      };
      ch.postMessage({ __k: -1, m: '__diag', a: [JSON.stringify(o)] });
    }
    [
      'loadstart','loadedmetadata','loadeddata','canplay','playing','waiting',
      'stalled','suspend','error','abort','emptied','pause','seeking'
    ].forEach(function(e){
      var v = document.getElementById('video');
      if (v) v.addEventListener(e, function(){ ch.postMessage({__k:-1,m:'__diag',a:['EVT ' + e]}); });
    });
    setInterval(send, 3000);
    setTimeout(send, 1500);
    // TEMP 自检 1：模拟用户点击第一个频道卡片，看是否会走到 biliResolve
    setTimeout(function(){
  } catch(e) {}
})();";

        private void ServeLocalFile(CoreWebView2WebResourceRequestedEventArgs e)
        {
            FileStream fs = null;
            try
            {
                Uri u = new Uri(e.Request.Uri);
                string path = Util.B64UrlDecode(u.AbsolutePath.TrimStart('/'));
                if (!File.Exists(path))
                {
                    e.Response = _wv.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }

                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long total = fs.Length;
                string range = null;
                try { range = e.Request.Headers.GetHeader("Range"); } catch { range = null; }

                long start = 0, end = total - 1;
                int code = 200;
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = range.Substring(6).Split('-');
                    long a = 0, b = total - 1;
                    long.TryParse(parts[0], out a);
                    if (parts.Length > 1 && parts[1].Length > 0) long.TryParse(parts[1], out b);
                    start = a; end = Math.Min(b, total - 1);
                    if (start > end) { start = 0; end = total - 1; }
                    code = 206;
                }

                long len = end - start + 1;
                fs.Position = start;
                byte[] buf = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(buf, read, (int)Math.Min(81920, len - read));
                    if (n <= 0) break;
                    read += n;
                }
                fs.Dispose(); fs = null;

                MemoryStream ms = new MemoryStream(buf, 0, read, false);
                string hdr = "Content-Type: " + GuessMime(path) + "\r\n" +
                             "Accept-Ranges: bytes\r\n" +
                             "Content-Length: " + read + (code == 206
                                ? "\r\nContent-Range: bytes " + start + "-" + (start + read - 1) + "/" + total
                                : "");
                e.Response = _wv.CoreWebView2.Environment.CreateWebResourceResponse(
                    ms, code, code == 206 ? "Partial Content" : "OK", hdr);
            }
            catch
            {
                try { e.Response = _wv.CoreWebView2.Environment.CreateWebResourceResponse(null, 500, "Error", ""); }
                catch { }
            }
            finally
            {
                try { if (fs != null) fs.Dispose(); } catch { }
            }
        }

        private static string GuessMime(string p)
        {
            string ext = Path.GetExtension(p).ToLowerInvariant();
            switch (ext)
            {
                case ".mp4": return "video/mp4";
                case ".webm": return "video/webm";
                case ".mkv": return "video/x-matroska";
                case ".mov": return "video/quicktime";
                case ".m4v": return "video/x-m4v";
                case ".flv": return "video/x-flv";
                default: return "video/mp4";
            }
        }

        // =================================================================
        //  JS → C# 桥接分发
        // =================================================================
        private async Task HandleMessage(string raw)
        {
            Dictionary<string, object> msg = null;
            try { msg = _json.Deserialize<Dictionary<string, object>>(raw); }
            catch { return; }
            if (msg == null || !msg.ContainsKey("__k") || !msg.ContainsKey("m")) return;

            string id = Convert.ToString(msg["__k"]);
            string method = Convert.ToString(msg["m"]);
            // 注意：JavaScriptSerializer 给的是 ArrayList，不能 `as List<object>`
            List<object> args = Json.AsList(msg.ContainsKey("a") ? msg["a"] : null);
            if (args == null) args = new List<object>();

            // 诊断回传：只记日志，不回复（避免污染 pending 表）
            if (method == "__diag")
            {
                Diag.Log("JS   " + (args.Count > 0 ? Convert.ToString(args[0]) : ""));
                return;
            }
            Diag.Log("CALL " + method + "  " + Cut(raw, 120));

            object result = null;
            string error = null;
            try { result = await Dispatch(method, args); }
            catch (Exception ex) { error = ex.Message; }

            try
            {
                string s = error != null ? ("ERR " + error) : _json.Serialize(result);
                Diag.Log("RET  " + method + " -> " + Cut(s, 240));
            }
            catch { }

            Reply(id, result, error);
        }

        private void Reply(string id, object result, string error)
        {
            try
            {
                Dictionary<string, object> box = new Dictionary<string, object>();
                box["__k"] = long.Parse(id);
                if (error != null) box["error"] = error; else box["result"] = result;
                string js = _json.Serialize(box);
                _wv.CoreWebView2.PostWebMessageAsJson(js);
            }
            catch
            {
                try
                {
                    _wv.CoreWebView2.PostWebMessageAsJson("{\"__k\":" + id + ",\"error\":\"serialize-failed\"}");
                }
                catch { }
            }
        }

        private Task<object> Dispatch(string m, List<object> a)
        {
            string s0 = a.Count > 0 ? Convert.ToString(a[0]) : "";
            double d0 = Num(a, 0);
            double d1 = Num(a, 1);

            switch (m)
            {
                // ---- 窗口 ----
                case "ready": return Done(true);
                case "winMin": Run(() => { WindowState = FormWindowState.Minimized; }); return Done(true);
                case "winMax": Run(() =>
                {
                    if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                    else { WindowState = FormWindowState.Normal; WindowState = FormWindowState.Maximized; }
                }); return Done(true);
                case "winClose": Run(() => Close()); return Done(true);
                case "winPin":
                    Run(() => { _pinned = !_pinned; if (!_mini) TopMost = _pinned; });
                    return Done(_pinned);
                case "setMini": bool on = d0 != 0; Run(() => SetMini(on)); return Done(true);
                case "toggleFullscreen": return setFullScreen(!_maxed);
                case "setFullscreen": bool fs = d0 != 0; return setFullScreen(fs);

                case "winDrag":
                    Run(() => Drag(s0, (int)Math.Round(d1), (int)Math.Round(Num(a, 2))));
                    return Done(true);
                case "winBounds":
                {
                    Rectangle b = Bounds;
                    return Done(new Dictionary<string, object> {
                        { "x", b.X }, { "y", b.Y }, { "width", b.Width }, { "height", b.Height } });
                }
                case "winSetBounds":
                {
                    Dictionary<string, object> b = a.Count > 0 ? a[0] as Dictionary<string, object> : null;
                    if (b != null)
                        Run(() => Bounds = new Rectangle(
                            (int)Math.Round(GetD(b, "x")), (int)Math.Round(GetD(b, "y")),
                            (int)Math.Round(GetD(b, "width")), (int)Math.Round(GetD(b, "height"))));
                    return Done(true);
                }
                case "videoRatio": return Done(RATIO);

                case "pickVideos": return PickVideos();

                // ---- B 站 ----
                case "biliStatus":       return Wrap(() => Bili.Status(_http));
                case "biliLogin":        return GuardLogin(() => DoLogin(Bili.LoginUrl, "bilibili", Bili.IsLoginCookie), "B站");
                case "biliLogout":       return Wrap(() => Bili.Logout());
                case "biliResolve":      return Wrap(() => Bili.Resolve(_http, s0));
                case "biliSearch":       return Wrap(() => Bili.Search(_http, s0, (int)d1));
                case "biliPages":        return Wrap(() => Bili.Pages(_http, s0));
                case "biliView":         return Wrap(() => Bili.View(_http, s0));
                case "biliResolveCid":   return Wrap(() => Bili.ResolveCid(_http, s0, (long)d1, A(a, 2)));
                case "setBiliQuality":   Bili.SetQuality((int)d0); return Done(Bili.Quality);
                case "biliQualityGet":   return Done(Bili.Quality);

                // ---- 网易云（HTTP + 落盘 cookie，无需常驻隐藏窗口）----
                case "neteaseStatus":    return Wrap(() => Netease.Status(_http));
                case "neteaseLogin":     return GuardLogin(() => DoLogin(Netease.LoginUrl, "netease", Netease.IsLoginCookie), "网易云");
                case "neteaseLogout":    return Wrap(() => Netease.Logout());
                case "neteaseSearch":    return Wrap(() => Netease.Search(_http, s0));
                case "neteaseResolve":   return Wrap(() => Netease.Resolve(_http, s0));

                // ---- 曲库 ----
                case "vaultStats":
                {
                    // 顺带把加载错误带回页面：曲库读失败时显示原因，而不是默默 0 条让人以为库丢了
                    Dictionary<string, object> vs = new Dictionary<string, object>();
                    vs["total"] = Vault.Items.Count;
                    string ve = Vault.LastError;
                    if (!string.IsNullOrEmpty(ve)) vs["error"] = ve;
                    return Done(vs);
                }
                case "vaultPull":        return Done(Vault.Pull(s0, (int)d1));
                case "vaultExplore":     return Vault.Explore(_http, s0, ToStrings(a, 1), (int)Num(a, 2));
                case "vaultPopular":     return Vault.Popular(_http);
                // 播放失败：从曲库永久删除（不拉黑、不解禁），避免坏源反复回来
                case "vaultRemove":
                {
                    List<string> rid = ToStrings(a, 0);
                    return Wrap(() => new Dictionary<string, object> {
                        { "ok", true }, { "removed", Vault.Remove(rid) } });
                }
            }
            throw new NotSupportedException("未实现的方法: " + m);
        }

        private bool _maxed;

        private Task<object> setFullScreen(bool on)
        {
            _maxed = on;
            Run(() =>
            {
                if (on) { WindowState = FormWindowState.Normal; WindowState = FormWindowState.Maximized; }
                else WindowState = FormWindowState.Normal;
            });
            return Done(_maxed);
        }

        // =================================================================
        //  窗口：拖拽 / 迷你 / 16:9 比例锁
        // =================================================================
        private void Drag(string act, int sx, int sy)
        {
            if (act == "start")
            {
                _dragging = true;
                _dragOffset = new Point(sx - Left, sy - Top);
            }
            else if (act == "move" && _dragging)
            {
                Left = sx - _dragOffset.X;
                Top = sy - _dragOffset.Y;
            }
            else if (act == "end") _dragging = false;
        }

        private void SetMini(bool on)
        {
            if (_mini == on) return;
            _mini = on;
            if (on)
            {
                _normalBounds = WindowState == FormWindowState.Normal ? Bounds : _normalBounds;
                MinimumSize = MinMini;
                WindowState = FormWindowState.Normal;
                Screen sc = Screen.FromHandle(Handle);
                Rectangle b = _miniBounds.IsEmpty
                    ? new Rectangle(sc.WorkingArea.Right - SizeMini.Width - 18,
                                    sc.WorkingArea.Top + 18, SizeMini.Width, SizeMini.Height)
                    : _miniBounds;
                Bounds = KeepOnScreen(b);
                TopMost = true;
                HideTray();
            }
            else
            {
                if (!_normalBounds.IsEmpty) Bounds = KeepOnScreen(_normalBounds);
                MinimumSize = MinNormal;
                TopMost = _pinned;
                ShowTray();
            }
        }

        private Rectangle KeepOnScreen(Rectangle b)
        {
            Rectangle wa = Screen.FromRectangle(b).WorkingArea;
            int x = Math.Max(wa.Left - 40, Math.Min(wa.Right - 60, b.X));
            int y = Math.Max(wa.Top - 40, Math.Min(wa.Bottom - 40, b.Y));
            return new Rectangle(x, y, b.Width, b.Height);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (_mini && WindowState == FormWindowState.Normal) _miniBounds = Bounds;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try { if (_wv != null) _wv.Invalidate(); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_GETMINMAXINFO)
            {
                Native.MINMAXINFO mmi = (Native.MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(Native.MINMAXINFO));
                Size mn = _mini ? MinMini : MinNormal;
                mmi.ptMinTrackSize = new Point(mn.Width, mn.Height);
                Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                mmi.ptMaxSize = new Point(wa.Width, wa.Height);
                mmi.ptMaxPosition = new Point(wa.X, wa.Y);
                Marshal.StructureToPtr(mmi, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_SIZING && WindowState != FormWindowState.Maximized)
            {
                // 在 OS 提交尺寸之前逐帧校正 → 拖拽全程严格 16:9，零卡顿。
                // （Electron 版在原生 setAspectRatio 与 will-resize 之间反复失败，
                //   见原项目档案 v28.4a / v28.4b；Win32 这一层可以直接做对。）
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int edge = m.WParam.ToInt32();
                int minW = _mini ? MinMini.Width : MinNormal.Width;
                int minH = _mini ? MinMini.Height : MinNormal.Height;

                int w = Math.Max(minW, r.Right - r.Left);
                int h = Math.Max(minH, r.Bottom - r.Top);

                switch (edge)
                {
                    case WMSZ_TOP:
                    case WMSZ_BOTTOM:
                    {
                        // 垂直拖拽：以高度为锚反推宽度，保持未动的水平边
                        int nw = (int)Math.Round(h * RATIO);
                        if (nw < minW) { nw = minW; h = (int)Math.Round(nw / RATIO); }
                        if (edge == WMSZ_TOP) r.Left = r.Right - nw; else r.Right = r.Left + nw;
                        if (edge == WMSZ_TOP) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
                        break;
                    }
                    case WMSZ_LEFT:
                    case WMSZ_RIGHT:
                    {
                        // 水平拖拽：以宽度为锚，垂直居中伸缩
                        int nh = (int)Math.Round(w / RATIO);
                        if (nh < minH) { nh = minH; w = (int)Math.Round(nh * RATIO); }
                        int cy = (r.Top + r.Bottom) / 2;
                        r.Top = cy - nh / 2;
                        r.Bottom = r.Top + nh;
                        if (edge == WMSZ_LEFT) r.Left = r.Right - w; else r.Right = r.Left + w;
                        break;
                    }
                    default:
                    {
                        // 四角：以宽度为锚，保持未动的两条边
                        int nh = (int)Math.Round(w / RATIO);
                        if (nh < minH) { nh = minH; w = (int)Math.Round(nh * RATIO); }
                        if (edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT) r.Top = r.Bottom - nh;
                        else r.Bottom = r.Top + nh;
                        if (edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT) r.Left = r.Right - w;
                        else r.Right = r.Left + w;
                        break;
                    }
                }

                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        // =================================================================
        //  托盘 / 关窗驻留（对应 Electron 的 Tray + win-pause-media）
        // =================================================================
        private void InitTray()
        {
            try
            {
                Icon ic = null;
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KisstrTV.app.ico"))
                { if (s != null) ic = new Icon(s, 32, 32); }
                if (ic == null) ic = Icon;

                _tray = new NotifyIcon();
                _tray.Icon = ic;
                _tray.Text = "KisstrTV";
                _tray.Visible = true;

                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("显示 KisstrTV", null, delegate { RestoreFromTray(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { _quitting = true; Application.Exit(); });
                _tray.ContextMenuStrip = menu;
                _tray.MouseClick += delegate(object s, MouseEventArgs e)
                {
                    if (e.Button == MouseButtons.Left) RestoreFromTray();
                };
            }
            catch { }
        }

        private void HideTray() { try { if (_tray != null) _tray.Visible = false; } catch { } }
        private void ShowTray() { try { if (_tray != null) _tray.Visible = true; } catch { } }

        private void RestoreFromTray()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Show();
                Activate();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_quitting || e.CloseReason == CloseReason.WindowsShutDown) return;
            // 点 ✕ = 隐藏到托盘驻留 → 通知页面暂停（语义同 Electron 版 v28.5）
            e.Cancel = true;
            Hide();
            try
            {
                if (_wv != null && _wv.CoreWebView2 != null)
                    _wv.CoreWebView2.ExecuteScriptAsync(
                        "try{window.dispatchEvent(new CustomEvent('kisstr-pause-media'));}catch(e){}");
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { Vault.Flush(); } catch { }
            try { Bili.SaveCovers(); } catch { }
        }

        // =================================================================
        //  登录窗口（B站 / 网易云共用）
        // =================================================================

        /// <summary>
        /// 限流冷却期禁止发起登录。
        /// 风控是对 IP 的，但登录会把「异常流量」和「具体账号」绑定起来。
        /// 在被挡的时候还去登录，等于主动把账号递上去。冷却结束再登就没事。
        /// </summary>
        private Task<object> GuardLogin(Func<Task<object>> open, string who)
        {
            if (HttpClient2.Blocked)
            {
                int s = HttpClient2.BlockSecondsLeft;
                return Done(Err(who + " 正在对本网络限流，请约 " + (s > 0 ? s + " 秒" : "几分钟")
                           + "后再登录。现在登录会把异常流量关联到你的账号，等冷却结束再登更安全。"));
            }
            return open();
        }

        private Task<object> DoLogin(string url, string profile, Func<IReadOnlyList<CoreWebView2Cookie>, bool> probe)
        {
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();
            Run(delegate
            {
                try
                {
                    LoginForm dlg = new LoginForm(url, profile, probe);
                    DialogResult r = dlg.ShowDialog(this);
                    Dictionary<string, object> res = new Dictionary<string, object>();
                    res["ok"] = (r == DialogResult.OK);
                    if (r != DialogResult.OK) res["msg"] = "未完成登录";
                    tcs.TrySetResult(res);
                }
                catch (Exception ex) { tcs.TrySetResult(Err(ex.Message)); }
            });
            return tcs.Task;
        }

        // =================================================================
        //  小工具
        // =================================================================

        private void Run(Action a)
        {
            try { if (IsHandleCreated) BeginInvoke(a); else a(); }
            catch { }
        }

        /// <summary>
        /// 网络调用并发上限。页面一次性为整库条目拉封面时可达上万次，
        /// 不限流会把 IP 打到 B 站风控（-412），之后所有请求都失败。
        /// </summary>
        private static readonly SemaphoreSlim _net = new SemaphoreSlim(8);

        private Task<object> Wrap(Func<object> f)
        {
            return Task.Run<object>(async () =>
            {
                await _net.WaitAsync();
                try { return f(); }
                catch (Exception ex) { return Err(ex.Message); }
                finally { _net.Release(); }
            });
        }

        private static Task<object> Done(object v)
        {
            TaskCompletionSource<object> t = new TaskCompletionSource<object>();
            t.SetResult(v);
            return t.Task;
        }

        private Task<object> PickVideos()
        {
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();
            Run(delegate
            {
                try
                {
                    using (OpenFileDialog d = new OpenFileDialog())
                    {
                        d.Title = "选择要加入电台的 MV 视频文件";
                        d.Multiselect = true;
                        d.Filter = "视频文件|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.flv;*.m4v|所有文件|*.*";
                        if (d.ShowDialog(this) != DialogResult.OK) { tcs.TrySetResult(new List<object>()); return; }
                        List<object> arr = new List<object>();
                        foreach (string p in d.FileNames)
                        {
                            string name = Path.GetFileNameWithoutExtension(p);
                            // 注意：index.html 的 addLocalFiles() 期望 {t,a,u}
                            // （Electron 版返回的是 {name,url}，与前端字段不符，这里已修正）
                            Dictionary<string, object> it = new Dictionary<string, object>();
                            it["t"] = name;
                            it["a"] = "本地文件";
                            it["u"] = "https://" + FileHost + "/" + Util.B64UrlEncode(p);
                            arr.Add(it);
                        }
                        tcs.TrySetResult(arr);
                    }
                }
                catch { tcs.TrySetResult(new List<object>()); }
            });
            return tcs.Task;
        }

        private static Dictionary<string, object> Err(string msg)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = false;
            d["msg"] = msg;
            return d;
        }

        private static double Num(List<object> a, int i)
        {
            if (i >= a.Count || a[i] == null) return 0;
            try { return Convert.ToDouble(a[i]); } catch { return 0; }
        }

        private static string A(List<object> a, int i)
        {
            return i < a.Count && a[i] != null ? Convert.ToString(a[i]) : "";
        }

        private static double GetD(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToDouble(v); } catch { return 0; }
        }

        private static List<string> ToStrings(List<object> a, int i)
        {
            List<string> r = new List<string>();
            if (i >= a.Count) return r;
            List<object> src = Json.AsList(a[i]);
            if (src == null) return r;
            foreach (object o in src) if (o != null) r.Add(Convert.ToString(o));
            return r;
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public Point ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }
    }
}
