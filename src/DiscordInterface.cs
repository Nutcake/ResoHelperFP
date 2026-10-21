using System.Configuration;
using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;

namespace ResoHelperFP;

public class DiscordInterface
{
    private const float UpdateTimeout = 5;
    private Timer? _timeout;
    private readonly Dictionary<string, Dictionary<string, SessionData>> _queuedSessionData = new();
    private readonly DiscordSocketClient _client = new();
    private readonly BotConfig _config;

    public event Func<SocketSlashCommand, Task>? SlashCommandReceived;
    public event Func<SocketAutocompleteInteraction, Task>? CommandAutocompleteReceived;

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
        _client.AutocompleteExecuted += AutocompleteHandler;
        Ready += OnDiscordReady;
    }

    private async Task AutocompleteHandler(SocketAutocompleteInteraction arg)
    {
        if (CommandAutocompleteReceived == null) return;
        await CommandAutocompleteReceived(arg);
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
                .WithName("restart")
                .WithDescription("Restart a given instance.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("instance")
                    .WithDescription("Instance to restart")
                    .WithRequired(false)
                    .WithType(ApplicationCommandOptionType.String)
                    .WithAutocomplete(true))
                .Build());
        }
        catch (HttpException exception)
        {
            var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);
            Logger.Error(json);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public async Task SendMessage(string message)
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

        await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
    }

    public void SetSessionDataBuffered(string hostname, Dictionary<string, SessionData> sessionData)
    {
        var needsUpdate = false;
        _queuedSessionData.TryGetValue(hostname, out var existingData);
        if (existingData == null || existingData.Count != sessionData.Count)
        {
            needsUpdate = true;
        }
        else
        {
            foreach (var session in sessionData)
            {
                existingData.TryGetValue(session.Key, out var existingSession);
                if (existingSession == null || !existingSession.Equals(session.Value))
                {
                    needsUpdate = true;
                    break;
                }
            }
        }

        if (needsUpdate)
        {
            _queuedSessionData[hostname] = sessionData;
            _timeout ??= new Timer(UpdateSessionInfoWrapper, null, TimeSpan.FromSeconds(UpdateTimeout),
                period: Timeout.InfiniteTimeSpan);
        }
    }

    private async void UpdateSessionInfoWrapper(object? _)
    {
        await UpdateSessionInfo();
        _timeout = null;
    }

    private async Task UpdateSessionInfo()
    {
        var status = string.Join(" | ",
            _queuedSessionData.SelectMany(pair => pair.Value)
                .Where(info => info.Value.ActiveUserCount > 0 && info.Key != "Userspace" && info.Key != "Local")
                .OrderByDescending(info => info.Value.ActiveUserCount)
                .Select(info => $"{info.Key.Replace("[fp]", "").Trim()}: {info.Value.ActiveUserCount}"));

        Logger.Log($"Updating Status: {status}");
        await _client.SetActivityAsync(new CustomStatusGame(status));
    }

    private Task Log(LogMessage msg)
    {
        Logger.Log(msg.ToString());
        return Task.CompletedTask;
    }

    public Dictionary<string, Dictionary<string, SessionData>> CurrentSessions => _queuedSessionData;
}
