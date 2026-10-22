using System.Configuration;
using System.Reflection;
using Fluxer.Net;
using Fluxer.Net.Commands;

namespace ResoHelperFP;

public class FluxerInterface
{
    private const float UpdateTimeout = 5;
    private Timer? _timeout;
    private readonly Dictionary<string, Dictionary<string, SessionData>> _queuedSessionData = new();
    private FluxerClient _client;
    private readonly BotConfig _config;
    private CommandService _commands;

    public event Func<SocketMessage, Task>? CommandReceived;

    public FluxerInterface(BotConfig config)
    {
        _config = config;
        if (_config.FluxerToken == null)
        {
            throw new ConfigurationErrorsException("Failed to get Fluxer bot token from config.json");
        }
        
        _client = new FluxerClient(_config.FluxerToken);
        _commands = new CommandService();
    }

    public async Task MainAsync()
    {
        await _client.StartAsync();

        await _commands.AddModulesAsync(Assembly.GetExecutingAssembly());
        _client.Gateway.MessageCreated += SlashCommandHandler;
    }

    private void SlashCommandHandler(SocketMessage msg)
    {
        CommandReceived?.Invoke(msg);
    }

    public async Task SendMessage(string message)
    {
        _client.Gateway.Guilds.TryGetValue(_config.FluxerServerId, out var guild);
        if (guild == null)
        {
            throw new ConfigurationErrorsException("Failed to find configured server.");
        }

        guild.Channels.TryGetValue( _config.FluxerLogChannelId, out var channel);

        if (channel == null)
        {
            throw new ConfigurationErrorsException("Failed to find configured channel ID.");
        }

        await channel.SendMessageAsync(message);
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

    private void UpdateSessionInfoWrapper(object? _)
    {
        UpdateSessionInfo();
        _timeout = null;
    }

    private void UpdateSessionInfo()
    {
        var status = string.Join(" | ",
            _queuedSessionData.SelectMany(pair => pair.Value)
                .Where(info => info.Value.ActiveUserCount > 0 && info.Key != "Userspace" && info.Key != "Local")
                .OrderByDescending(info => info.Value.ActiveUserCount)
                .Select(info => $"{info.Key.Replace("[fp]", "").Trim()}: {info.Value.ActiveUserCount}"));

        Logger.Log($"Updating Status: {status}");
        _client.Gateway.SetStatus(custom: new UserCustomStatusJson
        {
            Text = status
        });
    }

    public void Dispose()
    {
        _client.Gateway.Dispose();
    }

    public Dictionary<string, Dictionary<string, SessionData>> CurrentSessions => _queuedSessionData;
}
