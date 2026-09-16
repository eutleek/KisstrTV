// 语法检查 + 静态断言：确认本轮改动真实落地
const fs = require('fs');
const P = require('path');
const ROOT = 'C:\\Users\\biaoj\\Desktop\\KisstrTV-WV2';
const htmlPath = P.join(ROOT, '02-src', 'index.html');
const html = fs.readFileSync(htmlPath, 'utf8');

let pass = 0, fail = 0;
function ok(c, name) { if (c) { pass++; console.log('  PASS  ' + name); } else { fail++; console.log('  FAIL  ' + name); } }

console.log('=== 1. JS 语法检查（防止黑屏）===');
const re = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;
let m, n = 0, bad = 0;
while ((m = re.exec(html))) {
  n++;
  try { new Function(m[1]); }
  catch (e) { bad++; console.log('  Script #' + n + ' SYNTAX ERROR: ' + e.message); }
}
console.log('  checked ' + n + ' inline script blocks, ' + bad + ' syntax errors');
ok(n > 0 && bad === 0, '所有内联脚本语法正确');

console.log('');
console.log('=== 2. 关闭 / 最小化行为 ===');
ok(/addEventListener\(\s*'kisstr-pause-media'\s*,\s*pauseMedia\s*\)/.test(html), '已监听宿主派发的 kisstr-pause-media（关窗即暂停）');
ok(/function\s+pauseMedia\s*\(\s*\)/.test(html), 'pauseMedia() 已定义');
const btnMin = (html.match(/\$\('btnMin'\)\.onclick\s*=\s*function\s*\(\s*\)\s*\{[\s\S]{0,200}?\}/) || [''])[0];
ok(btnMin.length > 0 && !/pause/.test(btnMin), 'btnMin 不再暂停（最小化继续播放）');
const btnClose = (html.match(/\$\('btnClose'\)\.onclick\s*=\s*function\s*\(\s*\)\s*\{[\s\S]{0,200}?\}/) || [''])[0];
ok(/pauseMedia\(\)/.test(btnClose), 'btnClose 先 pauseMedia 再关闭');

console.log('');
console.log('=== 3. 失效源真删除 ===');
ok(/function\s+purgeItem\s*\(/.test(html), 'purgeItem() 已定义');
ok(/var\s+removedSet\s*=/.test(html), 'removedSet 永久移除表已建立');
ok(/yytv\.removed/.test(html), '移除表持久化到 localStorage');
ok(/function\s+failAndAdvance\s*\(/.test(html), 'failAndAdvance() 统一失败入口已建立');
ok(/function\s+stormHit\s*\(/.test(html), 'stormHit() 熔断已建立（防限流误删）');
ok(!/markDead\(item\);\s*toast\('该片源持续失败，已加入失效黑名单/.test(html), '旧的「拉黑」路径已移除');
ok((html.match(/failAndAdvance\(/g) || []).length >= 8, '失败点已接入新机制（>=8 处）');
ok(/removedSet\[it\.bvid\]/.test(html), 'mergeVault 入库过滤已删除源');
ok(/removedSet\[m\[1\]\]/.test(html), 'mergeBili 入库过滤已删除源');
ok(/removedSet\[String\(it\.id\)\]/.test(html), 'mergeNetease 入库过滤已删除源');

console.log('');
console.log('=== 4. 宿主侧 ===');
const bridge = fs.readFileSync(P.join(ROOT, '03-build', 'core.cs'), 'utf8');
ok(/'vaultPopular','vaultRemove'/.test(bridge), '桥接方法表已注册 vaultRemove');
const main = fs.readFileSync(P.join(ROOT, '03-build', 'main.cs'), 'utf8');
ok(/case\s+"vaultRemove"/.test(main), 'main.cs 已分发 vaultRemove');
const stub = fs.readFileSync(P.join(ROOT, '03-build', 'services.cs'), 'utf8');
ok(/public\s+static\s+int\s+Remove\s*\(\s*List<string>\s+ids\s*\)/.test(stub), 'stub 版 Vault.Remove 已实现');
const impl = fs.readFileSync(P.join(ROOT, '03-build', 'vault.impl.cs'), 'utf8');
ok(/public\s+static\s+int\s+Remove\s*\(\s*List<string>\s+ids\s*\)/.test(impl), '私有版 Vault.Remove 已实现');
ok(/_items\.RemoveAll/.test(impl), '私有版真正从 _items 删除');
ok(/if\s*\(\s*n\s*>\s*0\s*\)\s*Save\(\s*\)/.test(impl), '删除后落盘');

console.log('');
console.log('=== 5. 源码一致性（工程 vs 开源仓库）===');
const crypto = require('crypto');
function sha(p) { return crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex'); }
const pairs = [
  ['02-src/index.html', '05-opensource/src/index.html'],
  ['03-build/main.cs', '05-opensource/build/main.cs'],
  ['03-build/core.cs', '05-opensource/build/core.cs'],
  ['03-build/services.cs', '05-opensource/build/services.cs']
];
pairs.forEach(function (pr) {
  const a = P.join(ROOT, pr[0]), b = P.join(ROOT, pr[1]);
  const same = fs.existsSync(a) && fs.existsSync(b) && sha(a) === sha(b);
  ok(same, pr[0] + ' 与仓库副本一致');
});
ok(!fs.existsSync(P.join(ROOT, '05-opensource', 'build', 'vault.impl.cs')), '仓库内无 vault.impl.cs');

console.log('');
console.log('=== 6. 产物 ===');
[['01-dist/KisstrTV.exe', '完整版'], ['05-opensource/dist/KisstrTV.exe', '精简版']].forEach(function (x) {
  const p = P.join(ROOT, x[0]);
  if (fs.existsSync(p)) { pass++; console.log('  PASS  ' + x[1] + ' 已生成 ' + fs.statSync(p).size + ' bytes'); }
  else { fail++; console.log('  FAIL  ' + x[1] + ' 缺失'); }
});

console.log('');
console.log('RESULT: ' + pass + ' passed, ' + fail + ' failed');
process.exit(fail ? 1 : 0);
