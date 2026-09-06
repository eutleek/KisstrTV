// 音悦TV 复刻版 · Electron 主进程
// 职责：创建无边框窗口、窗口置顶、迷你悬浮、本地视频协议(local://)、文件选择、B站登录/解析/搜索
'use strict';

const { app, BrowserWindow, ipcMain, dialog, protocol, net, session, Tray, Menu, nativeImage, shell } = require('electron');
const path = require('path');
const fs = require('fs');
const fsp = fs.promises;
const { Readable } = require('stream');

// EPIPE 容错：stdout/stderr 管道被关闭时（无控制台 / 重定向 / 双击启动），console.log 写入会抛 EPIPE
// 未捕获的 EPIPE 会让 Electron 弹"主进程 JavaScript 错误"框。这里把它吞掉。
for (const s of [process.stdout, process.stderr]) {
  if (s && s.on) s.on('error', (e) => { if (!(e && e.code === 'EPIPE')) console.error('STREAM_ERR', e && e.message); });
}
process.on('uncaughtException', (err) => {
  if (err && err.code === 'EPIPE') return;   // 管道破裂：忽略
  console.error('UNCAUGHT', err && err.stack);
});

// 本地视频自定义协议（用于播放用户本机 MV 文件，支持 Range 拖动进度）
protocol.registerSchemesAsPrivileged([
  { scheme: 'local', privileges: { supportFetchAPI: true, stream: true, bypassCSP: true } }
]);

// 自动播放策略：允许无手势自动播放（模拟电台连续播放）
app.commandLine.appendSwitch('autoplay-policy', 'no-user-gesture-required');

const isWin = process.platform === 'win32';
let win = null;
let pinned = false;
let mini = false;
let normalBounds = null;
let tray = null;            // 系统托盘（右下角进程图标）：主窗模式驻留入口，mini 模式隐藏
let quitting = false;       // 正在真正退出（托盘/系统退出），此时关窗放行不转驻留
const curRatio = 16 / 9;      // 固定 16:9，窗口按它锁定比例（比例不变、无黑边）
// 比例锁定改用 will-resize，原 ratioLocked 防抖已不需要

/* ---------- 比例锁定：窗口固定 16:9，视频加载/拖动改大小都不改变窗口比例 ---------- */
ipcMain.handle('video-ratio', () => curRatio);   // 保留接口兼容，不再随视频变形

/* 改名后迁移旧 userData（曲库 vault / 登录 cookie / 迷你窗位置），避免数据丢失 */
/* 依次合并：音悦TV复刻 → MVMusicTV → KisstrTV（当前） */
function migrateOldUserData() {
  const oldDirs = ['音悦TV复刻', 'MVMusicTV'];
  const newDir = app.getPath('userData');
  if (newDir.toLowerCase().endsWith('kisstrtv')) {
    for (const oldName of oldDirs) mergeOldDir(oldName, newDir);
  }
}
function mergeOldDir(oldName, newDir) {
  try {
    const oldDir = path.join(app.getPath('appData'), oldName);
    if (oldDir === newDir || !fs.existsSync(oldDir)) return;
    // 1) vault 合并去重（旧库 + 新库 按 bvid 并集）
    const ov = path.join(oldDir, 'vault.json'), nv = path.join(newDir, 'vault.json');
    try {
      const oldV = JSON.parse(fs.readFileSync(ov, 'utf8'));
      if (oldV && Array.isArray(oldV.items) && oldV.items.length) {
        let newV = { items: [] };
        try { newV = JSON.parse(fs.readFileSync(nv, 'utf8')); } catch (e) {}
        if (!newV || !Array.isArray(newV.items)) newV = { items: [] };
        const known = new Set(newV.items.map((x) => x.bvid));
        let added = 0;
        for (const it of oldV.items) {
          if (it && it.bvid && !known.has(it.bvid)) { known.add(it.bvid); newV.items.push(it); added++; }
        }
        if (added) fs.writeFileSync(nv, JSON.stringify(newV));
      }
    } catch (e) {}
    // 2) cookie / 迷你窗位置：新目录缺失才拷贝
    for (const f of ['bili_cookies.json', 'mini_bounds.json']) {
      const src = path.join(oldDir, f), dst = path.join(newDir, f);
      if (!fs.existsSync(dst) && fs.existsSync(src)) { try { fs.copyFileSync(src, dst); } catch (e) {} }
    }
  } catch (e) {}
}

function createWindow() {
  win = new BrowserWindow({
    width: 1152,
    height: 648,              // 16:9 刚好填充（无黑边）
    minWidth: 928,            // 928×522 严格 16:9（928÷16×9=522）。旧值 920×520 与比例矛盾：
    minHeight: 522,           // 920÷16×9=517.5<520，比例锁与最小高度打架 → 缩放循环卡死（v28.4b「锁死」的真因）
    frame: false,               // 无边框，自定义标题栏
    backgroundColor: '#0c0c0e',
    show: !process.env.YYTV_SMOKE,  // 冒烟测试时不显示窗口
    icon: path.join(__dirname, 'assets', isWin ? 'icon.ico' : 'icon.png'),
    title: 'KisstrTV',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      spellcheck: false
    }
  });

  /* v29.7 比例锁定终版：OS 原生 setAspectRatio —— 拖拽全程由 Windows 按 16:9 实时联动宽高，
     这才是「长宽同时缩放 + 丝滑」的唯一原生机制（渲染层逐帧 setBounds 必黑闪，松手归正必跳变）。
     v28.4b 实测「锁死边缘」的真因 = 旧最小尺寸 920×520 与 16:9 矛盾（920÷16×9=517.5<520），
     最小尺寸改为严格等比 928×522 后比例锁与下限不再打架。 */
  try { win.setAspectRatio(curRatio); } catch (e) {}
  win.setMaximizable(true);

  win.loadFile(path.join(__dirname, 'index.html'), {
    query: process.env.YYTV_AUTOSTART === '1'
      ? { autostart: '1', sources: process.env.YYTV_SOURCES === '1' ? '1' : '0', channel: process.env.YYTV_CHANNEL || '' }
      : {}
  });

  // 外部链接（GitHub 仓库 / 官网等）统一交给系统默认浏览器打开，绝不导航本窗口。
  // target="_blank" / window.open 走 setWindowOpenHandler；普通 <a href> 跳转走 will-navigate。
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url && /^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.on('will-navigate', (event, url) => {
    const cur = win.webContents.getURL() || '';
    if (url && /^https?:/i.test(url) && url !== cur) {
      event.preventDefault();
      shell.openExternal(url);
    }
  });

  win.once('ready-to-show', () => {
    if (!process.env.YYTV_SMOKE) win.show();
    else {
      // 冒烟测试：确认页面加载且渲染层脚本无异常
      win.webContents.executeJavaScript(
        'JSON.stringify({t: (document.getElementById("nowTitle")||{}).textContent||"", chs: document.querySelectorAll(".ch").length, hasVideo: !!document.getElementById("video")})'
      ).then(function (r) { console.log('SMOKE_RENDER=' + r); })
       .catch(function (e) { console.log('SMOKE_JSERR=' + (e && e.message)); });
      // 冒烟测试：B站直链解析全链路（仅在冒烟模式）
      win.webContents.executeJavaScript(
        '(async function(){ try { var r = await window.audioAPI.biliResolve("BV1d4411N7zD"); return JSON.stringify({ok:!!(r&&r.ok), url:(r&&r.url?r.url.slice(0,42):null), msg:(r&&r.msg||"")}); } catch(e){ return "ERR:"+e.message; } })()'
      ).then(function (r) { console.log('SMOKE_BILI=' + r); })
       .catch(function (e) { console.log('SMOKE_BILIERR=' + (e && e.message)); });
      // 冒烟测试：B站直链真实起播（验证 Referer 注入 + video 播放链路）
      win.webContents.executeJavaScript(
        '(async function(){ try { var r = await window.audioAPI.biliResolve("BV1d4411N7zD"); if(!r||!r.ok) return "RESOLVE_FAIL"; var v = document.getElementById("video"); v.src = r.url; v.load(); v.play().catch(function(){}); await new Promise(function(res){ var done=false; function ok(){ if(done) return; done=true; res(); } v.addEventListener("loadedmetadata", function(){ setTimeout(ok, 1600); }); v.addEventListener("error", function(){ res(); }); setTimeout(ok, 9000); }); return JSON.stringify({dur:+v.duration.toFixed(1), ct:+(v.currentTime||0).toFixed(2), paused:v.paused, src:v.src.slice(0,42)}); } catch(e){ return "ERR:"+e.message; } })()'
      ).then(function (r) { console.log('SMOKE_PLAY=' + r); })
       .catch(function (e) { console.log('SMOKE_PLAYERR=' + (e && e.message)); });
    }
  });

  win.on('close', (event) => {
    // 「关窗→托盘驻留」：非真正退出时，拦截关闭转为隐藏到系统托盘，后台继续驻留/放歌。
    // 真退出路径（托盘点"退出"/app 退出）：quitting=true，这里放行。
    if (!quitting && tray) {
      event.preventDefault();
      win.hide();
      // v28.5 关窗(→托盘驻留)=自动暂停视频：通知渲染层暂停（不再是后台继续放歌）。
      // 重新点开窗口后由用户单击视频恢复播放。
      try { if (!win.isDestroyed()) win.webContents.send('win-pause-media'); } catch (e) {}
      return;
    }
    miniSaveBounds();
    if (normalBounds && !win.isMaximized()) normalBounds = win.getBounds();
  });
  win.on('moved', debouncedMiniSaveBounds);
  win.on('resized', debouncedMiniSaveBounds);
  /* v29.7 比例锁定 = OS 原生 setAspectRatio（上方）。这里保留 resize 尾沿防抖归正，
     仅作为兜底（还原 mini / 最大化还原等非拖拽路径残留的非 16:9 尺寸）；
     拖拽中 setAspectRatio 已实时保证比例，归正差 <2px 时不会动作。 */
  let ratioTimer = null;
  win.on('resize', () => {
    if (ratioTimer) clearTimeout(ratioTimer);
    ratioTimer = setTimeout(() => { ratioTimer = null; snapWindowToRatio(); }, 180);
  });
  // 松手归正：以当前宽度为锚，把高度校正到 16:9（宽高差超过 2px 才动，避免抖动）
  function snapWindowToRatio() {
    if (!win || win.isDestroyed() || win.isMaximized()) return;
    try {
      const b = win.getBounds();
      const minH = mini ? 162 : 522;
      const targetH = Math.max(minH, Math.round(b.width / curRatio));
      if (Math.abs(targetH - b.height) >= 2) {
        win.setBounds({ x: b.x, y: b.y, width: b.width, height: targetH });
      }
    } catch (e) {}
  }

  win.on('closed', () => {
    win = null;
    // 兜底清理所有常驻隐藏窗口（neteaseWin 等 `show:false` 会话窗），
    // 否则 window-all-closed 永不触发 → 进程残留后台不退出
    destroyHiddenWindows();
  });

  /* 生产环境（app.isPackaged）禁用 DevTools：任何方式打开都会立即关掉（v28 优化 #3） */
  if (app.isPackaged) {
    win.webContents.on('devtools-opened', () => { try { win.webContents.closeDevTools(); } catch (e) {} });
  }
}

// 本地文件协议：local://file/<base64url 绝对路径>
app.whenReady().then(() => {
  migrateOldUserData();
  vaultLoad();   // 必须在迁移之后加载，否则旧内存会覆盖迁移结果
  loadBiliCookie();
  miniLoadBounds();
  setupBiliHeadersInjection();
  protocol.handle('local', async (req) => {
    try {
      const u = new URL(req.url);
      const b64 = u.pathname.replace(/^\//, '');
      const filePath = Buffer.from(b64, 'base64url').toString('utf8');
      const stat = await fsp.stat(filePath);
      if (!stat.isFile()) throw new Error('not a file');

      const range = req.headers.get('range');
      let start = 0, end = stat.size - 1, code = 200;
      if (range) {
        const m = /bytes=(\d*)-(\d*)/.exec(range);
        if (m) {
          code = 206;
          if (m[1]) start = parseInt(m[1], 10);
          if (m[2]) end = Math.min(parseInt(m[2], 10), stat.size - 1);
          if (start > end) { start = 0; end = stat.size - 1; }
        }
      }
      const headers = {
        'Content-Type': 'video/mp4',
        'Content-Length': String(end - start + 1),
        'Accept-Ranges': 'bytes',
        'Content-Range': `bytes ${start}-${end}/${stat.size}`
      };
      const stream = Readable.toWeb(fs.createReadStream(filePath, { start, end }));
      return new Response(stream, { status: code, headers });
    } catch (e) {
      return new Response('not found', { status: 404 });
    }
  });

  createWindow();
  ensureTray();   // 右下角系统托盘（主窗 ✕ → 隐藏驻留；托盘右键「退出」才真退出）

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  vaultFlush();                     // 退出前落盘：vaultSave 节流后未提交的数据在此同步写出
  if (process.platform !== 'darwin') app.exit(0);   // 强制退出：不残留后台进程（否则单实例锁导致下次打不开）
});

// 系统关机/注销等真实退出：放行 close 拦截（quitting=true），避免驻留逻辑卡住系统退出
app.on('before-quit', () => { quitting = true; });

// 单实例
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.exit(0);
} else {
  app.on('second-instance', () => {
    // 已有窗口则置前；进程残留但窗口没了则重建窗口（修复"退出一次后打不开"）
    if (win && !win.isDestroyed()) {
      if (win.isMinimized()) win.restore();
      if (!win.isVisible()) win.show();
      win.focus();
    } else {
      createWindow();
    }
  });
}

/* ---------- IPC：窗口控制 ---------- */
ipcMain.handle('win-min', () => { win && win.minimize(); });
ipcMain.handle('win-max', () => {
  if (!win) return;
  if (win.isMaximized()) win.unmaximize(); else win.maximize();
});
ipcMain.handle('win-close', () => { win && win.close(); });
ipcMain.handle('win-pin', () => {
  if (!win) return pinned;
  pinned = !pinned;
  win.setAlwaysOnTop(pinned, 'floating');
  return pinned;
});

/* ---------- IPC：迷你悬浮（真窗口缩小 + 置顶 + 可拖动/记忆位置） ---------- */
const MINI_FILE = path.join(app.getPath('userData'), 'mini_bounds.json');
let miniBounds = null;
/* 尺寸净化：防止上次误存了最大化/超大窗口尺寸（如 1936x1048）被当成迷你尺寸。
   若存储尺寸超过屏幕 55%（说明不是迷你尺寸，是上次 bug 存坏的全窗尺寸），一律视为无效回退默认。 */
function sanitizeMiniBounds(b) {
  if (!b || typeof b !== 'object') return null;
  try {
    const { screen } = require('electron');
    const wa = screen.getPrimaryDisplay().workArea;
    const w = Math.round(b.width) || 0, h = Math.round(b.height) || 0;
    if (w > wa.width * 0.55 || h > wa.height * 0.55) return null;
    const ww = Math.max(280, Math.min(w, Math.round(wa.width * 0.5)));
    const hh = Math.max(160, Math.min(h, Math.round(wa.height * 0.5)));
    return { x: Math.round(b.x) || 0, y: Math.round(b.y) || 0, width: ww, height: hh };
  } catch (e) { return null; }
}
function miniLoadBounds() {
  try { miniBounds = sanitizeMiniBounds(JSON.parse(fs.readFileSync(MINI_FILE, 'utf8'))); } catch (e) { miniBounds = null; }
}
function miniSaveBounds() {
  try {
    if (mini && win && !win.isDestroyed()) {
      const b = sanitizeMiniBounds(win.getBounds());
      if (b) { miniBounds = b; fs.writeFileSync(MINI_FILE, JSON.stringify(b)); }
    }
  } catch (e) {}
}
/* moved / resized 在用户拖动窗口时一秒触发几十次，每次都同步落盘会引起卡顿。
   改为尾沿 250ms 防抖，落盘真正执行 miniSaveBounds()。 */
let miniSaveTimer = null;
function debouncedMiniSaveBounds() {
  if (miniSaveTimer) clearTimeout(miniSaveTimer);
  miniSaveTimer = setTimeout(() => { miniSaveTimer = null; miniSaveBounds(); }, 250);
}
function clampMiniBounds(b) {
  try {
    const { screen } = require('electron');
    const wa = screen.getDisplayMatching(b).workArea;
    b.x = Math.max(wa.x - 40, Math.min(wa.x + wa.width - 60, b.x));
    b.y = Math.max(wa.y - 40, Math.min(wa.y + wa.height - 40, b.y));
  } catch (e) {}
  return b;
}
ipcMain.handle('set-mini', (_e, on) => {
  if (!win) return;
  mini = !!on;
  // mini 悬浮时隐藏托盘图标（用户诉求：右下角图标仅主窗模式驻留时出现）
  if (mini) destroyTray(); else ensureTray();
  if (mini) {
    if (!win.isMaximized()) normalBounds = win.getBounds();
    let b = miniBounds;
    if (!b) {
      const { screen } = require('electron');
      const wa = screen.getPrimaryDisplay().workArea;
      b = { x: wa.x + wa.width - 480 - 18, y: wa.y + 18, width: 480, height: 270 };   // 迷你窗也 16:9
    }
    win.setMinimumSize(288, 162);   // 288×162 严格 16:9（288÷16×9=162），与 setAspectRatio 兼容
    win.setBounds(clampMiniBounds(Object.assign({}, b)));
    win.setAlwaysOnTop(true, 'floating');
  } else {
    if (normalBounds) win.setBounds(normalBounds);
    win.setMinimumSize(928, 522);   // 928×522 严格 16:9，与 setAspectRatio 兼容（v28.4b「锁死」真因见 createWindow 注释）
    win.setAlwaysOnTop(pinned, 'floating');
  }
});

/* ---------- IPC：选择本地视频文件 ---------- */
ipcMain.handle('pick-videos', async () => {
  if (!win) return [];
  const r = await dialog.showOpenDialog(win, {
    title: '选择要加入电台的 MV 视频文件',
    properties: ['openFile', 'multiSelections'],
    filters: [
      { name: '视频文件', extensions: ['mp4', 'mkv', 'webm', 'mov', 'avi', 'flv', 'm4v'] },
      { name: '所有文件', extensions: ['*'] }
    ]
  });
  if (r.canceled || !r.filePaths) return [];
  return r.filePaths.map((p) => ({
    name: path.basename(p),
    url: 'local://file/' + Buffer.from(p).toString('base64url')
  }));
});

/* =====================================================================
   B 站模块：登录 / 解析直链 / 搜索 / 状态
   ===================================================================== */
const BILI_COOKIE_FILE = path.join(app.getPath('userData'), 'bili_cookies.json');
const BILI_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36';
let biliCookie = '';   // "k=v; k=v; ..."
let biliUser = null;   // {name, face, level}

function biliHeaders() {
  const h = { 'User-Agent': BILI_UA, 'Referer': 'https://www.bilibili.com/' };
  if (biliCookie) h['Cookie'] = biliCookie;
  return h;
}
function saveBiliCookie() {
  try { fs.writeFileSync(BILI_COOKIE_FILE, JSON.stringify({ cookie: biliCookie, user: biliUser })); } catch (e) {}
}
function loadBiliCookie() {
  try {
    const d = JSON.parse(fs.readFileSync(BILI_COOKIE_FILE, 'utf8'));
    if (d.cookie) biliCookie = d.cookie;
    if (d.user) biliUser = d.user;
  } catch (e) {}
}
async function biliApi(url, params) {
  try {
    const qs = new URLSearchParams(params || {}).toString();
    const res = await net.fetch(url + (qs ? '?' + qs : ''), { headers: biliHeaders() });
    const txt = await res.text();
    try { return JSON.parse(txt); } catch (e) { return { code: -1, message: '响应解析失败' }; }
  } catch (e) {
    return { code: -2, message: '网络错误：' + (e && e.message ? e.message : e) };
  }
}
async function biliStatus() {
  if (!biliCookie) return { loggedIn: false };
  const d = await biliApi('https://api.bilibili.com/x/web-interface/nav');
  if (d && d.code === 0 && d.data && d.data.isLogin) {
    biliUser = {
      name: d.data.uname || '',
      face: d.data.face || '',
      level: d.data.level_info ? d.data.level_info.current_level : 0
    };
    saveBiliCookie();
    return { loggedIn: true, name: biliUser.name, level: biliUser.level, face: biliUser.face };
  }
  biliCookie = ''; biliUser = null; saveBiliCookie();
  return { loggedIn: false };
}
async function biliResolve(bvid) {
  const d = await biliApi('https://api.bilibili.com/x/web-interface/view', { bvid });
  if (!d || d.code !== 0) return { ok: false, msg: '视频不存在或不可播放' };
  const data = d.data;
  const cid = data.pages && data.pages[0] ? data.pages[0].cid : 0;
  if (!cid) return { ok: false, msg: '无法获取 cid' };
  for (const qn of biliQnOrder()) {
    const p = await biliApi('https://api.bilibili.com/x/player/playurl', { bvid, cid, qn, fnval: 0, fnver: 0 });
    if (p && p.code === 0 && p.data && p.data.durl && p.data.durl.length) {
      return { ok: true, url: p.data.durl[0].url, title: data.title, dur: data.duration || 0, cid, qn };
    }
  }
  return { ok: false, msg: biliCookie ? '该视频清晰度受限' : '未登录时仅提供低清晰度，建议登录B站获取高清' };
}
/* 获取视频分P列表（用于把「OP/ED合集」拆成单曲电台） */
async function biliPages(bvid) {
  const d = await biliApi('https://api.bilibili.com/x/web-interface/view', { bvid });
  if (!d || d.code !== 0 || !d.data) return { ok: false, msg: (d && d.message) || '视频不存在' };
  const pages = (d.data.pages || []).map((p, i) => ({
    p: i + 1, cid: p.cid, part: (p.part || '').replace(/<[^>]+>/g, '') || ('第' + (i + 1) + 'P'), dur: p.duration || 0
  }));
  return { ok: true, title: d.data.title, pages };
}
/* 按指定分P(cid)解析直链（合集电台用） */
async function biliResolveCid(bvid, cid, part) {
  if (!cid) return { ok: false, msg: 'cid 无效' };
  for (const qn of biliQnOrder()) {
    const p = await biliApi('https://api.bilibili.com/x/player/playurl', { bvid, cid, qn, fnval: 0, fnver: 0 });
    if (p && p.code === 0 && p.data && p.data.durl && p.data.durl.length) {
      return { ok: true, url: p.data.durl[0].url, part: part || '', qn };
    }
  }
  return { ok: false, msg: biliCookie ? '该分P清晰度受限' : '未登录时仅提供低清晰度' };
}
/* 画质档位顺序：选定档→兜底；自动=未登录标清起步/登录高清起步（更快更稳） */
function biliQnOrder() {
  if (biliQn > 0) return [biliQn, 80, 64, 32].filter((v, i, a) => a.indexOf(v) === i);
  return biliCookie ? [80, 64, 32] : [32, 64, 16];
}
/* 获取视频信息（含封面图，用于频道卡片图片填充） */
async function biliView(bvid) {
  const d = await biliApi('https://api.bilibili.com/x/web-interface/view', { bvid });
  if (!d || d.code !== 0 || !d.data) return { ok: false, msg: (d && d.message) || '视频不存在' };
  return {
    ok: true,
    pic: d.data.pic || '',
    title: (d.data.title || '').replace(/<[^>]+>/g, ''),
    author: (d.data.owner && d.data.owner.name) || ''
  };
}
async function biliSearch(keyword, page) {
  const pn = Math.max(1, Number(page) || 1);
  // search/type 允许 ps 最高 50；每次拉一页，取足量结果用于曲库扩充
  const d = await biliApi('https://api.bilibili.com/x/web-interface/search/type',
                          { search_type: 'video', keyword, ps: 50, pn });
  if (!d || d.code !== 0) return { ok: false, msg: '搜索失败：' + ((d && d.message) || '请稍后再试或先登录') };
  const list = ((d.data && d.data.result) || []).map((it) => ({
    bvid: it.bvid || '',
    title: (it.title || '').replace(/<[^>]+>/g, ''),
    duration: it.duration || '',
    author: it.author || '',
    play: it.play || 0
  })).filter((it) => it.bvid);
  return { ok: true, list };
}
function biliLogout() {
  biliCookie = ''; biliUser = null; saveBiliCookie();
  return true;
}

// 登录：打开独立 session 的 B 站登录窗口，等待登录成功后取回 cookie
async function biliLogin() {
  if (!win) return { ok: false, msg: '主窗口未就绪' };
  const ses = session.fromPartition('persist:bilibili');
  await ses.clearStorageData({ storages: ['cookies'] });
  // v29.8 登录页乱码根因：老版本（h5 移动页时代）在分区会话里留下的**过期 CSS 缓存**——
  // 每次点登录只清了 Cookie，样式文件命中坏缓存 → 页面裸奔成"乱码代码"、二维码掉到页底。
  // 连 HTTP 缓存一起清 + 会话级桌面 UA/中文语言头（与实测正常的探针配置完全一致）。
  try { await ses.clearCache(); } catch (e) {}
  try { ses.setUserAgent(BILI_UA, 'zh-CN,zh;q=0.9'); } catch (e) {}
  const win2 = new BrowserWindow({
    width: 1000, height: 720, title: '登录B站 - KisstrTV',
    parent: win, modal: false, show: false, autoHideMenuBar: true, center: true,
    backgroundColor: '#ffffff',
    webPreferences: { session: ses, contextIsolation: true, nodeIntegration: false, spellcheck: false }
  });
  win2.loadURL('https://passport.bilibili.com/login', { userAgent: BILI_UA });
  win2.once('ready-to-show', () => { if (!win2.isDestroyed()) win2.show(); });
  // 兜底：页面加载慢/卡住时也强制显示登录窗口，避免"点了没反应"
  setTimeout(() => { if (!win2.isDestroyed()) win2.show(); }, 2500);
  win2.webContents.on('did-fail-load', (_e, code, desc) => { console.log('BILI_LOGIN_LOAD_FAIL', code, String(desc).slice(0, 80)); });
  return new Promise((resolve) => {
    let done = false;
    const finish = (ok, msg) => {
      if (done) return;
      done = true;
      clearInterval(timer);
      if (!win2.isDestroyed()) win2.destroy();
      resolve(ok ? { ok: true, user: biliUser } : { ok: false, msg: msg || '未完成登录' });
    };
    const timer = setInterval(async () => {
      try {
        const cookies = await ses.cookies.get({ url: 'https://www.bilibili.com' });
        if (cookies.some((c) => c.name === 'SESSDATA')) {
          biliCookie = cookies.map((c) => c.name + '=' + c.value).join('; ');
          biliUser = null;
          await biliStatus();
          finish(true);
        }
      } catch (e) {}
    }, 1200);
    win2.on('closed', () => finish(!!biliCookie, '登录窗口已关闭'));
  });
}

// 给 B 站 / 网易云 CDN 请求补 Referer/UA（否则 <video> 拉直链会 403）
function setupBiliHeadersInjection() {
  session.defaultSession.webRequest.onBeforeSendHeaders((details, cb) => {
    try {
      const u = details.url || '';
      if (/\.(bilivideo\.com|hdslb\.com|biliapi\.net|bilibili\.com|mcdn\.bilivideo\.cn)/i.test(u)) {
        details.requestHeaders['Referer'] = 'https://www.bilibili.com/';
        details.requestHeaders['User-Agent'] = BILI_UA;
      } else if (/(music\.163\.com|music\.126\.net|v\.nosdn\.127\.net|m801\.music\.126\.net|mv001\.netease\.im|interface\.music\.163\.com|y\.music\.163\.com)/i.test(u)) {
        details.requestHeaders['Referer'] = 'https://music.163.com/';
        details.requestHeaders['User-Agent'] = NETEASE_UA;
      }
    } catch (e) {}
    cb({ requestHeaders: details.requestHeaders });
  });
}

/* ---------- IPC：渲染层就绪（可自动开播） ---------- */
ipcMain.handle('app-ready', () => true);

/* ---------- IPC：全屏（双击视频进入 / ESC 退出） ---------- */
ipcMain.handle('toggle-fullscreen', () => {
  if (!win) return false;
  const fs = !win.isFullScreen();
  win.setFullScreen(fs);
  return fs;
});
ipcMain.handle('set-fullscreen', (_e, on) => {
  if (win) win.setFullScreen(!!on);
  return true;
});

/* ---------- IPC：B 站 ---------- */
/* 整窗拖拽（JS 版）：视频上按住拖动窗口（纯拖拽，不做自动吸附，避免卡顿）；
   最大化的窗口按住拖拽会先还原为普通大小再跟随 */
let winDragOff = null;
function winRestorePreDrag() {
  try { if (win.isMaximized()) win.unmaximize(); } catch (e) {}
}
ipcMain.handle('win-drag', (_e, act, sx, sy) => {
  try {
    if (act === 'start') {
      winRestorePreDrag();
      const b = win.getBounds();
      winDragOff = { dx: sx - b.x, dy: sy - b.y };
    }
    else if (act === 'move' && winDragOff) {
      win.setPosition(Math.round(sx - winDragOff.dx), Math.round(sy - winDragOff.dy));
    }
    else if (act === 'end') { winDragOff = null; }
    return true;
  } catch (e) { return false; }
});
/* 无边框窗边缘缩放：渲染层热区拖拽 → 主进程 setBounds（OS 原生缩放仍可用于四角，这里补四边 + 光标） */
ipcMain.on('win-bounds', (e) => {
  try { e.returnValue = win && !win.isDestroyed() ? win.getBounds() : null; }
  catch (err) { e.returnValue = null; }
});
ipcMain.on('win-set-bounds', (_e, b) => {
  try {
    if (!win || win.isDestroyed() || !b) return;
    win.setBounds({
      x: Math.round(b.x), y: Math.round(b.y),
      width: Math.round(b.width), height: Math.round(b.height)
    });
  } catch (err) {}
});
ipcMain.handle('bili-status', () => biliStatus());
ipcMain.handle('bili-login', () => biliLogin());
ipcMain.handle('bili-logout', () => biliLogout());
ipcMain.handle('bili-resolve', (_e, bvid) => biliResolve(String(bvid || '')));
ipcMain.handle('bili-search', (_e, kw, page) => biliSearch(String(kw || ''), Number(page) || 1));
ipcMain.handle('bili-pages', (_e, bvid) => biliPages(String(bvid || '')));
ipcMain.handle('bili-view', (_e, bvid) => biliView(String(bvid || '')));
ipcMain.handle('bili-resolve-cid', (_e, bvid, cid, part) => biliResolveCid(String(bvid || ''), Number(cid) || 0, String(part || '')));
/* 画质偏好：0=自动（未登录→标清32，登录→高清64），16/32/64/80=指定档位 */
let biliQn = 0;
try { const f = fs.readFileSync(path.join(app.getPath('userData'), 'bili_qual.txt'), 'utf8'); const n = parseInt(f, 10); if ([0, 16, 32, 64, 80].includes(n)) biliQn = n; } catch (e) {}
ipcMain.handle('bili-quality', (_e, qn) => { biliQn = [0, 16, 32, 64, 80].includes(Number(qn)) ? Number(qn) : 0; try { fs.writeFileSync(path.join(app.getPath('userData'), 'bili_qual.txt'), String(biliQn)); } catch (e) {} return biliQn; });
ipcMain.handle('bili-quality-get', () => biliQn);

/* =====================================================================
   曲库系统（vault）：本地持久化的海量音乐/动漫 MV 池
   - 来源① 相关视频链（archive/related）：从种子 MV 出发 BFS 扩散，匿名稳定、天然同主题
   - 来源② 站内热门（popular）：全站热门里过滤音乐/动漫相关
   - 来源③ 关键词搜索（biliSearch，尽力而为，登录后更稳）
   - 去重按 bvid；存 userData/vault.json，越用越多，二次打开即有完整曲库
   ===================================================================== */
const VAULT_FILE = path.join(app.getPath('userData'), 'vault.json');
let vault = { items: [] };
const MUSIC_TID_SET = new Set([3, 1, 3, 4, 30, 20, 6, 31, 24, 25, 47, 86, 27, 119, 129, 154, 156, 157, 155, 23, 122, 51]);
function isMusicLike(tname, title) {
  const n = (tname || '') + '|' + (title || '');
  return /音乐|MV|OP|ED|OST|BGM|翻唱|VOCALOID|vocaloid|宅舞|AMV|MAD|歌|曲|舞|live|Live|现场|演奏|cover|Cover|歌姬|sing|Music/.test(n);
}
function vaultLoad() {
  try {
    const d = JSON.parse(fs.readFileSync(VAULT_FILE, 'utf8'));
    if (d && Array.isArray(d.items)) vault = d;
    else vault = { items: [] };
  } catch (e) { vault = { items: [] }; }
}
/* 节流版：vaultAdd 高峰期一秒内可能触发数十次，合并成 1 次同步写盘；
   退出前调用 vaultFlush() 强制立即落盘，避免数据丢失 */
let vaultDirty = false;
let vaultSaveTimer = null;
const VAULT_SAVE_DELAY = 1000;
function vaultSave() {
  vaultDirty = true;
  if (vaultSaveTimer) return;       // 已有待写 timer，复用
  vaultSaveTimer = setTimeout(() => {
    vaultSaveTimer = null;
    if (!vaultDirty) return;
    vaultDirty = false;
    try { fs.writeFileSync(VAULT_FILE, JSON.stringify(vault)); } catch (e) {}
  }, VAULT_SAVE_DELAY);
}
function vaultFlush() {              // 同步落盘：取消待写 timer，立即写一次
  if (vaultSaveTimer) { clearTimeout(vaultSaveTimer); vaultSaveTimer = null; }
  if (!vaultDirty) return;
  vaultDirty = false;
  try { fs.writeFileSync(VAULT_FILE, JSON.stringify(vault)); } catch (e) {}
}
function vaultAdd(list, theme, src) {
  const known = new Set(vault.items.map((x) => x.bvid));
  let added = 0;
  (list || []).forEach((it) => {
    if (!it.bvid || known.has(it.bvid)) return;
    known.add(it.bvid);
    vault.items.push({
      bvid: it.bvid, title: it.title || '', author: it.author || '',
      dur: it.dur || 0, theme: theme || 'hot', src: src || 'related', ts: Date.now()
    });
    added++;
  });
  if (added) { vaultSave(); if (vault.items.length > 60000) { vault.items = vault.items.slice(-60000); vaultSave(); } }
  return added;
}
function vaultPull(theme, limit) {
  const want = theme && theme !== 'hot';
  let arr = want ? vault.items.filter((x) => x.theme === theme) : vault.items.filter((x) => x.theme === 'hot');
  if (want) {
    if (arr.length < (limit || 30)) {
      const hot = vault.items.filter((x) => x.theme === 'hot');
      arr = arr.concat(hot).slice(0, limit || 30);
    }
  }
  arr.sort(() => Math.random() - 0.5);
  return arr.slice(0, limit || 40).map((x) => ({ bvid: x.bvid, title: x.title, author: x.author, dur: x.dur }));
}
async function biliRelated(bvid) {
  const d = await biliApi('https://api.bilibili.com/x/web-interface/archive/related', { bvid });
  if (d && d.code === 0 && Array.isArray(d.data)) {
    const list = d.data.map((it) => {
      const title = (it.title || '').replace(/<[^>]+>/g, '');
      return {
        bvid: it.bvid || '', title, author: (it.author || '').split('"')[0] || '',
        dur: it.duration || 0, tname: it.tname || '', music: isMusicLike(it.tname || '', title)
      };
    }).filter((it) => it.bvid && it.music && it.dur >= 45 && it.dur <= 1200);
    return { ok: true, list };
  }
  return { ok: false, msg: (d && d.message) || 'related 失败' };
}
let exploreBusy = false;
async function vaultExplore(theme, seeds, maxNodes) {
  if (exploreBusy) return { busy: true, added: 0, total: vault.items.length };
  exploreBusy = true;
  try {
    maxNodes = Math.min(Math.max(maxNodes || 30, 6), 150);
    const queue = (seeds || []).filter(Boolean).slice(0, 14);
    const seen = new Set(queue);
    let nodes = 0, added = 0;
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    while (queue.length && nodes < maxNodes) {
      const bv = queue.shift();
      nodes++;
      try {
        const r = await biliRelated(bv);
        if (r && r.ok && r.list) {
          const fresh = r.list.filter((it) => !seen.has(it.bvid));
          fresh.slice(0, 8).forEach((it) => seen.add(it.bvid));
          added += vaultAdd(fresh, theme, 'related');
          if (queue.length < maxNodes * 2) fresh.slice(0, 3).forEach((it) => queue.push(it.bvid));
        }
      } catch (e) {}
      await sleep(500);
    }
    return { busy: false, added, total: vault.items.length };
  } finally { exploreBusy = false; }
}
ipcMain.handle('vault-stats', () => ({ total: vault.items.length }));
ipcMain.handle('vault-pull', (_e, theme, limit) => vaultPull(String(theme || ''), Number(limit) || 30));
ipcMain.handle('vault-explore', (_e, theme, seeds, maxNodes) => vaultExplore(String(theme || 'hot'), Array.isArray(seeds) ? seeds : [], Number(maxNodes) || 30));
ipcMain.handle('vault-popular', async () => {
  let added = 0;
  for (let pn = 1; pn <= 34; pn++) {
    const d = await biliApi('https://api.bilibili.com/x/web-interface/popular', { ps: 20, pn });
    if (d && d.code === 0 && d.data && d.data.list && d.data.list.length) {
      const list = d.data.list.map((it) => ({
        bvid: it.bvid || '', title: (it.title || '').replace(/<[^>]+>/g, ''),
        author: it.owner ? it.owner.name : '', dur: it.duration || 0, tname: it.tname || ''
      })).filter((it) => it.bvid && isMusicLike(it.tname, it.title) && it.dur >= 45 && it.dur <= 1200);
      added += vaultAdd(list, 'hot', 'popular');
    } else break;
    await new Promise((r) => setTimeout(r, 450));
  }
  return { added, total: vault.items.length };
});

/* =====================================================================
   网易云音乐模块：登录 / 搜索 MV / 解析直链
   思路：登录窗口与 API 会话页共用 persist:netease 会话。
   所有接口调用都在真实 music.163.com 页面上下文内 fetch（同源 + 登录 cookie），
   这是匿名被风控、登录后最稳定的路径。
   ===================================================================== */
const NETEASE_SESSION_PARTITION = 'persist:netease';
const NETEASE_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36';
let neteaseWin = null;        // 登录后隐藏复用为 API 会话页
let neteaseLoggedIn = false;

// 统一销毁所有常驻隐藏窗口（`show:false` 的会话页），真正退出时调用，避免残留句柄
function destroyHiddenWindows() {
  for (const w of [neteaseWin]) {
    if (w && !w.isDestroyed()) { try { w.destroy(); } catch (e) {} }
  }
  if (neteaseWin && neteaseWin.isDestroyed()) neteaseWin = null;
}

/* ---------- 系统托盘（右下角进程图标） ---------- */
// 语义：点主窗 ✕ / Alt+F4 = 隐藏到托盘驻留（后台继续放歌）；真退出只能从托盘右键「退出」。
// 托盘图标常驻（mini 悬浮同样显示），保证任何时刻都有退出入口。
function trayIconPath() {
  return path.join(__dirname, 'assets', isWin ? 'icon.ico' : 'icon.png');
}
function ensureTray() {
  if (tray) return tray;
  try {
    let img;
    try { img = nativeImage.createFromPath(trayIconPath()); } catch (e) { img = nativeImage.createEmpty(); }
    if (img.isEmpty()) { try { img = nativeImage.createFromPath(path.join(__dirname, 'assets', 'icon-32.png')); } catch (e) {} }
    tray = new Tray(img);
    tray.setToolTip('KisstrTV — 右键退出');
    rebuildTrayMenu();
    tray.on('click', () => { if (win) { if (!win.isVisible()) win.show(); if (win.isMinimized()) win.restore(); win.focus(); } });
    return tray;
  } catch (e) { console.error('TRAY_ERR', e && e.message); return null; }
}
function rebuildTrayMenu() {
  if (!tray) return;
  const menu = Menu.buildFromTemplate([
    { label: '显示 KisstrTV', click: () => { if (win) { if (!win.isVisible()) win.show(); if (win.isMinimized()) win.restore(); win.focus(); } } },
    { type: 'separator' },
    { label: '退出', click: () => doQuit() }
  ]);
  tray.setContextMenu(menu);
}
function destroyTray() {
  if (tray) { try { tray.destroy(); } catch (e) {} tray = null; }
}
// 真正退出：置 quitting → 强杀，绕过 window-all-closed 对隐藏窗的依赖
function doQuit() {
  quitting = true;
  try { destroyTray(); } catch (e) {}
  try { if (win && !win.isDestroyed()) win.destroy(); } catch (e) {}
  destroyHiddenWindows();
  app.exit(0);
}

function neteaseSession() { return session.fromPartition(NETEASE_SESSION_PARTITION); }

async function neteaseHasLoginCookie() {
  try {
    const cookies = await neteaseSession().cookies.get({ url: 'https://music.163.com' });
    return cookies.some((c) => c.name === 'MUSIC_U' && c.value);
  } catch (e) { return false; }
}

// 确保存在一个加载了 music.163.com 的隐藏窗口作为 API 会话页
async function neteaseEnsurePage() {
  if (neteaseWin && !neteaseWin.isDestroyed()) return neteaseWin;
  neteaseWin = new BrowserWindow({
    show: false,
    webPreferences: { session: neteaseSession(), contextIsolation: true, nodeIntegration: false, sandbox: true }
  });
  await neteaseWin.loadURL('https://music.163.com/');
  await new Promise((r) => setTimeout(r, 1200)); // 等页面 JS 建立 cookie
  return neteaseWin;
}

let neteaseUser = null;   // {name, face} 登录后在曲库页可见，用于确认账号真的生效
async function neteaseFetchProfile(){
  try{
    const r = await neteasePageFetch('/api/nuser/account/get', { credentials:'include' });
    const j = r && r.json;
    const p = j && j.profile;
    if (p) return { name: p.nickname || '', face: p.avatarUrl || (p.avatarDetail && p.avatarDetail.url) || '' };
  }catch(e){}
  return null;
}
async function neteaseStatus() {
  neteaseLoggedIn = await neteaseHasLoginCookie();
  if (!neteaseLoggedIn) { neteaseUser = null; return { loggedIn: false }; }
  if (!neteaseUser) neteaseUser = await neteaseFetchProfile();
  return { loggedIn: true, name: (neteaseUser && neteaseUser.name) || '', face: (neteaseUser && neteaseUser.face) || '' };
}

// 登录：打开可见窗口让用户登录，成功后该会话复用给 API 页
async function neteaseLogin() {
  if (await neteaseHasLoginCookie()) { neteaseLoggedIn = true; neteaseUser = null; neteaseEnsurePage(); return { ok: true, already: true }; }
  const ses = neteaseSession();
  // v29.8 与 B站登录同款修复：清缓存 + 会话级桌面 UA/中文语言头（防旧缓存导致页面裸奔乱码）
  try { await ses.clearCache(); } catch (e) {}
  try { ses.setUserAgent(NETEASE_UA, 'zh-CN,zh;q=0.9'); } catch (e) {}
  const win2 = new BrowserWindow({
    width: 960, height: 720, title: '登录网易云音乐 - KisstrTV',
    parent: win, modal: false, show: false, autoHideMenuBar: true, center: true,
    backgroundColor: '#ffffff',
    webPreferences: { session: ses, contextIsolation: true, nodeIntegration: false, sandbox: true, spellcheck: false }
  });
  win2.loadURL('https://music.163.com/#/login', { userAgent: NETEASE_UA });
  win2.once('ready-to-show', () => { if (!win2.isDestroyed()) win2.show(); });
  setTimeout(() => { if (!win2.isDestroyed()) win2.show(); }, 2500);
  win2.webContents.on('did-fail-load', (_e, code, desc) => { console.log('NETEASE_LOGIN_LOAD_FAIL', code, String(desc).slice(0, 80)); });
  return new Promise((resolve) => {
    let done = false;
    const finish = (ok, msg) => {
      if (done) return;
      done = true;
      clearInterval(timer);
      if (!win2.isDestroyed()) win2.destroy();
      if (ok) { neteaseLoggedIn = true; neteaseUser = null; neteaseEnsurePage(); resolve({ ok: true }); }
      else resolve({ ok: false, msg: msg || '未完成登录' });
    };
    const timer = setInterval(async () => {
      try { if (await neteaseHasLoginCookie()) finish(true); } catch (e) {}
    }, 1500);
    win2.on('closed', () => { if (!neteaseLoggedIn) finish(false, '登录窗口已关闭'); });
  });
}

async function neteaseLogout() {
  neteaseUser = null;
  try { await neteaseSession().clearStorageData({ storages: ['cookies'] }); } catch (e) {}
  neteaseLoggedIn = false;
  if (neteaseWin && !neteaseWin.isDestroyed()) { neteaseWin.destroy(); neteaseWin = null; }
  return true;
}

// 在网易云页面上下文执行 fetch（同源 + 自动带登录 cookie）
async function neteasePageFetch(path, opts) {
  try {
    const w = await neteaseEnsurePage();
    const js = `(async()=>{
      try {
        const r = await fetch(${JSON.stringify(path)}, ${JSON.stringify(opts || {})});
        const t = await r.text();
        let j = null; try { j = JSON.parse(t); } catch(e){}
        return JSON.stringify({ status: r.status, json: j, text: t.slice(0,200) });
      } catch(e) { return JSON.stringify({ status: -1, json: null, text: 'ERR:' + (e.message||e) }); }
    })()`;
    const out = await w.webContents.executeJavaScript(js);
    return JSON.parse(out);
  } catch (e) {
    return { status: -2, json: null, text: '会话页错误：' + (e.message || e) };
  }
}

async function neteaseSearch(kw) {
  try {
    // csrf_token 从页面 cookie 取，body 在页面内拼
    const js = `(async()=>{
      try {
        const m = document.cookie.match(/(?:^|; )__csrf=([^;]+)/);
        const csrf = m ? m[1] : '';
        const body = 's=' + encodeURIComponent(${JSON.stringify(kw)}) + '&type=1004&offset=0&total=true&limit=15&csrf_token=' + csrf;
        const r = await fetch('/api/search/get/web', { method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: body, credentials:'include' });
        const t = await r.text();
        let j = null; try { j = JSON.parse(t); } catch(e){}
        return JSON.stringify({ status: r.status, json: j, text: t.slice(0,120) });
      } catch(e) { return JSON.stringify({ status: -1, json: null, text: 'ERR:' + (e.message||e) }); }
    })()`;
    const w = await neteaseEnsurePage();
    const out = JSON.parse(await w.webContents.executeJavaScript(js));
    const j = out.json;
    if (!j || j.code !== 200) return { ok: false, msg: '搜索失败（' + ((j && j.code) || '') + '），请先登录网易云' };
    const mvs = (j.result && j.result.mvs) || [];
    const list = mvs.slice(0, 12).map((m) => ({
      id: m.id, name: m.name || '', artist: m.artistName || '', play: m.playCount || 0, dur: m.duration || 0
    }));
    return { ok: true, list };
  } catch (e) {
    return { ok: false, msg: '搜索出错：' + (e.message || e) };
  }
}

async function neteaseResolve(mid) {
  const r = await neteasePageFetch('/api/mv/detail?id=' + String(mid) + '&type=mp4', { credentials: 'include' });
  const j = r.json;
  if (!j || j.code !== 200 || !j.data) return { ok: false, msg: 'MV解析失败（' + ((j && j.code) || r.status) + '）' };
  const brs = j.data.brs || {};
  let url = null;
  for (const br of ['1080', '720', '480', '240']) { if (brs[br]) { url = brs[br]; break; } }
  if (!url) return { ok: false, msg: '未找到可播放的 MV 地址' };
  return { ok: true, url, title: j.data.name || '' };
}

/* ---------- IPC：网易云 ---------- */
ipcMain.handle('netease-status', () => neteaseStatus());
ipcMain.handle('netease-login', () => neteaseLogin());
ipcMain.handle('netease-logout', () => neteaseLogout());
ipcMain.handle('netease-search', (_e, kw) => neteaseSearch(String(kw || '')));
ipcMain.handle('netease-resolve', (_e, mid) => neteaseResolve(String(mid || '')));

/* ---------- 冒烟测试：YYTV_SMOKE=1 时启动后自动退出 ---------- */
if (process.env.YYTV_SMOKE === '1') {
  app.whenReady().then(() => {
    setTimeout(() => { console.log('SMOKE_OK'); app.exit(0); }, 20000);
    setTimeout(() => {
      if (!win || !win.webContents) return;
      win.webContents.executeJavaScript(
        '(async function(){ try { var r = await window.audioAPI.vaultExplore("c_dongman", ["BV1Gr4nzFEAM"], 6); var s = await window.audioAPI.vaultStats(); return JSON.stringify({added:r.added, total:s.total}); } catch(e){ return "ERR:"+e.message; } })()'
      ).then((r) => { console.log('SMOKE_VAULT=' + r); }).catch((e) => { console.log('SMOKE_VAULTERR=' + (e && e.message)); });
      win.webContents.executeJavaScript(
        '(async function(){ try { var p = await window.audioAPI.biliPages("BV1x4411u7ot"); return JSON.stringify({ok:!!(p&&p.ok), n:(p&&p.pages?p.pages.length:0), first:(p&&p.pages&&p.pages[0]?p.pages[0].part:"")}); } catch(e){ return "ERR:"+e.message; } })()'
      ).then((r) => { console.log('SMOKE_PAGES=' + r); }).catch((e) => { console.log('SMOKE_PAGESERR=' + (e && e.message)); });
    }, 1200);
    // FM 端到端：切到动漫频道，等 12 秒看队列里有没有被拆出的分P单曲
    setTimeout(() => {
      if (!win || !win.webContents) return;
      win.webContents.executeJavaScript('(function(){ try { feedChannel("c_dongman", false); return "started"; } catch(e){ return "ERR:"+e.message; } })()')
        .then(() => {
          setTimeout(() => {
            win.webContents.executeJavaScript('window.__fmDebug ? window.__fmDebug() : "no-debug"')
              .then((r) => { console.log('SMOKE_FM=' + r); })
              .catch((e) => { console.log('SMOKE_FMERR=' + (e && e.message)); });
          }, 12000);
        }).catch((e) => { console.log('SMOKE_FMERR=' + (e && e.message)); });
    }, 1500);
    // 迷你窗：点「迷你」按钮进→验证尺寸/置顶→点右上 ✕ 关闭→还原（走真实 toggleMini 路径）
    setTimeout(async () => {
      try {
        await win.webContents.executeJavaScript('document.getElementById("btnMini").click()');
        await new Promise((r) => setTimeout(r, 700));
        const b1 = win.getBounds();
        const aot = win.isAlwaysOnTop();
        await win.webContents.executeJavaScript('(function(){ var b=document.getElementById("miniClose"); if(b){ b.click(); return "clicked"; } return "no-btn"; })()');
        await new Promise((r) => setTimeout(r, 700));
        const b2 = win.getBounds();
        console.log('SMOKE_MINI=' + JSON.stringify({ miniW: b1.width, miniH: b1.height, alwaysOnTop: aot, afterClickW: b2.width, afterClickH: b2.height }));
      } catch (e) { console.log('SMOKE_MINIERr=' + (e && e.message)); }
    }, 4000);
    // 真实鼠标事件诊断：迷你窗进→用 sendInputEvent 真实点击 ✕ → 检查是否还原
    setTimeout(async () => {
      try {
        await win.webContents.executeJavaScript('document.getElementById("btnMini").click()');
        await new Promise((r) => setTimeout(r, 700));
        const rect = await win.webContents.executeJavaScript('(function(){ var b=document.getElementById("miniClose"); var r=b.getBoundingClientRect(); return JSON.stringify({x:r.x+r.width/2, y:r.y+r.height/2, disp:getComputedStyle(b).display}); })()');
        const p = JSON.parse(rect);
        const pt = { x: Math.round(p.x), y: Math.round(p.y) };
        win.webContents.sendInputEvent({ type: 'mouseDown', x: pt.x, y: pt.y, button: 'left', clickCount: 1 });
        win.webContents.sendInputEvent({ type: 'mouseUp', x: pt.x, y: pt.y, button: 'left', clickCount: 1 });
        await new Promise((r) => setTimeout(r, 800));
        const b2 = win.getBounds();
        const bodyMini = await win.webContents.executeJavaScript('document.body.classList.contains("mini")');
        console.log('SMOKE_MINI2=' + JSON.stringify({ hit: pt, disp: p.disp, afterW: b2.width, afterH: b2.height, bodyMini }));
      } catch (e) { console.log('SMOKE_MINI2ERR=' + (e && e.message)); }
    }, 5500);
    // 片源按钮：真实点击 btnSrc → 检查 srcMask 是否 show；画质按钮 → 检查 label 变化
    setTimeout(async () => {
      try {
        const r = await win.webContents.executeJavaScript('(function(){ try{ document.getElementById("btnSrc").click(); var shown=document.getElementById("srcMask").classList.contains("show"); document.getElementById("srcMask").classList.remove("show"); var qb=document.getElementById("qualBtn"); var label0=qb.textContent; qb.click(); var label1=qb.textContent; var vn=document.getElementById("vaultNum").textContent; return JSON.stringify({srcShown:shown, qualBefore:label0, qualAfter:label1, vaultNum:vn}); }catch(e){ return "ERR:"+e.message; } })()');
        console.log('SMOKE_SRC=' + r);
      } catch (e) { console.log('SMOKE_SRCERR=' + (e && e.message)); }
    }, 7000);
    // 整窗拖拽 IPC + 音量条 + 迷你关闭按钮（苹果红点）+ 设置弹窗 + 磨砂圆角窗控
    setTimeout(async () => {
      try {
        const r = await win.webContents.executeJavaScript('(async function(){ try{ var p1=await window.audioAPI.winDrag("start",600,400); var p2=await window.audioAPI.winDrag("move",660,450); var p3=await window.audioAPI.winDrag("end"); var mc=document.querySelector(".mini-close svg line"); var volBtnHasPct=document.getElementById("volBtn").querySelectorAll(".pv").length; var rb=document.getElementById("statusbar").querySelector(".rightbar"); var histRight= rb ? rb.textContent.replace(/\\s+/g," ").trim() : "none"; document.getElementById("btnSet").click(); var setShown=document.getElementById("setMask").classList.contains("show"); document.getElementById("setClose").click(); var cc=getComputedStyle(document.getElementById("btnClose")).backgroundColor; var mc2=getComputedStyle(document.querySelector(".mini-close")).backgroundColor; var vr=document.getElementById("volRange").getBoundingClientRect().width; var logoImg=!!document.querySelector(".about-logo img"); return JSON.stringify({drag:!!(p1&&p2&&p3), miniCloseSvg:!!mc, volPctLeft:volBtnHasPct, rightbar:histRight, setShown:setShown, closeColor:cc, miniCloseColor:mc2, volWidth:Math.round(vr), logoImg:logoImg}); }catch(e){ return "ERR:"+e.message; } })()');
        console.log('SMOKE_DRAG=' + r);
      } catch (e) { console.log('SMOKE_DRAGERR=' + (e && e.message)); }
    }, 8500);
    // 双击视频=最大化：真实 sendInputEvent 双击 video → 检查 isMaximized；再双击还原
    setTimeout(async () => {
      try {
        const v = await win.webContents.executeJavaScript('(function(){ var v=document.getElementById("video"); var r=v.getBoundingClientRect(); return JSON.stringify({x:Math.round(r.x+r.width/2), y:Math.round(r.y+r.height/2)}); })()');
        const c = JSON.parse(v);
        for (let i = 0; i < 2; i++) {
          win.webContents.sendInputEvent({ type: 'mouseDown', x: c.x, y: c.y, button: 'left', clickCount: i + 1 });
          win.webContents.sendInputEvent({ type: 'mouseUp', x: c.x, y: c.y, button: 'left', clickCount: i + 1 });
          await new Promise((r2) => setTimeout(r2, 120));
        }
        await new Promise((r2) => setTimeout(r2, 600));
        const mx = win.isMaximized();
        // 再双击还原
        for (let i = 0; i < 2; i++) {
          win.webContents.sendInputEvent({ type: 'mouseDown', x: c.x, y: c.y, button: 'left', clickCount: i + 1 });
          win.webContents.sendInputEvent({ type: 'mouseUp', x: c.x, y: c.y, button: 'left', clickCount: i + 1 });
          await new Promise((r2) => setTimeout(r2, 120));
        }
        await new Promise((r2) => setTimeout(r2, 600));
        const mx2 = win.isMaximized();
        console.log('SMOKE_MAX=' + JSON.stringify({ afterDbl: mx, afterDbl2: mx2 }));
      } catch (e) { console.log('SMOKE_MAXERR=' + (e && e.message)); }
    }, 9800);
    // 冒烟测试：窗口固定 16:9（调用 video-ratio 后比例不变）
    setTimeout(() => {
      try {
        win.webContents.executeJavaScript('window.audioAPI.videoRatio(4,3)').then(function () {
          const b = win.getBounds();
          console.log('SMOKE_RATIO=' + JSON.stringify({ w: b.width, h: b.height, r: +(b.width / b.height).toFixed(2), expect: +(16 / 9).toFixed(2) }));
        }).catch(function (e) { console.log('SMOKE_RATIOERR=' + (e && e.message)); });
      } catch (e) { console.log('SMOKE_RATIOERR=' + (e && e.message)); }
    }, 12500);
    // 冒烟测试：视频单击=暂停（不得换视频/刷新）
    setTimeout(async () => {
      try {
        const v = await win.webContents.executeJavaScript('(function(){ var v=document.getElementById("video"); var r=v.getBoundingClientRect(); return JSON.stringify({src:(v.src||"").slice(0,40), x:Math.round(r.x+r.width/2), y:Math.round(r.y+r.height/2)}); })()');
        const c = JSON.parse(v);
        win.webContents.sendInputEvent({ type: 'mouseDown', x: c.x, y: c.y, button: 'left', clickCount: 1 });
        win.webContents.sendInputEvent({ type: 'mouseUp', x: c.x, y: c.y, button: 'left', clickCount: 1 });
        await new Promise((r2) => setTimeout(r2, 800));
        const v2 = JSON.parse(await win.webContents.executeJavaScript('(function(){ var v=document.getElementById("video"); return JSON.stringify({src:(v.src||"").slice(0,40), paused:v.paused}); })()'));
        console.log('SMOKE_CLICK=' + JSON.stringify({ before: c.src, after: v2.src, paused: v2.paused, switched: v2.src !== c.src }));
      } catch (e) { console.log('SMOKE_CLICKERR=' + (e && e.message)); }
    }, 13200);
  });
}
