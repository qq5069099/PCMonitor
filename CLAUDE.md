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
- 开关按机器持久化在 `data.db` 的 `pcs.{pcId}.screenshotUpload`（bool，缺键默认 `false` = 不上传）；单张体积档位存 `pcs.{pcId}.screenshotMaxKB`（int，只接受 **100/200/300/500/800**，其它值回落 300）。两者都由 viewer 通过同一个 WS 动作 `setScreenshotUpload`（带 `enabled` + `maxKB`）修改，并随 `pcList` 广播给前端（键名 `ScreenshotUpload` / `ScreenshotMaxKB`）
- 档位同时决定画质：PC 端 `Worker.PresetForKB()` 把 100/200/300/500/800(KB) 映射为每屏 1280/1600/1920/2560/3840px + JPEG 质量 50/55/60/65/75，档位越大越清晰；最终体积仍由 `CaptureSnapshot` 的降质/降分辨率兜底
- HTTP 接口（PC 端未登录，故前两个在 `IsPublicPath` 中放行；上传仅接受 `data.db` 中已存在的 pcId，体积上限 4MB，pcId 做路径段清洗防穿越）：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/screenshot/config?pcId=X` | 返回 `{enabled, maxKB}`，PC 端每轮上传前查询 |
| POST | `/api/screenshot/upload?pcId=X` | 请求体为裸 JPEG，落盘后返回 `{ok,file,bytes}` |
| GET | `/api/screenshots?pcId=X&date=yyyy-MM-dd` | 需登录 + 有该 PC 权限；返回 `{dates,date,shots}`，`date` 省略时取最新一天，`shots` 按**时间正序** |
| DELETE | `/api/screenshots?pcId=X[&date=D][&name=F]` | 需登录 + 权限；带 `name` 删单张、仅带 `date` 删当天、都不带删该机全部 |
| GET | `/shots/{pcId}-{date}/{HHmmss}.jpg` | 第二个 `UseStaticFiles` 提供的静态访问，位于认证中间件之后（必须登录） |

- 前端：齿轮设置面板里配置「📷 定时上传截图」开关 + 「单张体积上限」五档勾选式单选（勾一项自动取消其余，不允许全不选）；PC 列表页顶栏「🖼️ 历史截图」进入独立页面，可选机器 + 选日期（按天浏览），缩略图按时间正序排列，点图放大可翻页，支持删单张/删当天/删全部

### PCClient（`PCClient/`）—— .NET Worker Service（BackgroundService）

- 作为 `?role=pc` 连接 Server WebSocket，以本机机器名注册
- 截图循环：当 `_streaming` 为 true 时，按 Server 要求的帧率/画质截图并以二进制帧发送（8字节客户端 UTC 时间戳 + JPEG 数据）
- **定时截图归档循环**：`ExecuteAsync` 并行运行连接循环与 `RunScreenshotLoopAsync`；后者对齐到「分钟数 % 5 == 0」的整分时刻（:00 :05 :10…），先判断是否落在 `WorkPeriods` 内，再 `GET /api/screenshot/config` 查询该机开关，开启时才截图并 `POST /api/screenshot/upload`（裸 JPEG）。客户端不落盘，节奏不受 WS 断线重连影响
- **ScreenCapture**（静态类）：支持多显示器 —— 通过 `Graphics.CopyFromScreen` 捕获所有屏幕，等比缩放后水平拼接为单张 JPEG。在拼接图上绘制黄色圆圈 + 十字准星光标覆盖层及空闲时长标签。在远程控制模式下（收到控制指令后 60 秒内）改绘标准箭头光标。空闲检测采用 60 秒滑动窗口 + 反作弊机制（要求 60 秒内至少有 6 次不同的真实移动，每次位移超过 30px）
  - `Capture(maxWidthPerScreen, quality, isControlMode)`：实时推流用，不限制体积
  - `CaptureSnapshot(maxWidthPerScreen, quality, maxBytes)`：归档用。多屏仍拼成**同一张图**，先降 JPEG 质量、质量到底线仍超标再按体积比例缩小画布，保证单张（含双屏）不超过 `maxBytes`（默认 300KB），最后在右上角烧入 `yyyy-MM-dd HH:mm:ss` 截图时间
  - `IsWorkPeriod(workPeriods)`：仅按时间段判断（不含「周日不算」规则），供截图归档复用；`InWorkTime` = `IsWorkPeriod` 且非周日，专用于离岗告警
- **NativeSessionHelper**（静态类）：P/Invoke 封装 —— `SendInput` 实现鼠标/键盘注入（含多显示器虚拟桌面坐标映射）、`ClipCursor` 实现鼠标锁定、`LockWorkStation` 锁屏、通过 `shutdown.exe` 实现关机/重启
- 启动时通过 `Utility.EnsureInStartup()` 在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册自启动

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
- `ScreenshotIntervalMinutes` —— 归档间隔分钟数（默认 5，即分钟数 % 5 == 0 的整分时刻）

`Server/appsettings.json` 另有 `ServerPort`（默认 999）与可选 `ScreenshotRetentionDays`（默认 14）。

## 关键依赖

- `Microsoft.Extensions.Hosting.WindowsServices`（PCClient）—— Worker Service 托管
- `System.Drawing.Common` / Windows Forms（PCClient）—— 通过 GDI+ 实现屏幕截图
- 两个项目均以 `win-x64` 运行时标识发布
