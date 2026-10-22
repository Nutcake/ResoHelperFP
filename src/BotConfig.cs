namespace ResoHelperFP;

public class BotConfig
{
	public required string DiscordToken { get; set; }
	public ulong DiscordServerId { get; set; }
	public ulong DiscordLogChannelId { get; set; }

	public required string FluxerToken { get; set; }
	public ulong FluxerServerId { get; set; }
	public ulong FluxerLogChannelId { get; set; }

	public List<string>? ContainersWhitelist { get; set; }
}