using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 启动时确保已加入 Windows 启动组（注册表 Run 键）
Utility.EnsureInStartup();

var builder = Host.CreateApplicationBuilder(args);
// 关闭控制台日志，避免任何控制台输出（WinExe 下无窗口，输出到 EventLog/文件）
builder.Logging.ClearProviders();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
