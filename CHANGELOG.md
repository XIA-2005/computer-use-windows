# Changelog

## 6.0.1（2026-10-05）— 文档与打包修订

- SKILL.md 瘦身：版本历史、bench 实测数据、QQ NT 案例移到 `skills/computer-use/REFERENCE.md`（按需阅读，不再随技能全文加载）。
- SKILL.md 错误码表补全：按桌面层 / web 层分组，覆盖代码中全部错误码（新增 `ERR_NO_OCR`、`ERR_ARGS`、`ERR_NO_FRAME`、`ERR_TIMEOUT`、web 层 `ERR_BROWSER/ERR_PROFILE/ERR_START/ERR_CDP/ERR_WS` 等），并写明 OCR 语言包前提。
- 修正 SKILL.md 中指向 `win/_v2_backup/`、`win/_v3_backup/` 的失效引用。
- 命令速查表补 `ocr` 参数与常用别名（`dbl`、`go/nav`、`locate`、`txt`、`value`、`paste` 等）。
- README 补充：OCR 语言包要求、插件安装时用 `CU_STATE` 固定状态目录的建议、`cu.exe` 与 `client.cs` 的对应关系。
- 新增本文件；提交 `.zcodeignore`。

## 6.0.0（2026-09-26）— v6：浏览器层重做 + 桌面层提速

浏览器层：

- 文字定位引擎重做（剪枝 DFS）：嵌套按钮、aria-label、title、placeholder、label、alt 都能按字找，穿透 open shadow DOM 与同源 iframe，隐藏元素靠后，返回 `count/alts` 提示歧义。
- 默认**真实鼠标点击**（move→press→release，先做命中测试，被遮挡自动退回 DOM click 并 `warn`）。
- 新增 `web els`（编号元素 + `-Id`）、`web shot -Marks`（编号截图）、`web find`、`web hover`、`web scroll`。
- `type` 发标准 InputEvent + keyup，contenteditable 走可信 `Input.insertText`（富文本编辑器可用），新增 `-Method keys`。
- `wait` 改为页内 MutationObserver（元素一出现立刻返回）+ `-Stable`。
- `shot` 由浏览器按比例直接输出（不再解码重编码，快 2–3 倍）。
- 同 URL 重开也能正确等待，`open` 返回 `title`/`ready`（load 事件被慢资源拖住时按 `interactive` 返回可用页面而不是超时）。
- `type` 默认走**可信输入**（页面看到的和真人打字一样，Enter 能提交必应这类监听状态的搜索框）。
- 点开 `target=_blank` 链接会**自动跟到新标签**；会话标签跑到后台时自动激活。
- 每条命令不再做 HTTP 探活、常驻进程不再每次 Full GC。
- 专用实例带 `--disable-backgrounding-occluded-windows` 等参数（**旧实例需 `web stop` 一次**）。

桌面层同轮优化：

- OCR 改为内存直通（`find` 642→~450ms，带 `-Region` ~150ms）；画面没变时复用上次 OCR 结果（返回 `cached:true`）。
- `-Title/-Proc` 3 秒内复用已解析的窗口、进程名缓存 1 分钟（`snap` 275→150ms，`info` 113→48ms）。
- 缩图改 HighQualityBilinear。

基准：`computer-use/web/bench/bench.ps1`（本地页）+ `sites.ps1`（真实站点，Edge/Chrome 双实例），报告见同目录 `results-2026-09-26.md`。

## 5.0.0（2026-09）— v5：浏览器层 `web`（Chrome DevTools Protocol）

专用独立浏览器实例（不碰日常窗口），DOM 级点击/填表/读字/等待，单步内部耗时 3–80ms（截图路径的 1/3–1/5），支持 `-Sel` CSS 选择器、`-Text` 按文字点、`web shot` 后 `-X -Y` 图坐标点击、`-Verify` 读回核对、标签页管理。

## 4.0.0（2026-09）— v4：cu.exe 常驻进程 + UIA 控件层

`cu.exe` 常驻进程（单次调用 ~50–250ms，原来 ~600ms）；UI Automation 控件层（`snap -Marks` 编号框、`click -Id`、`click -Name`、坐标自动吸附控件、`type -Verify` 读回校验）；动作后局部稳定检测；OCR 局部对比度预处理 + `-Region`；Chromium 空白帧检测。

## 3.0.0（2026-09）— v3：统一入口 + DPI 精确映射

统一入口 `win/cu.ps1`，C# 核心预编译缓存，DPI 精确映射，帧文件坐标协议，动作后自动等待画面稳定 + 可选同步回截图，OCR 找字点击，批量动作。旧脚本名保留为兼容包装。
