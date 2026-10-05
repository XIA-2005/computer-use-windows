# Computer Use Skill（Windows v6）— 分享包说明

这是一个和具体应用无关的 Windows「电脑操作」原语工具包：截图给多模态模型看、模型报坐标、脚本精确点击/输入；另带一套走 Chrome DevTools Protocol 的浏览器层（不截图、不动鼠标，直接在页面里定位元素、注入可信的鼠标键盘事件）。完整用法见 `computer-use/skills/computer-use/SKILL.md`（写给模型/使用者看的手册，命令、参数、错误码、实测数据都在里面）。

## 环境要求
- Windows 10/11 x64，PowerShell 5.1（系统自带），.NET Framework 4.x（系统自带，首次运行用它的 csc 编译 C# 核心，约 3–8 秒）。
- 浏览器层需要本机装有 Microsoft Edge 或 Google Chrome（默认 Edge，`-Browser chrome` 切换）。
- OCR（`find` 找字）依赖 Windows 语言包：「设置 → 时间和语言 → 语言」里需包含要识别的语言，否则报 `ERR_NO_OCR`。
- 不需要安装任何东西，不需要管理员权限。

## 安装

**方式一：ZCode 插件市场（推荐）**——本仓库同时是一个 ZCode 插件市场。在 ZCode 里打开「插件市场 → 添加 → 添加插件市场」，粘贴本仓库地址（GitHub：`https://github.com/XIA-2005/computer-use-windows`；本地测试可直接粘贴仓库目录），添加后在「个人」里安装 **Computer Use (Windows)**，技能由 ZCode 统一加载与更新，无需手动拷贝文件。

**方式二：手动解压**
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

## 目录（以 `computer-use/` 插件文件夹为根）
- `skills\computer-use\SKILL.md` — 手册（给模型读的那份也是它；顶部含 ZCode 技能 frontmatter）。
- `skills\computer-use\REFERENCE.md` — 附属参考：web 层实测数据、Electron（QQ NT）实测案例，按需阅读。
- `.zcode-plugin\plugin.json` — ZCode 插件清单（技能入口指向 `skills\`）。
- `win\` — 全部代码：`cu.exe`（快速入口，由同目录 `client.cs` 编译而来，被杀软误报时可对照源码核对）、`cu.ps1`（分发）、`cu.cs`/`uia.cs`/`web.cs`（C# 核心，首次运行自动编译到 `win\bin\`）、`web-lib.js`（浏览器页内助手，改了即时生效）。
- `web\bench\` — 基准与真实站点测试脚本（`bench.ps1`、`sites.ps1`）和 2026-09-26 的测试报告。
- `state\` — 运行时状态（截图、帧文件、浏览器专用用户目录），自动生成；分享包里是空的。
- 仓库根还有 `CHANGELOG.md`（版本历史）。

> **插件方式安装的注意**：此时 `state\` 会落在 ZCode 的插件缓存目录里，插件更新或重装时可能被清掉（浏览器专用实例的登录态会丢）。建议设置环境变量 `CU_STATE` 把状态目录指到缓存之外的位置，例如 `%LOCALAPPDATA%\computer-use\state`。

## 安全提示
- 默认后台注入、不动光标、不抢焦点；`-Fg` 前台模式会抢焦点，脚本层没有限制，请在你自己的规则里要求「每次都先征得同意」。
- 浏览器专用实例的登录态保存在 `state\web\` 下，**不要把 `state\` 目录分享出去**。
- 只操作明确指定的窗口；截图会落到 `state\`，注意里面可能有敏感内容。
