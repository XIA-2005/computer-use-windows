# Computer Use — 参考资料（实测数据 / Electron 案例）

> 本文件是 SKILL.md 的附属参考：web 层实测数据、Electron（QQ NT）实测案例。日常操作看 SKILL.md 即可；本文件按需阅读，不影响任何命令行为。完整版本历史见仓库根 `CHANGELOG.md`（v6 起浏览器层 CDP 重做 + 桌面层提速）。

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

已知边界（`web\bench\results-2026-09-26.md`）：最小化窗口没有帧；`-Text` 不跨跨域 iframe；启发式排序在同名元素多时有歧义（看 `count/alts`）。

## 实测：QQ NT（Electron，2026-09-23）

| 操作 | 后台 | 前台 `-Fg` |
|---|---|---|
| 截图（窗口最小化 / 被遮挡） | ✓ `snap -Restore`，PrintWindow | — |
| 点击 / 滚动 | ✗ `changed=0`（Chromium 忽略 PostMessage） | ✓ |
| 中文 + emoji 输入 | — | ✓ Unicode SendInput |
| `click -Find "我的手机"` | — | ✓ OCR 定位并点中 |
| 发消息（给"我的手机"） | — | ✓ 输入 → 点击发送 → 截图确认 |

结论：QQ、微信（新版）、Chrome、VS Code、ShunCode 这类 `Chrome_WidgetWin_1` 窗口，**截图用后台，操作直接用 `-Fg`**（需用户许可），不要先试后台浪费一轮。

- OCR：v4 的第二遍用局部对比度预处理，蓝底白字"发送"已能识别（实测 `find -Find 发送` → (1269,828)，与目测一致）；加 `-Region` 限定在底部区域约 0.5 秒。
- UIA：QQ 不暴露控件（`els` 超时 0 个），不要用 `-Marks/-Name`，直接视觉坐标 / `-Find`。
- 截图：QQ 最小化或被遮挡时可能画出纯灰空白图，v4 会自动重试并在返回里给 `warn: ERR_BLANK`，这时需要（经许可）`activate` 后再截。
