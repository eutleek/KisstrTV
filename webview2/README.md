# KisstrTV · WebView2 版

KisstrTV 的 WebView2 换壳版。原本是 Electron 应用（安装包 76 MB），现在是一个
约 3 MB 的单文件 Windows 程序，界面 `index.html` 与主仓库逐字节相同。

## 结构

```
src/       index.html + assets   唯一真源，改这里
build/     编译环境              *.cs + build.bat（三个 WebView2 DLL 自行准备）
compare/   行为比对与验证脚本
```

## 构建

1. 从 NuGet 包 `Microsoft.Web.WebView2` 的 `build\native\x64\` 取出三个 DLL，
   放进 `build\`（`Microsoft.Web.WebView2.Core.dll` / `....WinForms.dll` / `WebView2Loader.dll`）
2. 双击 `build\build.bat`
3. 产物 `dist\KisstrTV.exe`

只需要 .NET Framework 4.x 自带的 `csc.exe` —— 没有 Visual Studio、没有 MSBuild、没有 SDK。
HTML、图标与三个 DLL 全部内嵌为资源，所以输出是单个约 3 MB 的 exe。

## 注意

- `.bat` 只能写 ASCII。cmd 按本地 ANSI 代码页解析，中文注释会被误读成命令
  （实测报过 `'lt' 不是内部或外部命令`）。
- 目标机需要 WebView2 Runtime（Win11 与多数 Win10 自带）。
- 宿主内置请求合并、节流与 412 断路器，**不要为了代码干净删掉**。
- 曲库数据在 `%APPDATA%\KisstrTV\vault.json`，不在工程目录内。
- **`vault.json` 绝不能带 UTF-8 BOM**：两个宿主共用同一份曲库文件，而 Electron 侧的
  `JSON.parse` 不认 BOM，会直接把曲库读成 0 条。C# 侧落盘须用 `new UTF8Encoding(false)`，
  JS 侧读取前剥 `\uFEFF`。

## 关于完整版

上游工程里还有一个 `vault.impl.cs`（曲库挖掘与筛选策略），属于私有实现，不在本仓库。
缺少它时 `services.cs` 的占位实现生效：程序照常编译运行，只是曲库为空。
