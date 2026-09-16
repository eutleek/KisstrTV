/* 行为验证：在真实浏览器里跑 index.html，mock 掉宿主 audioAPI。
   三件事必须成立：
     A. 宿主报「限流」(net:true) 时，UI 绝不能删曲库
     B. 宿主报「源真失效」(无 net) 时，UI 必须删
     C. 关闭按钮先暂停再关；最小化不暂停
*/
const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright-core');

const ROOT = 'C:\\Users\\biaoj\\Desktop\\KisstrTV-WV2';
const PAGE = 'file:///' + path.join(ROOT, '02-src', 'index.html').replace(/\\/g, '/');

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
].find(p => fs.existsSync(p));

const sleep = ms => new Promise(r => setTimeout(r, ms));
let pass = 0, fail = 0;
const bad = [];
function ok(name, cond, extra) {
    if (cond) { pass++; console.log('   ok   ' + name); }
    else { fail++; bad.push(name + (extra ? ' | ' + extra : '')); console.log('   FAIL ' + name + (extra ? '  ' + extra : '')); }
}

/* 注入到页面的 mock：mode 决定 biliResolve 怎么回答 */
function mockInit(mode) {
    window.__calls = { vaultRemove: 0, winClose: 0, winMin: 0, pauses: 0 };
    const P = v => Promise.resolve(v);
    const noop = () => P({});

    window.audioAPI = {
        // 窗口
        winDrag: noop, winMin: () => { window.__calls.winMin++; return P({}); },
        winMax: noop, winClose: () => { window.__calls.winClose++; return P({}); },
        winBounds: () => P({ x: 0, y: 0, w: 1280, h: 720 }),
        winSetBounds: noop, setMini: noop, setFullscreen: noop,
        videoRatio: noop, ready: noop, onPauseMedia: noop, pickVideos: () => P([]),
        setBiliQuality: noop,
        // 登录
        biliStatus: () => P({ loggedIn: false }),
        biliLogin: noop, biliLogout: noop,
        neteaseStatus: () => P({ loggedIn: false }),
        neteaseLogin: noop, neteaseLogout: noop,
        // B站
        biliView: () => P({ ok: false, net: true, msg: 'mock' }),
        biliPages: () => P({ ok: false, net: true, msg: 'mock 限流' }),
        biliResolve: () => P(mode === 'net'
            ? { ok: false, net: true, msg: '网络受限或超时，本条保留待重试' }
            : { ok: false, msg: '视频不存在或不可播放' }),
        biliResolveCid: () => P(mode === 'net'
            ? { ok: false, net: true, msg: '网络受限或超时，本条保留待重试' }
            : { ok: false, msg: '视频不存在或不可播放' }),
        biliSearch: () => P({ ok: true, list: [] }),
        // 网易云
        neteaseResolve: () => P({ ok: false, net: true, msg: 'mock' }),
        neteaseSearch: () => P({ ok: true, list: [] }),
        // 曲库
        vaultStats: () => P({ total: 200 }),
        vaultPull: () => P(Array.from({ length: 40 }, (_, i) => ({
            bvid: 'BV' + String(i).padStart(10, '1'), title: '测试曲目' + i, author: '测试', dur: 200,
        }))),
        vaultExplore: () => P({ busy: false, added: 0, total: 200 }),
        vaultPopular: () => P({ added: 0, total: 200 }),
        vaultRemove: ids => { window.__calls.vaultRemove++; window.__calls.lastIds = ids; return P({ ok: true, removed: (ids || []).length }); },
    };
    // 记录 pause 调用
    const origPause = HTMLMediaElement.prototype.pause;
    HTMLMediaElement.prototype.pause = function () { window.__calls.pauses++; return origPause.apply(this, arguments); };
}

(async () => {
    if (!EDGE) { console.log('NO EDGE FOUND'); process.exit(1); }
    const browser = await chromium.launch({ executablePath: EDGE, headless: true });

    async function scenario(mode) {
        const ctx = await browser.newContext();
        await ctx.addInitScript(mockInit, mode);
        const page = await ctx.newPage();
        const errs = [];
        page.on('pageerror', e => errs.push(String(e.message).slice(0, 160)));
        await page.goto(PAGE, { waitUntil: 'load' });
        await sleep(1500);
        // 点「开始收听」→ feedRandomChannel()，让页面真正进入连播流程
        await page.evaluate(() => { const b = document.getElementById('welcomeGo'); if (b) b.click(); });
        await sleep(14000);   // 等自动连播 + 多次失败

        const st = await page.evaluate(() => {
            let removed = {};
            try { removed = JSON.parse(localStorage.getItem('yytv.removed') || '{}'); } catch (e) { }
            return {
                removedKeys: Object.keys(removed).length,
                calls: JSON.parse(JSON.stringify(window.__calls)),
                queue: (typeof state !== 'undefined' && state.queue) ? state.queue.length : -1,
            };
        });
        await ctx.close();
        return { st, errs };
    }

    // ---------- A: 限流 ----------
    console.log('');
    console.log('=== A) host says 限流 (net:true) -> must NOT delete ===');
    let A = await scenario('net');
    console.log('   pageErrors:', A.errs.length ? A.errs : 'none');
    console.log('   state:', JSON.stringify(A.st));
    ok('A: no JS error', A.errs.length === 0, A.errs.join(' ; '));
    ok('A: removedSet 为空（没删曲库）', A.st.removedKeys === 0, 'removed=' + A.st.removedKeys);
    ok('A: 未调用 vaultRemove', A.st.calls.vaultRemove === 0, 'calls=' + A.st.calls.vaultRemove);

    // ---------- B: 真失效 ----------
    console.log('');
    console.log('=== B) host says 源失效 (no net) -> must delete ===');
    let B = await scenario('dead');
    console.log('   pageErrors:', B.errs.length ? B.errs : 'none');
    console.log('   state:', JSON.stringify(B.st));
    ok('B: no JS error', B.errs.length === 0, B.errs.join(' ; '));
    ok('B: 已从曲库移除', B.st.removedKeys > 0, 'removed=' + B.st.removedKeys);
    ok('B: 调用了 vaultRemove', B.st.calls.vaultRemove > 0, 'calls=' + B.st.calls.vaultRemove);

    // ---------- C: 关闭/最小化 ----------
    console.log('');
    console.log('=== C) 关闭先暂停 / 最小化不暂停 ===');
    const ctx = await browser.newContext();
    await ctx.addInitScript(mockInit, 'net');
    const page = await ctx.newPage();
    await page.goto(PAGE, { waitUntil: 'load' });
    await sleep(1500);
    await page.evaluate(() => { const b = document.getElementById('welcomeGo'); if (b) b.click(); });
    await sleep(7000);

    // 让 video 处于"正在播放"的假象：pauseMedia 里有 if(!video.paused) 守卫
    await page.evaluate(() => {
        const v = document.querySelector('video');
        try { Object.defineProperty(v, 'paused', { configurable: true, get: () => false }); } catch (e) { }
        window.__calls.pauses = 0; window.__calls.winClose = 0; window.__calls.winMin = 0;
    });

    await page.click('#btnMin');
    await sleep(600);
    const afterMin = await page.evaluate(() => ({ ...window.__calls }));

    await page.evaluate(() => {
        const v = document.querySelector('video');
        try { Object.defineProperty(v, 'paused', { configurable: true, get: () => false }); } catch (e) { }
    });
    await page.click('#btnClose');
    await sleep(600);
    const afterClose = await page.evaluate(() => ({ ...window.__calls }));

    console.log('   after btnMin  :', JSON.stringify(afterMin));
    console.log('   after btnClose:', JSON.stringify(afterClose));
    ok('C: btnMin 调用了 winMin', afterMin.winMin > 0);
    ok('C: 最小化不暂停', afterMin.pauses === 0, 'pauses=' + afterMin.pauses);
    ok('C: btnClose 调用了 winClose', afterClose.winClose > 0);
    ok('C: 关闭先暂停', afterClose.pauses > 0, 'pauses=' + afterClose.pauses);

    await ctx.close();
    await browser.close();

    console.log('');
    console.log('================================');
    console.log('  PASS ' + pass + ' / ' + (pass + fail) + '   FAIL ' + fail);
    bad.forEach(b => console.log('    x ' + b));
    console.log('================================');
    fs.writeFileSync(path.join(ROOT, '_compare', 'behavior.txt'),
        'PASS ' + pass + ' / ' + (pass + fail) + '  FAIL ' + fail + '\n' + bad.join('\n'));
    process.exit(fail ? 1 : 0);
})().catch(e => { console.log('FATAL: ' + e.message); process.exit(2); });
