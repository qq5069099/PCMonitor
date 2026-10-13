# CLAUDE.md

#对话使用:中文沟通

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 构建与运行

```bash
# 构建整个解决方案 (Release)
dotnet build PCMonitor.sln -c Release

# 单独构建各项目
dotnet build Server/Server.csproj -c Release
dotnet build PCClient/PCClient.csproj -c Release

# 发布 PCClient 为单文件 Windows exe
dotnet publish PCClient/PCClient.csproj -c Release -r win-x64

# 发布 Server
dotnet publish Server/Server.csproj -c Release -r win-x64
```

此项目中没有测试。

## 架构

Windows 专用 .NET 8 双进程解决方案（目标框架 `net8.0-windows`）。两个项目均输出为 `WinExe`（无控制台窗口）。

### Server（`Server/`）—— ASP.NET Core Web API

- 监听 `http://0.0.0.0:999`，通过静态文件提供 Web 前端页面
- WebSocket 端点 `/ws` 通过查询参数区分两种角色：`?role=pc`（被控端）和 `?role=viewer`（浏览器监控端）
- 将已连接 PC 的 JPEG 屏幕帧转发给正在观看的浏览器端（二进制 WebSocket 帧格式：8字节服务器时间戳 + 1字节 PC ID 长度 + PC ID + 原始 PC 数据）
- 通过 `TextDB` 持久化 PC 元数据（名称、锁定状态、备注）—— 一个基于文件的自定义 JSON 键值存储，存储在 `data.db`，可选 AES-CBC 加密
- 将远程控制指令（鼠标、键盘、锁定、重启、关机）从浏览器端转发至被控 PC
- 启动时从 `TextDB` 恢复 PC 列表和锁定状态；被锁定 PC 重连后自动重新下发 `lock_input`
- 处理 `watch`、`watchAll`、`unwatch` 操作；当无 viewer 观看时自动停止 PC 推流

**定时截图归档（每 5 分钟留档，默认关闭）**

- 存储：`{程序目录}/screenshots/{pcId}-{yyyy-MM-dd}/{HHmmss}.jpg`（目录名与文件名本身即时间排序键，目录日期用服务器本地时间）
- 保留 **14 天**（配置项 `ScreenshotRetentionDays`）；后台任务每 30 分钟清理一次过期目录，启动时先跑一轮
- 开关按机器持久化在 `data.db` 的 `pcs.{pcId}.screenshotUpload`（bool，缺键默认 `false` = 不上传）；单张体积档位存 `pcs.{pcId}.screenshotMaxKB`（int，只做 1~4096 护栏）、间隔存 `screenshotIntervalMin`（int，只做 1~1440 分钟护栏）、该档位对应的截图参数存 `screenshotWidth`/`screenshotQuality`（int，0=未指定）、时段限制存 `screenshotWorkTimeOnly`（bool，默认 `true`）。全部由 viewer 通过同一个 WS 动作 `setScreenshotUpload`（带 `enabled` + `maxKB` + `intervalMin` + `width` + `quality` + `workTimeOnly`）修改，并随 `pcList` 广播给前端（键名同名的 PascalCase 形式）
- **改动立即生效**：`setScreenshotUpload` 处理完会通过 `PushShotConfigAsync` 向被控 PC 发 `{"command":"screenshot_config", enabled, maxKB, intervalMin, width, quality, workTimeOnly}`；PC 每次注册/重连上线时也会补推一次（离线期间改的设置在上线时生效）
- 档位同时决定画质：**由前端档位表下发**（见下「配置单一来源」），PC 端直接采用 `width`/`quality`；只有老数据（`width=0`）才回落到 `Worker.PresetForFbKB()` 按体积上限阈值推算。最终体积仍由 `CaptureSnapshot` 的降质/降分辨率兜底
- HTTP 接口（PC 端未登录，故前两个在 `IsPublicPath` 中放行；上传仅接受 `data.db` 中已存在的 pcId，体积上限 4MB，pcId 做路径段清洗防穿越）：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/screenshot/config?pcId=X` | 返回 `{enabled, maxKB, intervalMin, width, quality, workTimeOnly}`，PC 端每轮上传前查询（作为推送的兜底） |
| POST | `/api/screenshot/upload?pcId=X` | 请求体为裸 JPEG，落盘后返回 `{ok,file,bytes}` |
| GET | `/api/screenshots?pcId=X&date=yyyy-MM-dd` | 需登录 + 有该 PC 权限；返回 `{dates,date,shots}`，`date` 省略时取最新一天，`shots` 按**时间正序**（接口契约不变；历史截图页在渲染时整体倒序，**新的在前**，看大图的 `‹`=更晚、`›`=更早） |
| DELETE | `/api/screenshots?pcId=X[&date=D][&name=F]` | 需登录 + 权限；带 `name` 删单张、仅带 `date` 删当天、都不带删该机全部 |
| GET | `/shots/{pcId}-{date}/{HHmmss}.jpg` | 第二个 `UseStaticFiles` 提供的静态访问，位于认证中间件之后（必须登录） |

- 前端：齿轮设置面板里点「📷 定时截图设置」打开**独立弹框**配置（「开启定时上传」与「仅工作时间段」两个勾选项并排一行、上传间隔下拉、体积上限勾选式单选（选项均由本文件的选项表生成，见下））。设置面板本身的备注名/定时锁定时段是**只读展示 + 右侧 ✏️ 按钮**点开弹框编辑，以压缩面板高度；所有弹框都有 `max-height` + 内部滚动，小屏不会把「← 返回」挤出可视区。PC 列表页顶栏「🖼️ 历史截图」进入独立页面，可选机器 + 选日期（**下拉框**按天浏览，已去掉逐天 ‹ › 按钮；日期下拉只列出有截图的日子），缩略图按时间正序排列，点图放大可翻页（滚轮/双指缩放、拖动平移、双击复位，不会滚动到底层页面）
- 📷 定时截图弹框在**两个入口**共用一个（PC 设置面板的按钮、历史截图页日期下拉右侧的按钮），弹框目标机器由调用方传入（`openShotCfg(pcId, pcName)`；不传则用设置面板的 `pendingPcId`），z-index 高于历史截图页
- 历史截图墙的滚动由外层 `#shotsGridWrap` 承担，网格 `#shotsGrid` 本身**不做滚动容器**：网格自己 `overflow-y:auto` 时，`width:100%` 的图片会让自动行高塌陷成十几像素（图片被裁成细条，且总高度恰好不溢出），表现为既不滚动也看不到完整图

### PCClient（`PCClient/`）—— .NET Worker Service（BackgroundService）

- 作为 `?role=pc` 连接 Server WebSocket，以本机机器名注册
- 截图循环：当 `_streaming` 为 true 时，按 Server 要求的帧率/画质截图并以二进制帧发送（8字节客户端 UTC 时间戳 + JPEG 数据）
- **定时截图归档循环**：`ExecuteAsync` 并行运行连接循环与 `RunScreenshotLoopAsync`。时序分两步：`DelayToNextWindowStart` 让**截图固定在每个间隔窗口的起点**（分钟数能被间隔整除的整分，如间隔 5 → :00 :05 :10）拍，保证截图时刻整齐；`RandomUploadDelay` 让**拍完不立即上传**，而是等窗口内一段随机时长（两端各留 5 秒，如 5 分钟窗口即 5~295 秒）再 `POST /api/screenshot/upload`（裸 JPEG）——截图时刻整齐、上传流量打散，避免多台机器挤在同一秒。到点后按服务器配置（开关 / 是否仅工作时间段 / 间隔 / 档位）决定是否截图。**上传失败会一直重试直到成功**（退避 10/20/40/60 秒，退避期间开关被关掉则放弃）。配置改动由服务器 WS 推送 `screenshot_config` 触发，`SleepOrConfigChangedAsync` 会打断当前等待立即重算 —— 设置即生效，PC 离线则等它上线时补推。客户端不落盘，节奏不受 WS 断线重连影响
  - 时段判断 `InShotWindow(cfg)`：服务器勾了「仅工作时间段」才判断 `WorkPeriods`；**未配置 WorkPeriods 时视为全天允许**（否则只放 exe、没有 appsettings.json 的客户端会永远静默不上传）
- **ScreenCapture**（静态类）：支持多显示器 —— 通过 `Graphics.CopyFromScreen` 捕获所有屏幕，等比缩放后水平拼接为单张 JPEG。在拼接图上绘制黄色圆圈 + 十字准星光标覆盖层及空闲时长标签。在远程控制模式下（收到控制指令后 60 秒内）改绘标准箭头光标。空闲检测采用 60 秒滑动窗口 + 反作弊机制（要求 60 秒内至少有 6 次不同的真实移动，每次位移超过 30px）
  - `Capture(maxWidthPerScreen, quality, isControlMode)`：实时推流用，不限制体积
  - `CaptureSnapshot(maxWidthPerScreen, quality, maxBytes)`：归档用。多屏仍拼成**同一张图**，先降 JPEG 质量、质量到底线仍超标再按体积比例缩小画布，保证单张（含双屏）不超过 `maxBytes`（默认 300KB），最后在右上角烧入 `yyyy-MM-dd HH:mm:ss` 截图时间
  - `IsWorkPeriod(workPeriods)`：仅按时间段判断（不含「周日不算」规则），供截图归档复用；`InWorkTime` = `IsWorkPeriod` 且非周日，专用于离岗告警
- **NativeSessionHelper**（静态类）：P/Invoke 封装 —— `SendInput` 实现鼠标/键盘注入（含多显示器虚拟桌面坐标映射）、`ClipCursor` 实现鼠标锁定、`LockWorkStation` 锁屏、通过 `shutdown.exe` 实现关机/重启
- 启动时通过 `Utility.EnsureInStartup()` 在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册自启动

### 配置单一来源（重要约定）

**所有「有哪些可选值」只写在 `Server/wwwroot/index.html`**（`SHOT_INTERVAL_OPTIONS` / `SHOT_KB_OPTIONS` / `QUALITY_PRESETS` 三张表，
页面上的间隔下拉、体积档位勾选、画质按钮都由它们渲染）。服务器与 PC 端**只做范围护栏，不再列举合法值**：

- 服务器 `NormalizeShotMaxKB` / `NormalizeShotInterval` / `NormalizeShotWidth` / `NormalizeShotQuality` 仅 Math.Clamp
- PC 端 `Worker.SafeInterval()` 与 `Min/MaxCaptureWidth` 同样只是护栏；档位的分辨率/质量由服务器透传（`width`/`quality`），实时推流的每屏像素宽由 viewer 的 `watch` 指令带 `maxWidth` 透传
- 因此**加/改一个间隔、体积档位或画质档位，只改 index.html 一处**，不必重发服务器，更不必重发客户端 exe（客户端 exe 一旦分发就要每台机器下载 ~150MB 并重启，代价最高）

### 核心数据流

```
PCClient(们) ──WebSocket (二进制 JPEG 帧)──▶ Server ◀──WebSocket──▶ 浏览器 viewer(们)
                ◀──WebSocket (JSON 指令)──             ──JSON 指令──▶
                ──HTTP POST 截图(每5分钟)──▶ screenshots/{pc}-{date}/ ◀──HTTP 历史截图页──
```


### 共享工具类（`Utility.cs`，两个项目中各自复制一份）

AES-256-CBC 加解密、MD5 哈希、注册表自启动注册。两份代码完全一致，有意不提取为共享库。

### 配置文件（`PCClient/appsettings.json`）

- `ServerUrl` —— WebSocket 服务器地址
- `EmployeeUrl` —— 空闲告警 API 地址（向 `/api/Plan/item/add` 发送 POST 请求）
- `WorkPeriods` —— 工作时间段数组（如 `"8:40~12:00"`），空闲告警与定时截图上传都只在此时间段内触发
- `ScreenshotMaxWidth` —— 定时截图每屏最大像素宽（默认 1600）
- `ScreenshotQuality` —— 定时截图 JPEG 起始质量（默认 50）
- `ScreenshotMaxBytes` —— 定时截图单张体积上限，含多屏拼接（默认 307200 = 300KB；超限自动降质/降分辨率）
- （归档间隔/体积档位不在这里配置：由服务器按网页端的设置下发，见下「配置单一来源」）

`Server/appsettings.json` 另有 `ServerPort`（默认 999）与可选 `ScreenshotRetentionDays`（默认 14）。

## 关键依赖

- `Microsoft.Extensions.Hosting.WindowsServices`（PCClient）—— Worker Service 托管
- `System.Drawing.Common` / Windows Forms（PCClient）—— 通过 GDI+ 实现屏幕截图
- 两个项目均以 `win-x64` 运行时标识发布
