# Changelog

## 6.1.0 真实站点复核修正（2026-10-05，专用实例上实测后追加）

- **`type -Enter` 回车兜底**：实测必应首页刚 `open` 时其提交脚本尚未就绪，回车的 `\r` 落进输入框（textarea 型搜索框）而没有提交，查询串还混进 `%0D%0A`。现在检测"值尾部有换行"→ 清掉换行 → 用真实鼠标点表单自己的提交按钮（返回 `enterVia:"submit-click"`，实测 114ms）；表单没有提交控件时只回 `enterNote`、不改动值。本地回归：`page.html` 的 `#lateform`；必应 3/3 复测通过。
- **拦截页识别（`wall` + `hint`）**：`open`/`info`/`els`/`text` 的成功回复与 `find`/`click`/`wait` 的失败回复现在会标明落在什么页面上——`cloudflare`（人机验证）/`risk`（站点风控）/`login`（登录墙）。实测来源：v2ex（CF 12s+reload+12s 仍未过）、京东（`risk_handler`→`passport...login.aspx`）、知乎（`/signin`，无 password 输入框，靠文案识别）。**只识别上报，不做任何绕过**；`hint` 明确要求停下并请用户在专用窗口手动过一次（凭证留在 profile）。
- **`info` 新文档回归修复**：站点自己跳转产生的新文档里，`info` 作为首个命令曾报 `ReferenceError: __cu is not defined`（本轮引入的回归，JD 跳登录页时复现）；`info` 改用 `LibEval`（自动注入页内助手）。本地回归：跨文档点击后立刻 `info`。
- **点击跳转不再误报 warn**：`web click` 命中会跳转的链接后元素消失，旧实现回"请核对结果"警告；现在返回 `gone:true`、不告警（JD/维基/GitHub 实测），与文档"元素消失多半是跳转"一致。
- **回归套件**：`bench.ps1` 新增 7 项本地场景（回车兜底、无 CRLF 校验、挑战页/登录页的 `wall` 断言、跨文档 `info`）；新增 `challenge.html`、`login.html`；`sites.ps1` 修 JD 搜索框选择器（`#key` 已消失，改 `input[type=text]`）、zhihu/v2ex 按 `wall` 判定预期结果（新增 `-WantErr`）、新增 `wiki`/`mdn` 两个读文档场景。
- `web-lib.js` VERSION 20→21：页内助手是"每文档安装一次 + 版本号守卫"，**改内容必须升版本**，否则已打开的标签仍跑旧助手。

## 6.1.0 实现复核修正（2026-10-05，独立审计后追加）

- **UIA 直调的线程治理与结果三态**：`Act` 与 `Collect` 共用僵尸判龄/线程计数（此前每次超时的直调会静默泄漏一个 UIA 线程）；直调结果不确定（超时/占用）时返回 `method:"uia-pending"` + `warn`，**不再**补发坐标点击（此前可能在 UIA 稍后生效后造成双触发）；实时判定控件已禁用同样报 `ERR_DISABLED`（此前缓存显示"启用"而实际禁用时会回退坐标盲点）；`-Method uia` 为强制语义（失败报 `ERR_UIA_ACT_FAILED`，不静默回退），`auto`/`coord` 行为不变。
- **emoji 代理对成对发送**：前台 `SendInput` 输入此前按码元逐个"按下+抬起"，现改为同一批内两按下、两抬起（严格控件拆半丢字的场景）；`desktop.ps1` 的 `-Fg` 读回断言覆盖。
- **`Input.insertText` 超时不重发**：`web keys`/`OneKey` 两处中文与未知 token 的插入与此前 `Runtime.evaluate` 同策略修复（重发=文本插两遍）。
- 剪贴板读回确认单轮预算 220→150ms（最坏 ≈600ms，确认机制保留）。
- `web click` 校验失败路径如实返回 `warn`（补点后仍未命中 / 遮挡不可补点两种情形都明说，此前只更新 `verified` 不给提示）；`click -Name` 的 OCR 兜底同样返回 `hit_box`/`alts`（此前只有 `-Find` 有）。
- `testwin.ps1` 增加禁用按钮；`desktop.ps1` 增加两条拒绝路径断言（ERR_DISABLED、ERR_UIA_ACT_FAILED），全链路 21 项。

## 6.1.0（2026-10-05）— 更快更准：正确性修复 + 精度增强

正确性（P0）：

- **OCR 结果缓存指纹修复**：原实现按固定步长跨字节抽样（4K 窗口每 ~123 字节才采 1 个），画面局部小改会被漏掉 → `find` 复用过期 OCR 结果、返回旧坐标，`wait -Find` 误判"没出现"。改为分块覆盖采样（4096 块，块内等距采样并把块号混入哈希），任意局部改动必被覆盖，开销不变。
- **`web eval` 超时不再重发**：`Runtime.evaluate` 超时后原会重试一次，有副作用的 JS（点击/提交）可能执行两遍；现在与 `Input.*` 一致只发一次（会话/连接类错误仍会重连重试）。
- **UIA `busy` 不再永久锁死**：旧实现里一次挂死的 UIA walk 会让之后所有 UIA 调用直接返回空列表；现在超过 30s 视为僵尸并放行新 walk（并发上限 3），状态区分 `busy` / `busy-zombie`。
- **UIA 不可用记忆**：UIA 每次都超时的进程（QQ NT 等实测）记住 10 分钟，`els` / `-Name` / `snap -Marks` 直接跳过 UIA 走 OCR（省掉每次 1.5s 空等）；`-UiaForce` 可强制重试。
- **`snap.ps1`（v2 兼容包装）不再绕过尺寸上限**：原来传 `MaxSide=0; MaxPixels=0`，两个上限全绕过 → 4K 窗口输出全分辨率大图；改为跟随 cu.ps1 默认上限（判断改 `-le 0` 兜底）。
- **blank 重试只对 PrintWindow 生效**：原来屏幕抓取也参与"空白图"重试——大面积纯色窗口每次截图白等最多 1s 并误报 `ERR_BLANK`；现在 screen 路径不重试、不报空白，print 路径重试 3×150ms。
- **OCR 结果缓存改双槽（plain/prep）**：两遍 OCR 不再互相顶掉缓存，静态画面下重复 `find` / `wait -Find` 轮询零 OCR。
- **WinRT 调用 15s 超时**：OCR 引擎卡死不再挂起整个常驻进程（报 `ERR_OCR_TIMEOUT`）。
- **新增 `-Lang <tag>`**：指定 OCR 语言（如 `-Lang zh-Hans-CN`）；语言包已装但不在用户配置列表时也能用，tag 不对会得到明确报错。

更快（P1）：

- 动作后"没变化就提前返回"窗口 400→250ms（`-Quiet` / `CU_SETTLE_QUIET` 可调），首查 25→15ms、轮询 30→20ms；返回新增 `no_change` 字段。
- 注入时序默认减半（后台 mousemove→down 8→3ms、down→up 20→10ms，双击 20→12ms，前台 40→20ms，拖拽/滚动/WaitStable/聚焦等待同步缩短），`CU_SLOW=1` 一键恢复旧时序。
- 剪贴板改原生 API（`Get-/Set-Clipboard` cmdlet 每次 30–60ms → 1–2ms），`-Verify` 时读回确认后再恢复用户剪贴板（原来固定等 350ms）。
- web 层轮询压缩：`start` 150→60ms（前 2s）、`open`/ready 30→15ms、`wait` 切片 50→35ms、`target=_blank` 空轮询 1500→700ms。
- 输出体积：`web text` 默认 20000 字符（`-Max` 可调）、`ocr` 默认 300 行（`-Max`，超出带 `truncated`）、桌面 `els` 支持 `-Size` 上限。

更准（P2）：

- **OCR 三级匹配**：精确子串 → 归一化（全角→半角、去标点/符号）→ 模糊（易混字符 0/O、1/l/I、5/S… 折叠 + 编辑距离 ≤1，查询 ≥4 字符）。命中带 `match:"exact|norm|fuzzy"`；`-Strict` 只走精确级（跳过归一化与模糊两级）。
- **多候选一次识别**：`-Find "保存|确定|Save"`（`click -Find` / `find` / `wait -Find` 均支持），一次 OCR 服务多个候选并给出命中情况。
- **点击返回带命中信息**：`click -Find` 返回 `hit_box`（命中框）、`match`、`alts`（其它候选文本 + 中心点），换目标不用再跑一次 OCR。
- **UIA 控件直调点击**：`click -Id` / `-Name` 默认按控件语义触发（`Invoke/Toggle/SelectionItem/ExpandCollapse`，返回 `method:"uia"`），不受遮挡与坐标误差影响；控件禁用直接报 `ERR_DISABLED`；不支持时自动回退坐标点击（`method:"coord"`），`-Method coord` 可强制。
- **`type -Id/-Name` 现在真的会先点击目标控件**（文档一直这么写，代码此前忽略了这两个参数）。
- **Chromium/Electron/Qt 自动前台（可选，默认关）**：`CU_FG_AUTO=1` 或 `-FgAuto` 时这类窗口的动作自动走前台（`fg_auto:true`），`-BgForce` 单次退回；默认关闭以保持"不动光标、不抢焦点"。
- **后台动作失败给 `hint`**：非客户区、Chromium 忽略后台消息、"没变化"等直接返回可读原因与下一步建议，省掉一轮排查。
- **web 点击后校验**：派发后校验该点是否仍命中目标元素（`verified`），布局移动时安全补点一次（`retried:true`）；元素已消失（跳转）不补点。
- `BgMouse` 返回 `in_client`；`click` 返回 `method`。

验证与打包：

- 新增 `web/bench/desktop.ps1`（自建 WinForms 测试窗口的全链路回归：blank 重试、OCR 缓存、QuickHash 变更检测、UIA 直调、坐标回退、settle、剪贴板、emoji、`-Fg`），本机全部通过；`web/bench/bench.ps1` 与 v6 基线对比无回退。数字见 `skills/computer-use/REFERENCE.md`。
- SKILL.md：调用行改为 Git Bash 可用写法（原 `CU='"..."'` 只在 cmd.exe 成立）、补新参数/字段/环境变量、错误码表补 `ERR_DISABLED`/`ERR_OCR_TIMEOUT`/`ERR_TYPE`、修正与实测不符的性能宣称。
- 新增 `win/build.ps1`：可复现编译 `client.cs → cu.exe` 与核心 DLL；包内带预编译 DLL（hash 命中即省首次 3–8s 编译，不匹配自动现场编译）。
- CHANGELOG 移入插件包（原来在仓库根，插件安装后读不到）。

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
