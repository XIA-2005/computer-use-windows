---
name: computer-use
description: 操作 Windows 桌面应用与专用浏览器实例的原语工具包：截图给多模态模型直接看、DPI 精确点击/输入/OCR 找字（cu.exe），附 Chrome DevTools Protocol 浏览器层（DOM 级定位点击，不截图、不动日常浏览器）。当需要在本机自动化 GUI 或浏览器任务、截图验证界面状态、精确点击输入、读取屏幕文字时使用。
---

# Computer Use Skill — Windows v6（快 + 准 + 浏览器 CDP）

> **路径基准**：本文件位于分享包 / ZCode 插件的 `skills\computer-use\` 下。下文提到的 `win\`、`web\`、`state\` 一律相对于**分享包 / 插件根**（即本文件所在目录的上两级）；`cu.exe` 的完整相对路径 = 本技能目录的 `..\..\win\cu.exe`。

> 定位：与具体应用无关的 Windows Computer Use 原语。截图交给**原生多模态模型直接看**，模型输出图上坐标，脚本负责把坐标**精确**映射回屏幕并注入。ShunCode 本体零改动。
> 当前 v6（2026-09-26）：浏览器层 CDP 重做（真实鼠标点击、可信输入、文字定位引擎），桌面层提速（OCR 内存直通与结果复用）。完整版本历史见仓库根 `CHANGELOG.md`，实测数据与 Electron 案例见同目录 `REFERENCE.md`。

## 铁律（不变）

1. 感知 = 原生多模态模型直接看截图。禁止把截图转成字符画/色表/逐点 JSON 再决策。
2. OCR 只用于**文字定位/精确取值**（`find` / `-Find`），不替代视觉判断。
3. 成功标准 = 截图上可验证的变化，不只看返回码。
4. 只操作用户指定的窗口；默认后台注入、不动光标、不抢焦点。

## 调用方式

```bash
CU='"<本技能目录>\..\..\win\cu.exe"'      # 推荐：快（<本技能目录> = 本 SKILL.md 所在目录；cu.exe 固定在插件根的 win\ 下；值里嵌双引号是故意的——路径可能含空格）
$CU <cmd> [参数]        # 每条命令只输出一行 JSON；ok:false 时退出码 1
# 兼容/兜底：powershell -NoProfile -ExecutionPolicy Bypass -File "<本技能目录>\..\..\win\cu.ps1" <cmd> [参数]
```

- `cu.exe` 参数与 `cu.ps1` 完全相同。它把命令交给后台常驻的 `cu.ps1 serve`（命名管道，仅当前用户可连），省掉每次启动 PowerShell + 加载 DLL 的 ~500ms。第一次调用会自动拉起常驻进程（约 1–2 秒），空闲 20 分钟自动退出（`CU_IDLE=分钟`）。改了 `cu.ps1/cu.cs/uia.cs` 会自动换新进程。
- 常驻进程不可用时自动回退为直接运行 `cu.ps1`；`CU_NODAEMON=1` 强制不用常驻；`cu.exe --stop` 手动停止。
- 首次运行会用 .NET 自带 csc 把 `cu.cs + uia.cs + web.cs` 编译到 `win/bin/cu-<hash>.dll`，之后直接加载。状态文件默认在 `computer-use/state/`（可用环境变量 `CU_STATE` 改）。

## 命令速查

| 命令 | 作用 | 关键参数 |
|---|---|---|
| `info` | 列可见窗口（hwnd/标题/进程/矩形/DPI/前台）+ 屏幕几何 | `-Title 过滤` `-All` |
| `snap` | 截窗口（或全屏）→ 图 + **帧文件** | `-Title/-Proc/-Hwnd` `-Out` `-Region x,y,w,h` `-Grid 100` `-MaxSide` `-Method auto/screen/print` `-Restore`；`-Marks`（控件编号图 `cur-marks.jpg` + 列表）/ `-Uia`（只要列表） |
| `els` | 列出窗口的可交互控件（UIA）：`[编号,类型,名称,cx,cy,w,h]` | `-Text2`（含文本元素）`-UiaTimeout 1500` |
| `zoom` | 以帧图上一点为中心，截**原生分辨率**放大图（自带网格），并成为新的当前帧 | `-X -Y -R 120 -ZoomSize 900` |
| `click` / `double` / `rclick` / `mclick` | 点击 | `-X -Y`（帧图坐标）/ `-Id N`（marks 编号）/ `-Name 控件名`（UIA，找不到自动转 OCR）/ `-Find 文字`（OCR）；`-Mods ctrl,shift`；`-Fg`；`-Snap`；`-NoSnapTo` |
| `move` / `down` / `up` | 悬停 / 按下 / 抬起 | 同上 |
| `drag` | 拖拽 | `-X -Y -X2 -Y2` |
| `scroll` / `hscroll` | 滚轮（负=向下/向左） | `-X -Y -Wheel -3` |
| `key` | 按键/组合键，空格分隔为序列 | `-Keys "ctrl+a delete"` `-Repeat n` |
| `type` | 输入文字（可先点击输入框、可回车） | `-Text` / `-TextB64`（UTF-8 base64，中文最稳）/ `-TextFile`；`-X -Y` / `-Id` / `-Name` / `-Find`；`-Enter`；`-Verify`（读回输入框内容核对）；`-Method auto/replacesel/char/clip` |
| `mark` | 点前预览：画准星 + 生成 4× 放大核对图 | `-X -Y` 或 `-Pts "x:y,x:y"`；`-Zoom 24`（0=不出放大图） |
| `find` | OCR 找字，返回帧图坐标（中心 cx,cy）；返回 `pass`（plain/prep 哪一遍命中）和 `cached`（画面未变，复用了上次识别） | `-Find 文字` `-Index n` `-Region x,y,w,h`（只识别帧图的一块，快 3–8 倍） |
| `ocr` | 全部文字行 + 坐标（帧图坐标） | `-Path 图片`（识别指定图片）；`-Region x,y,w,h`（只识别帧图一块，快）；`-Method auto/screen/print` |
| `wait` | 等文字出现 / 等画面稳定 / 固定等待 | `-Find 文字 -Timeout 8000` / `-Stable` / `-Ms 300` |
| `activate` | 把目标窗口置前（前台模式前用） | |
| `frame` | 打印当前帧信息 | |
| `do` | 一次进程内执行多步（快） | `-Steps '[{"cmd":"click","X":1,"Y":2},...]'` 或 `-StepsFile`；也可混 `{"cmd":"web","Sub":"click",...}` |
| `web <子命令>` | **浏览器 CDP 层**（见下文「浏览器层」）：点按钮/填表/读字/截图都不走屏幕 | `-Browser edge\|chrome` `-Url` `-Sel` `-Text`/`-TextB64` `-Id` `-Index` `-Exact` `-Js` `-X -Y` `-Wheel` `-NewTab` `-Marks` `-Verify` |

通用：`-Frame <帧文件或图片路径>` 指定用哪张截图的坐标系（默认最近一次 snap/zoom）；`-Screen` 表示坐标是物理屏幕像素；`-Force` 跳过窗口移动检查；`-Settle 毫秒` 动作后最多等多久画面稳定（默认 1000，0=不等）；`-Quiet 毫秒` 动作后这么久画面（整窗 + 点击点周围局部）都没变化就提前返回（默认 400）。

别名：`dbl`=`double`、`rclick`/`mclick`=`click`（右/中键）；web 子命令 `go`/`nav`=`open`、`locate`=`find`、`txt`=`text`、`value`=`val`、`elements`=`els`、`forward`=`fwd`；`type -Method paste` 等于 `-Method clip`。

OCR 语言前提：Windows OCR 只识别「设置 → 时间和语言 → 语言」里已安装的语言（中英混排界面需对应语言包都在）；一个可用语言都没有时 `find`/`ocr` 报 `ERR_NO_OCR`。

## 坐标协议：帧（frame）—— 零换算、DPI 安全

- 全程 **Per-Monitor DPI Aware**，所有屏幕坐标都是物理像素。v2 在 125%/150%/200% 缩放下会点偏，v3 已修复。
- 每次 `snap`/`zoom` 生成一张图和一个帧文件（`<图>.frame.json` + `state/last.frame.json`），记录 `ox,oy`（图左上角在屏幕上的位置）、`s`（缩放比例）、`hwnd`、窗口边界。
- 模型报**图上坐标** → 动作命令用帧自动换算：`屏幕 = o + (图坐标 + 0.5) / s`。截图被缩小过也不影响精度（缩多少就换算回多少）。
- 窗口被移动/缩放/关闭/最小化后，旧帧自动失效，动作直接拒绝（`ERR_STALE_FRAME` 等），不会误点。
- 截的是 DWM 可见边界（不含透明阴影边），默认最长边 ≤1568、总像素 ≤115 万：模型看得清、传得快、不会被模型端二次缩放。
- 窗口被其他窗口挡住时自动用 PrintWindow 截图，不用把窗口放到前面。

## 标准操作流程（快）

```bash
$CU snap -Title "记事本"                       # 1. 截图 → 看图（打开 state/cur.jpg）
$CU click -X 412 -Y 230 -Snap                   # 2. 点击，等画面稳定后自动回截图 → 直接看新图
$CU type -X 300 -Y 400 -TextB64 5L2g5aW9 -Enter # 3. 点输入框 + 输入 + 回车，一条命令完成
```

- **一动一截**：动作加上 `-Snap`（也可 `-Snap -Marks`），返回里的 `after` 就是动作后的新帧，下一步直接用，不用再单独 snap。返回里的 `changed`（画面变化百分比）和 `settled` 可以用来判断：`changed≈0` 说明这次点击多半没生效。
- **有文字就用文字定位**：按钮/菜单/链接上有字时优先 `click -Find "保存"`（OCR 在原生分辨率上找字，比目测坐标更准），多个匹配用 `-Index`。
- **连续的确定动作用 `do` 批量执行**（一次进程、零启动开销），最后一步加 `"Snap":true`：
  ```bash
  $CU do -Title "记事本" -Steps '[{"cmd":"key","Keys":"ctrl+a"},{"cmd":"type","Text":"hello"},{"cmd":"key","Keys":"ctrl+s","Snap":true}]'
  ```
- 等待加载用 `wait -Find "完成"` 或 `wait -Stable`，**不要**用固定 sleep 盲等。

## 浏览器层（CDP）—— 操控浏览器最快最准的方式（v6）

Chrome/Edge 内置调试协议（DevTools Protocol）。`web` 系列命令直接在页面里定位元素，用 CDP 注入**可信的鼠标/键盘事件**（浏览器把它们当成真人输入：pointerdown/mousedown/hover 全有，但不动系统光标、不抢焦点）：**不截图、不 OCR**，单步内部耗时 2–30ms，含 `cu.exe` 管道开销整条命令 45–85ms（屏幕路径对应要 600–1200ms）。页内助手在 `win\web-lib.js`（改它立即生效，不用重启常驻进程）。

- **专用独立实例**：独立用户目录 `state\web\{edge|chrome}`（登录过的网站会保持登录），调试端口只监听 `127.0.0.1`（edge=9462，chrome=9461）。**永远不会附着到你日常开的浏览器窗口**；启动带 `--disable-sync`，不会被账号同步污染。
- 其它 `web` 命令发现浏览器没开时会自动拉起；当前标签页会记住（常驻进程重启不丢）。`-Browser chrome` 切换浏览器（默认 edge）。

| 命令 | 作用 | 关键参数 |
|---|---|---|
| `web start` / `web stop` / `web status` | 启动 / 关闭整个专用实例（stop 会关掉它的所有标签） / 查看状态与标签列表 | `-Url`（start 时打开） |
| `web open -Url 地址` | 打开网址并**等页面加载完**才返回（返回 `title`、`ready`=complete/interactive；DOM 就绪 1.2s 后 load 仍没来就按 interactive 返回并 `warn`，页面已可用） | `-NewTab` 新标签；`-Timeout 8000`；无 scheme 自动补 `https://` |
| `web els` | **列出可交互元素并编号**：`[编号,类型,文字,x,y,w,h]`（视口 CSS 坐标；整页、含 shadow DOM / 同源 iframe，只列可见的） | `-Sel` 限定范围；`-All` 连 li/td/option 也列；`-Size 300` 上限 |
| `web find` | 只定位不动作：返回命中元素、`count`（同文匹配数）、`alts`（其它候选）、`hit`（中心点是否真能点到） | 同 click 的定位参数 |
| `web click` | 点页面元素：**默认真实鼠标**（move→press→release），先命中测试；点不到（被遮挡）自动退回 DOM click 并给 `warn`；点开新标签的链接会**自动跟过去**（返回 `newTab:{url,title}`，`web tab -Index N` 可切回）；会话标签在后台时先自动激活（`activated:true`） | `-Sel "css选择器"` / `-Text "按钮文字"`（`-Find`、`-TextB64`、`-FindB64` 同义）/ `-Id N`（els 或 shot -Marks 的编号）/ `-X -Y`（上次 `web shot` 图上的坐标）；`-Index 2` 第 2 个匹配；`-Exact` 整段文字相等；`-Method auto\|mouse\|js\|double`（mouse=被遮挡就报错不乱点；js=只发 DOM click）；`-Button right\|middle` |
| `web hover` | 悬停（真实 mousemove：菜单/提示会弹出） | 同 click 的定位参数 |
| `web scroll` | 滚动：把元素滚到视口中央 / 滚轮 / 像素 | `-Sel/-Text/-Id`（滚到元素）；`-Wheel -5`（负=向下，每格 100px）；`-Y 600 -X 0`（scrollBy） |
| `web type` | 填表：默认**可信输入**（聚焦 → 全选 → `Input.insertText`，浏览器按真人打字处理：beforeinput/input、联想、Enter 提交都正常）；`<select>`、不可见目标、超长文本走原生 setter + `InputEvent` | `-Sel`/`-Id`（省略=当前焦点元素，无焦点报错不乱打；目标不可见会给 `warn`）；`-Text/-TextB64/-TextFile`；`-Append`（光标移到末尾再输）；`-Enter`；`-Verify` 读回核对；`-Method value` 强制 setter（最快，但有些站的回车不认）/ `-Method keys` 强制可信输入；`<select>` 按 value / 显示文字 / 包含匹配；清空用 `web keys -Sel X -Keys "ctrl+a delete"` |
| `web keys` | 页面按键/组合键 | `-Keys "ctrl+s"`、`"enter"`、`"tab"`、`"ctrl+a delete"`（空格分隔序列；ctrl+a/c/v/x/z 带编辑命令，任何输入框都生效）；`-Sel/-Id` 先聚焦到目标；中文走 insertText |
| `web text` | 读页面/元素文字（返回里带 url、title） | `-Sel`（默认 body；超长自动截断10万字符） |
| `web val` | 读输入框/富文本当前值 | `-Sel`/`-Id`（默认焦点元素） |
| `web eval -Js` | 执行任意 JS，返回值自动 JSON 化（页内可直接用 `__cu.*` 助手） | `-Timeout 10000` |
| `web shot` | 截**视口**图（浏览器直接按比例输出，默认最长边 1568、JPEG） | `-Out` `-Full`（整页）`-Marks`（**给可见的可交互元素画编号框**，返回 `elements` 列表，之后 `click -Id N`）`-Quality 85` `-MaxSide`；返回 `scale`（图像px→CSSpx）、`dpr`、`sx/sy`；之后可用 `web click -X -Y`（页面滚动了也会按帧记录的位置换算） |
| `web wait` | 等元素**可见** / 等文字 / 等 URL / 等加载完成 / 等页面文字**稳定**；条件一满足立刻返回（页内 MutationObserver，不是轮询） | `-Sel` `-Find 文字` `-Url 子串` `-Ready` `-Stable`（正文 500ms 无变化；`-Ms` 改窗口）`-Timeout 8000`；都不给时 `-Ms 300` 固定等待 |
| `web tabs` / `web tab` | 列标签 / 切换标签并记住 | `tab -Index 2` / `-Url 子串` / `-Title 子串` |
| `web close` | 关闭当前（或匹配的）标签 | 同 tab 的选择参数 |
| `web reload` / `web back` / `web fwd` / `web info` | 刷新 / 后退 / 前进 / 当前页 url+title+ready | |

标准流程：

```bash
$CU web open -Url https://example.com          # 打开并等加载完（本地页 ~60ms，含网络 0.3–1.5s）
$CU web els                                      # 一眼看清页面上能点/能填的东西（编号、文字、位置），不用截图
$CU web type -Id 13 -TextB64 5YWz6ZSu6K+N -Verify   # 往 13 号输入框填字（中文用 B64）并读回核对
$CU web click -Text "百度一下"                    # 按文字点：返回 count/alts，count>1 说明有同名元素，看 alts 决定是否加 -Index/-Sel
$CU web wait -Url "wd=" -Timeout 8000            # 等跳转 / 或 web wait -Sel "#result" 等结果出现 / web wait -Stable 等页面稳定
$CU web text                                     # 读结果文字
$CU web shot -Marks -Out state\page.jpg          # 需要模型看页面长相时才截图；图上有编号 → click -Id N；也可报图上坐标 → click -X -Y
$CU web stop                                     # 用完关掉整个实例
```

- **定位优先级**：`-Id`（els/shot -Marks 编号，最准）≈ `-Sel`（CSS）> `-Text`（按字，含 aria-label/title/placeholder/label/alt）> 截图坐标（canvas/地图/验证码才用）。`-Text` 命中多个时按「整段相等 > 前缀 > 包含、可交互元素优先、刚填过字的表单同组优先、可见优先、面积小优先」排序，`-Index n` 取第 n 个；结果里的 `count`/`alts` 告诉你有没有歧义。
- **点击返回值**：`method:"mouse"` = 真实鼠标事件已发出；`hit:false` + `warn` = 中心点被别的元素挡住（遮罩/悬浮条/弹窗），已改用 DOM click——这时先看一眼 `cover` 是什么，弹窗就先关掉；`frames:false` = 浏览器窗口被最小化/隐藏，鼠标事件会卡住，已自动改用 DOM click。
- **点击不会等跳转**：点完链接如需等待，接 `web wait -Ready` / `web wait -Url 子串` / `web wait -Stable`（`web open` 自带等待）。开了新标签会自动跟过去（返回 `newTab`），跑完记得 `web close` 收拾，或 `web tab -Index N` 切回。
- **回车提交要核对**：`type -Enter` 后用 `wait -Url/-Sel` 确认真的走了；刚 `open` 完的页面脚本可能还没挂好监听（必应偶发），没反应就 `click` 搜索按钮，或 `wait -Stable` 后重试。
- **专用实例是独立账号环境**：知乎/京东/微博这类要登录或有风控的站会跳登录页或验证页（`open` 返回的 `url`/`title` 能看出来）；它不会、也不该借用你日常浏览器里的登录态。Cloudflare「正在进行安全验证」页通常几秒后自动过，用 `wait -Find 目标文字 -Timeout 15000` 等。
- 返回里的 `ackLate:true` = 浏览器收下了事件但确认迟到（页面正忙/正在跳转），动作已发出，接着 `wait` 即可。
- `web type` 后 `visible:false` 表示填进了一个不可见的输入框（老站常留着隐藏的表单）——真人不可能在那里输入，改用 `web els` 里列出的那个。
- Electron 应用内嵌页面**没有** CDP 端口，仍走原来的 `snap/click`（`-Fg`）；浏览器普通窗口（非专用实例）也不在 CDP 覆盖内。
- 实测数据（真实站点流程、本地基准、v5 对比）见同目录 `REFERENCE.md`，基准脚本在 `web\bench\`。

## 控件层（UI Automation）—— 比目测坐标更准

```bash
$CU snap -Title "记事本" -Marks      # 返回 elements:[[1,"Button","保存",x,y,w,h],...] + cur-marks.jpg（每个控件画编号框）
$CU click -Id 5                       # 点 5 号控件的中心（屏幕真实矩形，不受缩放误差影响）
$CU click -Name "保存设置"            # 不截图直接按名字点（UIA 找不到自动转 OCR）
$CU type -Id 6 -TextB64 5L2g5aW9 -Verify   # 输入后读回内容，返回 verify.contains=true/false
```

- 看图决策时优先看 `cur-marks.jpg`，目标有编号就用 `-Id`，没有编号（画布、图片、自绘界面）再用坐标。
- **坐标吸附**：当前帧有控件列表时（`snap -Marks/-Uia` 之后），`click -X -Y` 落在控件内会在返回中注明 `on`；偏出控件 ≤6 图像像素时自动吸到最近控件中心（`snapped_to`）。`-NoSnapTo` 关闭。
- `-Id` 只对产生它的那一帧有效：窗口移动/重截后要重新 `snap -Marks`（否则 `ERR_NO_MARKS` / `ERR_STALE_FRAME`）。
- UIA 调用有超时（默认 1.5s，`-UiaTimeout`），超时返回空列表不会卡住。
- **Chromium/Electron（QQ、微信新版、VS Code 等）通常不暴露 UIA 控件**（实测 QQ NT：0 个，超时），此时 `-Marks` 无编号，`-Name` 自动转 OCR——对这类程序直接用视觉坐标 + `-Find`。Win32 / WinForms / WPF / 资源管理器 / 设置等效果最好。

## 精确点击（准）

按目标大小分级，不要每次都走最重的流程：

| 目标 | 做法 |
|---|---|
| 大按钮 / 有文字 | 直接 `click -X -Y` 或 `click -Find 文字` |
| 小图标（<24px）/ 密集列表 / 棋盘格 | 先 `mark -X -Y`，看 `*-zoom.png`（4× 放大，中心像素用框标出），对准了再点 |
| 看不清 / 很小 / 高分屏 | `zoom -X -Y -R 80` → 在放大图（自带坐标网格）上重新读坐标 → 直接 `click`（zoom 会成为当前帧，坐标自动换算） |
| 需要读坐标刻度 | `snap -Grid 100`，图上叠加带数字的网格线 |

多个待点目标：`mark -Pts "x:y,x:y"` 一次核对，再用 `do` 按顺序点。

## 输入与按键

- 后台 `type` 默认 `-Method auto`：标准 Win32 编辑框用 `EM_REPLACESEL`（直接写入，中文/长文本一次完成，最可靠），其他控件逐字发 `WM_CHAR`。键盘消息发给**获得焦点的子控件**（v2 只发给顶层窗口，所以很多程序收不到）。
- `-Method clip`：通过剪贴板粘贴，完成后自动恢复用户原来的剪贴板内容。
- 前台 `-Fg`：用 `SendInput` 发 Unicode 字符（不依赖输入法、不占剪贴板）；超过 400 字自动改为粘贴。输入过程中焦点被切走会立即停止（`ERR_FOCUS_LOST`），不会打到别的窗口。
- 中文或包含特殊字符的文本请用 `-TextB64`（UTF-8 base64），避免 shell 转义和编码问题。
- `key` 在后台模式下：标准编辑框的 `ctrl+a/c/v/x/z` 直接走编辑消息（100% 生效）；其他程序的组合键是模拟的，部分程序会忽略（返回里有 `warn`）→ 用截图确认，不生效再按下面的规则切换到 `-Fg`。

## 后台 → 前台

后台注入（PostMessage）对传统 Win32 程序、多数对话框/编辑框有效。**Chromium/Electron/CEF、WinUI、UWP、DirectX 游戏**经常忽略后台消息。判断方法：点击或输入后 `changed≈0`，并且截图上没有变化。

- 前台模式 `-Fg` 会抢焦点，**必须上报并获得用户当次许可**才能使用。
- `-Fg` 会先把目标窗口置前，失败返回 `ERR_NOFOCUS`，什么都不发送；目标点被别的窗口挡住返回 `ERR_OCCLUDED`，不会误点；点击完成后光标恢复原位（`-KeepCursor` 可以关闭）。
- Electron 应用（QQ、微信新版、VS Code 等 `Chrome_WidgetWin_1` 窗口）实测结论：截图用后台（`-Restore`/PrintWindow），点击/输入直接 `-Fg`，不要先试后台浪费一轮；UIA 拿不到控件，直接视觉坐标 / `-Find`。完整实测案例见 `REFERENCE.md`。

## 错误码

任何一步失败 → 停下来上报，**不要**静默重试或自行切换到前台模式。同族错误码合并在一行；每条返回的 `msg` 都有具体原因。

桌面层（snap/click/type/find/mark/do）：

| 码 | 含义 / 处理 |
|---|---|
| `ERR_NO_WINDOW` | 找不到窗口 → `info -Title 关键字` 查看准确标题，或用 `-Proc` / `-Hwnd` |
| `ERR_ARGS` | 参数缺失或格式不对（消息里写明缺什么，如 `-Region` 要 x,y,w,h） |
| `ERR_NO_FRAME` | 没有当前帧 → 先 `snap`；`-Id` 必须用 `snap -Marks` 产生的帧 |
| `ERR_STALE_FRAME` / `ERR_WINDOW_GONE` / `ERR_FRAME_MISMATCH` | 帧失效：窗口被移动/缩放/已关闭/帧属于别的窗口 → 重新 snap（窗口没了先 `info`） |
| `ERR_OUTSIDE_IMAGE` | 坐标超出图片范围 → 检查是否用了错误的图 |
| `ERR_MINIMIZED` | 窗口已最小化 → `snap -Restore`（恢复窗口但不激活） |
| `ERR_REGION` / `ERR_CAPTURE` / `ERR_SAVE`、`warn: ERR_BLANK` | 截图失败类：区域在窗口外 / 抓帧失败（消息带实际用的 screen/print）/ 写图文件失败；空白图（Chromium 不绘制后台窗口）→ 重试或经许可 `activate` 后重截 |
| `ERR_NO_OCR` | 没有可用的 OCR 语言包 → 设置 → 时间和语言 → 语言，添加要识别的语言（OCR 只认用户语言列表） |
| `ERR_TEXT_NOT_FOUND` | OCR（或 UIA 兜底 OCR）没找到文字 → 看图确认文字真的可见，或改用坐标 |
| `ERR_OCR` / `ERR_CLIPBOARD` / `ERR_NO_PTS` / `ERR_SAME_FILE` / `ERR_MARK` | 参数与辅助类：OCR 预处理失败；剪贴板写入失败（改 `-TextB64`）；`mark` 缺 `-Pts`/`-X -Y`；输入输出同一文件；画标注失败 |
| `ERR_KEY` / `ERR_TIMEOUT` | 按键组合无法识别（查 `-Keys` 拼写）/ `wait -Find` 或编辑框写入超时（截图确认实际状态） |
| `ERR_NOFOCUS` / `ERR_OCCLUDED` / `ERR_FOCUS_LOST` | 前台模式的安全拦截，什么都没发送或已停止 |
| `ERR_NO_MARKS` / `ERR_NO_ELEMENT` | 当前帧没有控件列表 / 没有这个编号 → `snap -Marks` |
| `ERR_STEP` / `ERR_EXCEPTION` | `do` 第 N 步失败（返回带 `step` 下标和各步结果）/ 未捕获异常（看 msg） |
| `ERR_DAEMON` | 常驻进程中途退出 → 重试一次；仍失败设 `CU_NODAEMON=1` |
| `ERR_LOAD` | C# 编译失败（通常是 cu.cs / uia.cs 被改坏了） |

web 层：

| 码 | 含义 / 处理 |
|---|---|
| `ERR_TEXT_NOT_FOUND` / `ERR_NOT_FOUND` / `ERR_INDEX` | 页面里没有这段文字 / 选择器无匹配 / `-Index` 超出 `count` → `web els` 或 `web text` 看看页面到底有什么 |
| `ERR_OCCLUDED`（`-Method mouse`） | 元素中心被 `cover` 里的元素挡住，真实鼠标点不到 → 先关弹窗/滚动，或 `-Method js` |
| `ERR_HIDDEN`（`-Method mouse`） | 浏览器窗口最小化/不可见，无法产生帧 → 恢复窗口，或 `-Method js` |
| `ERR_NO_MARKS` / `ERR_NO_ELEMENT` / `ERR_STALE` | 还没 `web els` / 编号不存在 / 该元素已从页面消失 → 重新 `web els` |
| `ERR_NOT_EDITABLE` / `ERR_NO_FOCUS` / `ERR_DISABLED`（web type） | 目标不是输入框 / 没有焦点元素又没给 -Sel / 元素被禁用 |
| `ERR_NO_FRAME` | `web click -X -Y` 但还没有 `web shot` 的图 → 先 `web shot` |
| `ERR_NOT_RUNNING` | 专用浏览器没在运行 → `web start`（多数 web 命令会自动拉起，一般是刚 `web stop` 过） |
| `ERR_ARGS` | 参数问题（`-Browser` 取值、缺 `-Url`/`-Keys`/`-Js` 等，消息里写明） |
| `ERR_BROWSER` / `ERR_PROFILE` / `ERR_START` | 找不到 Edge/Chrome 安装 / 专用用户目录创建失败 / 浏览器启动失败或秒退 → 看 msg |
| `ERR_TIMEOUT` | 调试端口 12s 没开（可能被占用，`web stop` 后重试）/ 页面加载超时 / `wait` 条件未满足 |
| `ERR_CDP` / `ERR_WS` / `ERR_CLOSED` / `ERR_ATTACH` / `ERR_NAV` | CDP 通信错误（响应异常、WebSocket 连不上/断开、标签附着失败、导航被拦截）→ 浏览器正忙或刚崩，`web stop` 后重来 |
| `ERR_JS` / `ERR_CLICK` / `ERR_KEYS` / `ERR_SEL` / `ERR_LOAD` / `ERR_SHOT` | 页内执行类：eval 的 JS 抛异常 / DOM click 失败（页面可能刚跳转，重试）/ 未知修饰键 / 选择器语法错 / `web-lib.js` 加载失败 / 截图数据为空 → 各看 msg |

## 安全规则

- 只操作会话里用户指定的窗口；全屏截图或其他窗口需要另外授权。
- 默认不动光标、不抢焦点，用户可以同时使用电脑。
- 不读取密码框或敏感区域。
- 前台模式（`-Fg`）需要用户当次明确许可。

## 兼容旧脚本

`snap.ps1 / act-bg.ps1 / act.ps1 / mark.ps1 / type.ps1 / info.ps1 / ocr.ps1` 仍可按旧参数调用，内部转发到 `cu.ps1`，输出改为 JSON 格式（v2/v3 的原始脚本只存在于旧版分享包，本包内没有备份副本）。

## 截图回传

截图文件通过原生图像通道（读图 / 附件）交给模型。不要通过 run_command 用 base64 回传（约 64KB 截断，分片读取会丢字节）。
