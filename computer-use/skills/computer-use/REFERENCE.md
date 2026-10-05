# Computer Use — 参考资料（实测数据 / Electron 案例）

> 本文件是 SKILL.md 的附属参考：web 层实测数据、桌面层回归与实测案例（Electron/QQ NT）。日常操作看 SKILL.md 即可；本文件按需阅读，不影响任何命令行为。完整版本历史见插件根的 `CHANGELOG.md`（技能目录上两级）。

## 桌面层回归（2026-10-05，v6.1）

`web\bench\desktop.ps1`（自建 WinForms 测试窗口，本机 3120×1984@200%，含 cu.exe 管道开销）：

| 检查 | v6.1 实测 | 说明 |
|---|---|---|
| `snap` DPI-unaware 窗口 | 155–230ms | PrintWindow 或屏幕抓取（见下"抓取路径"） |
| `snap` 纯色（无边框全白）窗口 | 78–140ms（screen 路径，无 `ERR_BLANK`、无重试） | v6 会重试 4×250ms 并把真实窗口误报为空白 |
| `snap -Marks`（7 个控件） | 210–350ms | |
| `find` 冷（小窗口全屏 OCR） | 260–420ms | 大窗口（6MP）约 1.5s，`-Region` 显著更快 |
| `find` 同画面重复 | 97–120ms，`cached:true` | 双槽缓存，静态画面零 OCR |
| `find` 全角/误读字符 | `match:"norm"` / `"fuzzy"` 命中 | v6 只有精确子串，这类查询会 `ERR_TEXT_NOT_FOUND` |
| `click -Id`（UIA 直调） | 340–430ms，`via:"invoke"`，`changed=0.5–3%` | 无坐标点击 |
| `click -Id -Method coord` | 440ms，`changed≈0.01%` | 坐标回退仍可用，但信号弱（UIA 直调更可判定） |
| 无变化点击 | `settle_ms=252–290`，`no_change:true` | v6 默认等满 400ms |
| `click -Id -Method uia`（无可触发模式的元素） | 98–175ms，`ERR_UIA_ACT_FAILED` | 强制直调失败如实报错，不回退坐标；`auto` 才会回退 |
| `click -Id`（禁用按钮） | ~100ms，`ERR_DISABLED` | 缓存与实时判定都会拒绝，不盲点 |
| `type -Verify`（后台/剪贴板/emoji） | 265–545ms，`verify.contains:true`，`source:"edit"` | 标准编辑框用 WM_GETTEXT 精确读回；emoji 代理对：后台逐 `WM_CHAR`、前台 `SendInput` 按"两按下两抬起同批"成对发送，均已验证 |
| `-Fg` 前台输入 | 405–415ms，`verify.contains:true` | 光标用后恢复 |

**抓取路径与 OCR 质量（v6.1 实测结论）**：对 **DPI 不感知**的窗口，屏幕抓取拿到的是 Windows 放大后的**模糊**位图，而 `PrintWindow` 渲染的是原生清晰画面 —— 同一个窗口，屏幕路径下 OCR 把 `STATIC-42` 读成乱码、PrintWindow 路径下能认出。所以：老程序（不声明 DPI 感知）文字 OCR 不准时，加 `-Method print`；现代 Per-Monitor 感知的程序两条路径都清晰。桌面回归脚本因此固定用 `-Method print` 保证可重复。

web 层同机复测（`web\bench\bench.ps1 -N 5`）与 v6 基线逐项一致（差异均在噪声内）：`frame` 50ms、`web info` 52ms、`web text` 62ms、`open` 106ms、`click -Text` 76ms、`type -Verify` 64ms、`wait -Sel` 371ms、`shot` 视口 177ms / 整页 133ms、`els` 57ms、`hover` 62ms、`shot -Marks` 152ms。`web click` 新增事后校验（`verified`）约 +5–10ms。

## web 层实测（2026-09-26）

真实站点（Edge 与 Chrome 专用实例各跑一遍 `web\bench\sites.ps1`）：

- B 站首页 `els` 76 个元素 11ms、点「热门」自动跟到新标签并 `wait -Url` 31ms。
- 网易云音乐主内容在同源 iframe 里，`els`/`find`/`click` 直接穿透，点「排行榜」后 `wait -Url toplist` 3ms。
- GitHub 仓库页 `click Issues`（真实鼠标）+ `wait` 0.9s、`hover` 真实悬停、`/` 快捷键打开搜索。
- 必应 `type -Enter` → 结果页 0.8–1.4s；豆瓣搜索全流程 <1.5s。

本地基准（本机 3120×1984@200%，含 cu.exe 管道开销，`web\bench\bench.ps1 -N 5` 中位数，括号内为 v5）：

| 操作 | v6 | v5 |
|---|---|---|
| `web text` | 49ms | 64ms |
| `click -Text`（带 pointerdown/mousedown） | 69–83ms | 59–91ms（只有 click 事件，mousedown 菜单/aria-label 图标/悬停都做不到） |
| `type -Verify` | 59ms | 66ms（无 InputEvent/keyup） |
| contenteditable | 56ms | 245ms |
| `wait -Sel`（元素 350ms 后出现） | 364ms | 964ms |
| `shot` 视口 / 整页 | 162ms / 147ms | 480ms / 744ms |
| `web open` 本地页 | 111ms | 191ms |
| `els` | 49ms | — |
| `hover` | 77ms | — |
| 批量 `do` 5 步 web 动作 | 284ms | — |

## 已知边界（v6.1）

- 最小化窗口没有帧；`-Text` 不跨跨域 iframe；启发式排序在同名元素多时有歧义（看 `count/alts`）。
- 模糊匹配（`match:"fuzzy"`）是为了容忍 OCR 误读（实测 `STATIC-42` 被读成 `STATlC-42`），可能命中形近文本；对结果有疑问时用 `-Strict` 收紧，或核对 `hit_box` 与 `alts`。查询短于 4 字符不做模糊。
- `-Find "a|b"` 用 `|` 分隔候选；查询里真要包含 `|` 时用 `-FindB64` 并另想办法（会按候选拆分）。
- UIA 直调对 Win32/WinForms/WPF 点按类控件最有效；纯文本/画布控件会回退坐标点击（`-Method uia` 则直接报 `ERR_UIA_ACT_FAILED`）。直调 800ms 未确认时返回 `method:"uia-pending"` + `warn`，不补坐标点击（防双触发），用 after 帧判断是否已生效。UIA 连续超时的进程会被记忆 10 分钟（`-UiaForce` 重试）。
- `CU_FG_AUTO=1` / `CU_SLOW=1` / `CU_SETTLE_QUIET` 在常驻进程启动时读取，改完要 `cu.exe --stop`。
- `-Method clip` 走粘贴消息：常驻进程会先等焦点真正落到目标控件（`WaitFocus`），粘贴后用 `WM_GETTEXT` 读回，必要时最多重发 2 次粘贴，然后才恢复用户剪贴板。工具包/输入法占用剪贴板时 `ClipSet` 会重试 5 次，失败报 `ERR_CLIPBOARD`。
- `-Verify` 的 `source` 字段：`edit` = 标准编辑框，用 `WM_GETTEXT` 同步精确读取（不受 UIA 值滞后影响）；其它值来自 UIA（`value`/`text`/`no-pattern`/`other-process`）。
- `web text` 默认截断 2 万字符（`-Max` 可调）；`ocr` 默认 300 行（`-Max`）。

## 实测：QQ NT（Electron，2026-09-23）

| 操作 | 后台 | 前台 `-Fg` |
|---|---|---|
| 截图（窗口最小化 / 被遮挡） | ✓ `snap -Restore`，PrintWindow | — |
| 点击 / 滚动 | ✗ `changed=0`（Chromium 忽略 PostMessage） | ✓ |
| 中文 + emoji 输入 | — | ✓ Unicode SendInput |
| `click -Find "我的手机"` | — | ✓ OCR 定位并点中 |
| 发消息（给"我的手机"） | — | ✓ 输入 → 点击发送 → 截图确认 |

结论：QQ、微信（新版）、Chrome、VS Code、ShunCode 这类 `Chrome_WidgetWin_1` 窗口，**截图用后台，操作直接用 `-Fg`**（需用户许可），不要先试后台浪费一轮；v6.1 起后台没生效时返回里会直接给 `hint`，也可以设 `CU_FG_AUTO=1` 让这类窗口自动走前台（默认关）。

- OCR：v4 的第二遍用局部对比度预处理，蓝底白字"发送"已能识别（实测 `find -Find 发送` → (1269,828)，与目测一致）；加 `-Region` 限定在底部区域约 0.5 秒。
- UIA：QQ 不暴露控件（`els` 超时 0 个），不要用 `-Marks/-Name`，直接视觉坐标 / `-Find`；v6.1 起会把该进程记 10 分钟，后续 `-Name` 直接转 OCR 不再每次等 1.5s。
- 截图：QQ 最小化或被遮挡时可能画出纯灰空白图（PrintWindow 路径），工具会重试 3×150ms 并给 `warn: ERR_BLANK`，这时需要（经许可）`activate` 后再截；也正因如此，这个 `warn` 只对 PrintWindow 给出——画面本来就是纯色的真实窗口（全白/全黑）不会再被误报，也不再多等 1 秒。
