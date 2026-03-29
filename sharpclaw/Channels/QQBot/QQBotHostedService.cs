using Luolan.QQBot;
using Microsoft.Extensions.Hosting;
using sharpclaw.Core;
using sharpclaw.UI;

namespace sharpclaw.Channels.QQBot;

/// <summary>
/// QQ Bot 托管服务。作为 ASP.NET Core IHostedService 嵌入 Web 宿主，
/// 随 Web 应用一同启动和停止。
/// </summary>
public sealed class QQBotHostedService : IHostedService
{
    private readonly AgentBootstrap.BootstrapResult _bootstrap;
    private QQBotClient? _bot;
    private QQBotChatIO? _chatIO;
    private Task? _agentTask;
    private CancellationTokenSource? _cts;

    public QQBotHostedService(AgentBootstrap.BootstrapResult bootstrap)
    {
        _bootstrap = bootstrap;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var qqConfig = _bootstrap.Config.Channels.QQBot;

        _bot = new QQBotClientBuilder()
            .WithAppId(qqConfig.AppId)
            .WithClientSecret(qqConfig.ClientSecret)
            .WithIntents(Intents.Default | Intents.GroupAtMessages | Intents.C2CMessages)
            .UseSandbox(qqConfig.Sandbox)
            .Build();

        _chatIO = new QQBotChatIO(_bot);
        _cts = new CancellationTokenSource();

        // 注册消息事件
        _bot.OnAtMessageCreate += async e =>
        {
            Console.WriteLine($"[QQBot] 频道消息: {e.Message.Content}");
            await _chatIO.EnqueueMessageAsync(e.Message, MessageSource.Channel);
        };

        _bot.OnGroupAtMessageCreate += async e =>
        {
            Console.WriteLine($"[QQBot] 群消息: {e.Message.Content}");
            await _chatIO.EnqueueMessageAsync(e.Message, MessageSource.Group);
        };

        _bot.OnC2CMessageCreate += async e =>
        {
            Console.WriteLine($"[QQBot] 私聊消息: {e.Message.Content}");
            await _chatIO.EnqueueMessageAsync(e.Message, MessageSource.C2C);
        };

        _bot.OnReady += e =>
        {
            Console.WriteLine($"[QQBot] 机器人已就绪: {_bot.CurrentUser?.Username ?? "unknown"}");
            return Task.CompletedTask;
        };

        // 创建 MainAgent
        var agent = new Agents.MainAgent(
            _bootstrap.Config,
            _bootstrap.MemoryStore,
            _bootstrap.CommandSkills,
            chatIO: _chatIO,
            _bootstrap.AgentContext);

        // 启动 Bot 连接
        await _bot.StartAsync(_cts.Token);
        Console.WriteLine("[QQBot] QQ Bot 已启动");

        // 在后台运行 Agent 对话循环
        _agentTask = Task.Run(() => agent.RunAsync(_cts.Token));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[QQBot] 正在停止 QQ Bot...");

        _cts?.Cancel();
        _chatIO?.RequestStop();

        if (_bot is not null)
            await _bot.StopAsync();

        if (_agentTask is not null)
        {
            try { await _agentTask; }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();

        Console.WriteLine("[QQBot] QQ Bot 已停止");
    }
}
