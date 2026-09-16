// KisstrTV 回归验证 v3：语法 + 本轮改动断言（误删防护 / 曲库守卫）
const fs = require('fs');
const vm = require('vm');
const path = require('path');

const ROOT = 'C:\\Users\\biaoj\\Desktop\\KisstrTV-WV2';
const SRC = path.join(ROOT, '02-src', 'index.html');
const BUILD = path.join(ROOT, '03-build');

const html = fs.readFileSync(SRC, 'utf8');
const core = fs.readFileSync(path.join(BUILD, 'core.cs'), 'utf8');
const services = fs.readFileSync(path.join(BUILD, 'services.cs'), 'utf8');
const maincs = fs.readFileSync(path.join(BUILD, 'main.cs'), 'utf8');

let pass = 0, fail = 0;
const bad = [];

function ok(name, cond, extra) {
    if (cond) { pass++; }
    else { fail++; bad.push(name + (extra ? '  ' + extra : '')); }
}

// ---------------- 1) 语法：所有内联 <script> 必须能 parse ----------------
console.log('=== 1) JS syntax ===');
const re = /<script\b[^>]*>([\s\S]*?)<\/script>/gi;
let m, blocks = 0, synErr = 0;
while ((m = re.exec(html)) !== null) {
    const code = m[1];
    if (/^\s*$/.test(code)) continue;
    blocks++;
    try {
        new vm.Script(code, { filename: 'inline#' + blocks });
    } catch (e) {
        synErr++;
        console.log('   [SYNTAX ERROR] block#' + blocks + ' -> ' + e.message);
    }
}
console.log('   inline blocks: ' + blocks + '   syntax errors: ' + synErr);
ok('JS syntax 0 error', synErr === 0);
ok('at least 1 script block', blocks >= 1);

// ---------------- 2) UI：误删防护 ----------------
console.log('');
console.log('=== 2) UI: 限流不得误删曲库 ===');
ok('failAndAdvance 有 keep 参数', /function failAndAdvance\(item,\s*msg,\s*delay,\s*keep\)/.test(html));
ok('keep 优先于 stormHit', /if\(keep \|\| stormHit\(\)\)/.test(html));
ok('只有 keep=false 才 purgeItem', /else purgeItem\(item\);/.test(html));
const netRefs = (html.match(/r\.net|pNet|nnet/g) || []).length;
ok('UI 读取宿主 net 标记 (>=3 处)', netRefs >= 3, 'found ' + netRefs);
ok('B站解析失败分支带 net', /var net = !!\(r && r\.net\)/.test(html));
ok('网易云解析失败分支带 net', /var nnet = !!\(r && r\.net\)/.test(html));
ok('B站 catch 走 keep=true', /B站连接异常，本条保留'?, 600, true\)/.test(html));
ok('网易云 catch 走 keep=true', /网易云连接异常，本条保留'?, 600, true\)/.test(html));
ok('合集分P 失败区分网络原因', /var pNet = !!\(pr && pr\.net\)/.test(html));
ok('直链过期先重解析再判定', /it\._expRetry/.test(html) && /播放链接已过期，正在重新解析/.test(html));
ok('重解析前清 resolveCache', /delete resolveCache\['b:'\+bv0\]/.test(html));

// ---------------- 3) UI：需求一（关闭暂停/最小化续播） ----------------
console.log('');
console.log('=== 3) UI: 关窗暂停 / 最小化继续 ===');
ok('监听 kisstr-pause-media', /addEventListener\('kisstr-pause-media'/.test(html));
ok('btnClose 先暂停再关', /\$\('btnClose'\)\.onclick\s*=\s*function\(\)\{\s*pauseMedia\(\);/.test(html));
ok('btnMin 不再 pause', !/\$\('btnMin'\)\.onclick[\s\S]{0,160}video\.pause\(\)/.test(html));

// ---------------- 4) UI：失效源删除链路 ----------------
console.log('');
console.log('=== 4) UI: 失效源删除 ===');
ok('removedSet 永久表', /localStorage\.setItem\(REMOVED_LS/.test(html));
ok('purgeItem 四路删除', /function purgeItem\(item\)/.test(html) && /vaultRemove/.test(html));
ok('三处入库都过滤 removedSet', (html.match(/removedSet\[/g) || []).length >= 3);
ok('熔断 stormHit 仍在', /function stormHit\(\)/.test(html));

// ---------------- 5) 宿主：曲库守卫 ----------------
console.log('');
console.log('=== 5) host: vault guards ===');
const vault = fs.readFileSync(path.join(BUILD, 'vault.impl.cs'), 'utf8');
ok('MaxJsonLength 解到上限', /MaxJsonLength = int\.MaxValue/.test(vault));
ok('_loadOk 未成功不得写盘', /if \(!_loadOk\)[\s\S]{0,400}return;/.test(vault));
ok('Add 拒绝在未加载时写入', /if \(!_loadOk\) return 0;/.test(vault));
ok('Remove 拒绝在未加载时写入', /if \(!_loadOk\) return 0;/.test(vault));
ok('失败可重试(不提前置 _loaded)', /_failAttempts/.test(vault));
ok('LastError 暴露给 UI', /public static string LastError/.test(vault));
ok('Save 前留快照 vault.prev.json', /vault\.prev\.json/.test(vault));
ok('stub 版也有 LastError', /public static string LastError/.test(services));

// ---------------- 6) 宿主：net 标记 ----------------
console.log('');
console.log('=== 6) host: net 失败标记 ===');
ok('Bili.Fail 有 net 重载', /Fail\(string msg, bool net\)/.test(services));
ok('Bili.Resolve o==null 判 net', /if \(o == null\) return Fail\("网络受限或超时/.test(services));
ok('code!=0 才算真失效', /if \(Json\.Num\(Json\.Get\(o, "code"\)\) != 0\) return Fail\("视频不存在或不可播放"\)/.test(services));
ok('清晰度受限判 net', /该视频清晰度受限", true\)/.test(services));
ok('Netease.Mes 有 net 重载', /Mes\(string m, bool net\)/.test(services));
ok('Netease 未登录判 net', /请先登录网易云音乐", true\)/.test(services));
ok('vaultStats 回传 error', /vs\["error"\] = ve;/.test(maincs));
ok('bridge 注册 vaultRemove', /vaultRemove/.test(core));

console.log('');
console.log('================================');
console.log('  PASS ' + pass + ' / ' + (pass + fail) + '   FAIL ' + fail);
if (fail) {
    console.log('');
    console.log('  failed items:');
    bad.forEach(b => console.log('    x ' + b));
}
console.log('================================');

fs.writeFileSync(path.join(ROOT, '_compare', 'verify3.txt'),
    'PASS ' + pass + ' / ' + (pass + fail) + '  FAIL ' + fail + '\n' + bad.join('\n'));
process.exit(fail ? 1 : 0);
