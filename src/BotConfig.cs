namespace ResoHelperFP;

public class BotConfig
{
    public required string DiscordToken { get; set; }
    public ulong DiscordServerId { get; set; }
    public ulong DiscordLogChannelId { get; set; }
    public List<string>? ContainersWhitelist { get; set; }
}