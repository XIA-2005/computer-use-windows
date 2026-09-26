# Computer Use Skill（Windows v6）— 分享包说明

这是一个和具体应用无关的 Windows「电脑操作」原语工具包：截图给多模态模型看、模型报坐标、脚本精确点击/输入；另带一套走 Chrome DevTools Protocol 的浏览器层（不截图、不动鼠标，直接在页面里定位元素、注入可信的鼠标键盘事件）。完整用法见 `SKILL.md`（写给模型/使用者看的手册，命令、参数、错误码、实测数据都在里面）。

## 环境要求
- Windows 10/11 x64，PowerShell 5.1（系统自带），.NET Framework 4.x（系统自带，首次运行用它的 csc 编译 C# 核心，约 3–8 秒）。
- 浏览器层需要本机装有 Microsoft Edge 或 Google Chrome（默认 Edge，`-Browser chrome` 切换）。
- 不需要安装任何东西，不需要管理员权限。

## 安装
1. 把整个 `computer-use` 文件夹解压到任意位置（路径里有空格也可以）。
2. 打开 PowerShell 或 cmd，运行一次：
   ```
   "解压路径\computer-use\win\cu.exe" info
   ```
   第一次会编译并拉起常驻进程（几秒），之后每条命令 ~50ms。看到一行以 `{"ok":true` 开头的 JSON 就好了。
3. 浏览器层试一下：
   ```
   "解压路径\computer-use\win\cu.exe" web open -Url https://www.bing.com
   "解压路径\computer-use\win\cu.exe" web els
   ```
   它会启动一个**专用的**浏览器实例（独立用户目录 `state\web\`，不碰你日常的浏览器窗口和登录态）。

## 目录
- `SKILL.md` — 手册（给模型读的那份也是它）。
- `win\` — 全部代码：`cu.exe`（快速入口）、`cu.ps1`（分发）、`cu.cs`/`uia.cs`/`web.cs`（C# 核心，首次运行自动编译到 `win\bin\`）、`web-lib.js`（浏览器页内助手，改了即时生效）。
- `web\bench\` — 基准与真实站点测试脚本（`bench.ps1`、`sites.ps1`）和 2026-09-26 的测试报告。
- `state\` — 运行时状态（截图、帧文件、浏览器专用用户目录），自动生成；分享包里是空的。

## 安全提示
- 默认后台注入、不动光标、不抢焦点；`-Fg` 前台模式会抢焦点，脚本层没有限制，请在你自己的规则里要求「每次都先征得同意」。
- 浏览器专用实例的登录态保存在 `state\web\` 下，**不要把 `state\` 目录分享出去**。
- 只操作明确指定的窗口；截图会落到 `state\`，注意里面可能有敏感内容。
