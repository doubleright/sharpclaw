using sharpclaw.Channels.Tui;
using sharpclaw.Core;
using sharpclaw.UI;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

#if DEBUG 
// 调试模式下如果没有提供命令参数，默认启动 Web 宿主，方便调试和开发。
if (command.Length == 0)
{
    Console.WriteLine("[Debug] 未提供命令参数，默认启动 Web 宿主。");
    command = "web";
}
else 
    Console.WriteLine($"[Debug] 启动参数: {string.Join(' ', args)} ");
#endif

switch (command)
{
    case "" or "web":
        KeyStore.PasswordPrompt = ConsolePasswordPrompt;
        await RunWebAsync(args);
        return;

    case "cli":
        await RunCliClientAsync(args);
        return;

    case "tui":
        RunTui(args);
        return;

    case "config":
        RunTui(args);
        return;

    case "help" or "--help" or "-h":
        PrintHelp();
        return;

    default:
        PrintHelp();
        return;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Sharpclaw - AI 智能助手

        用法: sharpclaw <命令> [选项]

        命令:
          (默认)                            启动 Web 宿主（含 WebSocket + 可选 QQBot）
          web [--address ADDR] [--port N]   启动 Web 宿主（同上）
          cli [--address ADDR] [--port N]   以 CLI 终端连接到 Web 宿主
          tui                               启动 TUI 终端界面（本地模式）
          config                            打开配置界面
          help                              显示帮助信息
        """);
}

/// <summary>
/// 启动 Web 宿主：初始化 AgentBootstrap，启动 ASP.NET Core WebApp。
/// 如果 QQBot 配置启用，会自动注册为托管服务随 Web 一同启动。
/// </summary>
static async Task RunWebAsync(string[] args)
{
    // ── 配置检测 ──
    if (!SharpclawConfig.Exists())
    {
        Console.WriteLine("[Error] 配置文件不存在，请先运行 'sharpclaw config' 完成配置。");
        return;
    }

    var bootstrap = AgentBootstrap.Initialize();

    await sharpclaw.Channels.Web.WebServer.RunAsync(args, bootstrap);
}

/// <summary>
/// CLI WebSocket 客户端模式：连接到正在运行的 Web 宿主。
/// </summary>
static async Task RunCliClientAsync(string[] args)
{
    // 解析连接参数
    var address = "localhost";
    var port = 5000;

    // 尝试从配置文件读取 Web 地址和端口
    if (SharpclawConfig.Exists())
    {
        try
        {
            KeyStore.PasswordPrompt = _ => null; // CLI 客户端不需要交互式密码
            var config = SharpclawConfig.Load();
            address = config.Channels.Web.ListenAddress;
            port = config.Channels.Web.Port;
        }
        catch { }
    }

    // 命令行参数覆盖
    var addrIdx = Array.IndexOf(args, "--address");
    if (addrIdx >= 0 && addrIdx + 1 < args.Length)
        address = args[addrIdx + 1];
    var portIdx = Array.IndexOf(args, "--port");
    if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var p))
        port = p;

    var serverUrl = $"ws://{address}:{port}/ws";

    using var client = new sharpclaw.Channels.Cli.CliClient(serverUrl);
    await client.RunAsync();
}

static void RunTui(string[] args)
{
    using var app = Application.Create().Init();

    // TUI 模式下通过对话框提示输入密码
    KeyStore.PasswordPrompt = prompt =>
    {
        string? result = null;
        var dlg = new Dialog { Title = "Keychain 解锁", Width = 50, Height = 15 };
        var label = new Label { Text = prompt, X = 1, Y = 1, Width = Dim.Fill(1) };
        var field = new TextField { X = 1, Y = 3, Width = Dim.Fill(1), Secret = true };
        dlg.Add(label, field);

        var ok = new Button { Text = "确定", IsDefault = true };
        ok.Accepting += (_, e) => { result = field.Text; dlg.RequestStop(); e.Handled = true; };
        var cancel = new Button { Text = "跳过" };
        cancel.Accepting += (_, e) => { dlg.RequestStop(); e.Handled = true; };
        dlg.AddButton(ok);
        dlg.AddButton(cancel);

        app.Run(dlg);
        dlg.Dispose();
        return result;
    };

    // ── 配置检测 ──
    if (args.Contains("config") || !SharpclawConfig.Exists())
    {
        var configDialog = new ConfigDialog();
        if (SharpclawConfig.Exists())
            configDialog.LoadFrom(SharpclawConfig.Load());
        app.Run(configDialog);
        configDialog.Dispose();

        if (!configDialog.Saved || args.Contains("config"))
            return;
    }

    // ── 初始化 ──
    var bootstrap = AgentBootstrap.Initialize();

    if (bootstrap.MemoryStore is null)
        AppLogger.Log("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

    // ── 创建 ChatWindow 并启动主智能体 ──
    var chatWindow = new ChatWindow(bootstrap.Config.Channels.Tui);
    var agent = new sharpclaw.Agents.MainAgent(
        bootstrap.Config, bootstrap.MemoryStore, bootstrap.CommandSkills, chatIO: chatWindow, bootstrap.AgentContext);

    // 在后台线程启动智能体循环
    _ = Task.Run(() => agent.RunAsync());

    // 运行 Terminal.Gui 主循环（阻塞直到退出）
    app.Run(chatWindow);
    chatWindow.Dispose();

    // 清理所有后台任务
    bootstrap.TaskManager.Dispose();
}

static string? ConsolePasswordPrompt(string prompt)
{
    Console.Write($"[KeyStore] {prompt} (直接回车跳过): ");
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Remove(sb.Length - 1, 1);
        else if (key.KeyChar >= ' ') sb.Append(key.KeyChar);
    }
    var result = sb.ToString();
    return string.IsNullOrEmpty(result) ? null : result;
}
