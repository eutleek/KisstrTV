// KisstrTV · preload
// 通过 contextBridge 向渲染层暴露安全的窗口控制 API
'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('audioAPI', {
  winMin:  () => ipcRenderer.invoke('win-min'),
  winMax:  () => ipcRenderer.invoke('win-max'),
  winClose:() => ipcRenderer.invoke('win-close'),
  winPin:  () => ipcRenderer.invoke('win-pin'),
  setMini: (on) => ipcRenderer.invoke('set-mini', on),
  toggleFullscreen: () => ipcRenderer.invoke('toggle-fullscreen'),
  setFullscreen: (on) => ipcRenderer.invoke('set-fullscreen', on),
  pickVideos: () => ipcRenderer.invoke('pick-videos'),
  ready:   () => ipcRenderer.invoke('app-ready'),
  winDrag: (act, x, y) => ipcRenderer.invoke('win-drag', act, x, y),
  // 无边框窗边缘缩放：热区拖拽（渲染层算好 bounds 交给主进程 setBounds）
  winBounds: () => ipcRenderer.sendSync('win-bounds'),
  winSetBounds: (b) => ipcRenderer.send('win-set-bounds', b),
  videoRatio: (w, h) => ipcRenderer.invoke('video-ratio', w, h),
  // B 站
  biliStatus:  () => ipcRenderer.invoke('bili-status'),
  biliLogin:   () => ipcRenderer.invoke('bili-login'),
  biliLogout:  () => ipcRenderer.invoke('bili-logout'),
  biliResolve: (bvid) => ipcRenderer.invoke('bili-resolve', bvid),
  biliSearch:  (kw, page) => ipcRenderer.invoke('bili-search', kw, page || 1),
  biliPages:   (bvid) => ipcRenderer.invoke('bili-pages', bvid),
  biliView:    (bvid) => ipcRenderer.invoke('bili-view', bvid),
  biliResolveCid: (bvid, cid, part) => ipcRenderer.invoke('bili-resolve-cid', bvid, cid, part),
  setBiliQuality: (qn) => ipcRenderer.invoke('bili-quality', qn),
  biliQualityGet: () => ipcRenderer.invoke('bili-quality-get'),
  // 网易云音乐
  neteaseStatus:  () => ipcRenderer.invoke('netease-status'),
  neteaseLogin:   () => ipcRenderer.invoke('netease-login'),
  neteaseLogout:  () => ipcRenderer.invoke('netease-logout'),
  neteaseSearch:  (kw) => ipcRenderer.invoke('netease-search', kw),
  neteaseResolve: (mid) => ipcRenderer.invoke('netease-resolve', mid),
  // 曲库（本地持久化海量池）
  vaultStats:   () => ipcRenderer.invoke('vault-stats'),
  vaultPull:    (theme, limit) => ipcRenderer.invoke('vault-pull', theme, limit),
  vaultExplore: (theme, seeds, maxNodes) => ipcRenderer.invoke('vault-explore', theme, seeds, maxNodes),
  vaultPopular: () => ipcRenderer.invoke('vault-popular'),
  // 主进程 → 渲染层事件订阅
  // 窗口被关闭(隐藏到托盘)时主进程广播，渲染层据此暂停视频（v28.5）
  onPauseMedia: (cb) => { ipcRenderer.on('win-pause-media', () => { try { cb && cb(); } catch (e) {} }); }
});
