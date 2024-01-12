﻿using System.Configuration;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Elements.Core;
using Newtonsoft.Json;
using SkyFrost.Base;

namespace ResoHelperFP;

public class DiscordInterface
{
    private const float UpdateTimeout = 5;
    private Timer? _timeout;
    private readonly Dictionary<string, Dictionary<string, SessionData>> _queuedSessionData = new();
    private readonly DiscordSocketClient _client = new();
    private readonly BotConfig _config;

    public event Func<SocketSlashCommand, Task>? SlashCommandReceived;

    public DiscordInterface(BotConfig config)
    {
        _config = config;
    }

    public event Func<Task> Ready
    {
        add => _client.Ready += value;
        remove => _client.Ready -= value;
    }

    public async Task MainAsync()
    {
        _client.Log += Log;

        if (_config.DiscordToken == null)
        {
            throw new ConfigurationErrorsException("Failed to get discord bot token from config.json");
        }

        await _client.LoginAsync(TokenType.Bot, _config.DiscordToken);
        await _client.StartAsync();
        _client.SlashCommandExecuted += SlashCommandHandler;
        Ready += OnDiscordReady;
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        if (SlashCommandReceived == null) return;
        await SlashCommandReceived(command);
    }

    private async Task OnDiscordReady()
    {
        var guild = _client.GetGuild(_config.DiscordServerId);

        if (guild == null)
        {
            throw new ConfigurationErrorsException("Failed to find configured server.");
        }

        try
        {
            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                .WithName("sessions")
                .WithDescription("List all furpunch Resonite sessions currently running.")
                .Build());
            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                .WithName("week")
                .WithDescription("Get the current week type for furpunch Resonite sessions."
                ).Build());
            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                .WithName("contact")
                .WithDescription("Interact with headless contacts.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("requests")
                    .WithDescription("Get a list of pending contact requests to the headless.")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                ).AddOption(new SlashCommandOptionBuilder()
                    .WithName("accept")
                    .WithDescription("Accept a pending contact request to the headless.")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("username").WithDescription("User to accept")
                        .WithRequired(true)
                        .WithType(ApplicationCommandOptionType.String)
                    )
                )
                .Build());
        }
        catch (HttpException exception)
        {
            var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);
            UniLog.Error(json);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public void SendMessage(string message)
    {
        var guild = _client.GetGuild(_config.DiscordServerId);
        if (guild == null)
        {
            throw new ConfigurationErrorsException("Failed to find configured server.");
        }

        var channel = guild.TextChannels.FirstOrDefault(channel => channel.Id == _config.DiscordLogChannelId);

        if (channel == null)
        {
            throw new ConfigurationErrorsException("Failed to find configured channel ID.");
        }

        channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
    }

    public void SetSessionInfoBuffered(string hostname, Dictionary<string, SessionData> sessionInfos)
    {
        _queuedSessionData[hostname] = sessionInfos;
        if (_timeout == null)
        {
            _timeout = new Timer(async state =>
            {
                await UpdateSessionInfo();
                _timeout = null;
            }, null, TimeSpan.FromSeconds(UpdateTimeout), period: Timeout.InfiniteTimeSpan);
        }
    }

    private async Task UpdateSessionInfo()
    {
        var status = string.Join(" | ",
            _queuedSessionData.SelectMany(pair => pair.Value)
                .Where(info => info.Value.ActiveUserCount > 0)
                .OrderBy(info => info.Value.ActiveUserCount)
                .Select(info => $"{info.Key.Replace("[fp]", "").Trim()}: {info.Value.ActiveUserCount}"));

        UniLog.Log($"Updating Status: {status}");
        await _client.SetActivityAsync(new CustomStatusGame(status));
    }

    private Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }
}