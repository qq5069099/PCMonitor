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

### PCClient（`PCClient/`）—— .NET Worker Service（BackgroundService）

- 作为 `?role=pc` 连接 Server WebSocket，以本机机器名注册
- 截图循环：当 `_streaming` 为 true 时，按 Server 要求的帧率/画质截图并以二进制帧发送（8字节客户端 UTC 时间戳 + JPEG 数据）
- **ScreenCapture**（静态类）：支持多显示器 —— 通过 `Graphics.CopyFromScreen` 捕获所有屏幕，等比缩放后水平拼接为单张 JPEG。在拼接图上绘制黄色圆圈 + 十字准星光标覆盖层及空闲时长标签。在远程控制模式下（收到控制指令后 60 秒内）改绘标准箭头光标。空闲检测采用 60 秒滑动窗口 + 反作弊机制（要求 60 秒内至少有 6 次不同的真实移动，每次位移超过 30px）
- **NativeSessionHelper**（静态类）：P/Invoke 封装 —— `SendInput` 实现鼠标/键盘注入（含多显示器虚拟桌面坐标映射）、`ClipCursor` 实现鼠标锁定、`LockWorkStation` 锁屏、通过 `shutdown.exe` 实现关机/重启
- 启动时通过 `Utility.EnsureInStartup()` 在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册自启动

### 核心数据流

```
PCClient(们) ──WebSocket (二进制 JPEG 帧)──▶ Server ◀──WebSocket──▶ 浏览器 viewer(们)
                ◀──WebSocket (JSON 指令)──             ──JSON 指令──▶
```

### 共享工具类（`Utility.cs`，两个项目中各自复制一份）

AES-256-CBC 加解密、MD5 哈希、注册表自启动注册。两份代码完全一致，有意不提取为共享库。

### 配置文件（`PCClient/appsettings.json`）

- `ServerUrl` —— WebSocket 服务器地址
- `EmployeeUrl` —— 空闲告警 API 地址（向 `/api/Plan/item/add` 发送 POST 请求）
- `WorkPeriods` —— 工作时间段数组（如 `"8:40~12:00"`），仅在此时间段内发送空闲告警

## 关键依赖

- `Microsoft.Extensions.Hosting.WindowsServices`（PCClient）—— Worker Service 托管
- `System.Drawing.Common` / Windows Forms（PCClient）—— 通过 GDI+ 实现屏幕截图
- 两个项目均以 `win-x64` 运行时标识发布
