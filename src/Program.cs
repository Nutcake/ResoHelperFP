using System.Globalization;
using System.Runtime.InteropServices;
using Discord;
using Discord.WebSocket;
using Docker.DotNet;
using Docker.DotNet.Models;
using Newtonsoft.Json;
using SocketMessage = Fluxer.Net.SocketMessage;

namespace ResoHelperFP;

internal static class Program
{
	private static async Task<int> Main(string[] _)
	{
		var resoHelperFp = new ResoHelperFp();
		await resoHelperFp.MainLoop();
		return 0;
	}
}

internal class ResoHelperFp
{
	private bool _running = true;
	private DiscordInterface? _discordInterface;
	private FluxerInterface? _fluxerInterface;
	private SessionDataReceiver? _dataReceiver;
	private DockerClient? _dockerClient;
	private BotConfig? _config;

	public async Task MainLoop()
	{
		Console.CancelKeyPress += OnCancel;

		_config = JsonConvert.DeserializeObject<BotConfig>(await File.ReadAllTextAsync("config.json"));

		if (_config == null)
		{
			throw new System.Configuration.ConfigurationErrorsException("Failed to load config.json");
		}

		_discordInterface = new DiscordInterface(_config);
		_fluxerInterface = new FluxerInterface(_config);
		_dockerClient = CreateDockerClient();
		_dataReceiver = new SessionDataReceiver();
		try
		{
			var ready = false;
			_discordInterface.Ready += () =>
			{
				ready = true;
				return Task.FromResult(0);
			};
			await _discordInterface.MainAsync();
			await _fluxerInterface.MainAsync();

			while (!ready)
			{
				await Task.Delay(500);
			}

			_discordInterface.SlashCommandReceived += HandleSlashCommands;
			_discordInterface.CommandAutocompleteReceived += HandleAutocomplete;
			_fluxerInterface.CommandReceived += HandleFluxerCommand;
			_dataReceiver.SessionsUpdated += _discordInterface.SetSessionDataBuffered;
			_dataReceiver.SessionsUpdated += _fluxerInterface.SetSessionDataBuffered;

			_ = Task.Run(async () =>
			{
				while (_running)
				{
					try
					{
						await _dataReceiver.HandleConnection();
					}
					catch (Exception e)
					{
						Logger.Error("Failed to receive status message: " + e.Message);
					}
				}
			});
			while (_running)
			{
				await Task.Delay(5000);
			}
		}
		catch (Exception e)
		{
			Logger.Error($"Bot died: {e.Message}");
			throw;
		}
	}

	private async Task HandleAutocomplete(SocketAutocompleteInteraction interaction)
	{
		if (_config == null) return;
		var commandName = interaction.Data.CommandName;
		var currentText = (interaction.Data.Current.Value.ToString() ?? string.Empty).ToLower();

		switch (commandName)
		{
			case "restart":
			{
				if (_config.ContainersWhitelist == null) break;
				List<string> options = ["All", .. _config.ContainersWhitelist];
				var items =
					options
						.Where(item => item.ToLower().StartsWith(currentText.ToLower()))
						.Take(25)
						.Select(x => new AutocompleteResult($"{x}", x))
						.ToList();

				await interaction.RespondAsync(items);
				break;
			}
		}
	}

	private async Task HandleFluxerCommand(SocketMessage command)
	{
		if (_fluxerInterface == null) return;
		switch (command)
		{
			case not null when command.Content.StartsWith("/sessions"):
			{
				var sessions = _fluxerInterface.CurrentSessions;
				var response = HandleSessionsCommand(sessions);
				await _fluxerInterface.SendMessage(response);
				break;
			}
			case not null when command.Content.StartsWith("/week"):
			{
				var response = HandleWeekCommand();
				await _fluxerInterface.SendMessage(response);
				break;
			}
			case not null when command.Content.StartsWith("/restart"):
			{
				var session = command.Content.Split(" ").GetValue(1) as string;
				var response = await HandleRestartCommand(session);
				if (response != null)
				{
					await _fluxerInterface.SendMessage(response);
				}

				break;
			}
		}
	}

	private string HandleSessionsCommand(Dictionary<string, Dictionary<string, SessionData>> sessions)
	{
		string response;
		if (!sessions.Any())
		{
			response = "There are currently no sessions running.";
		}
		else
		{
			response = string.Join("\n",
					sessions.Select(pair =>
						$"**{pair.Key}**\n{string.Join("\n", pair.Value
							.Where(info => info.Key != "Userspace" && info.Key != "Local")
							.Select(valuePair => $"- {valuePair.Key.Replace("[fp]", "").Trim()}: {valuePair.Value.ActiveUserCount}" + (valuePair.Value.Hidden ? " [H]" : "")))}\n"))
				.Trim();
		}

		response += "\n\nSessions marked with [H] are hidden and require an invite to join.\n" +
		            "Send the `/requestInvite` command to the respective headless user **inside Resonite** to get an invite.\n" +
		            "You can add a session index to the command to get an invite to a specific session, starting with 0.\n" +
		            "For example, `/requestInvite 0` will get you an invite to the first session on that headless.";

		return response;
	}

	private string HandleWeekCommand()
	{
		var now = DateTime.Now;
		var week = GetIso8601WeekOfYear(now);
		var sessionType = week % 2 == 1 ? "Movie Night" : "Funny Friday";
		string response;
		if (now.DayOfWeek < DayOfWeek.Friday)
		{
			response = $"The coming weekend is **{sessionType}**!";
		}
		else
		{
			var offweekType = week % 2 == 0 ? "Movie Night" : "Funny Friday";
			response =
				$"The current weekend is **{sessionType}**! Next weekend will be {offweekType}";
		}

		return response;
	}

	private async Task<string?> HandleRestartCommand(string? instance)
	{
		if (_config?.ContainersWhitelist == null || _config.ContainersWhitelist.Count == 0)
		{
			return "No containers configured for restart!";
		}

		if (instance == null)
		{
			return $"Please specify which instance to restart:\n{string.Join("\n", _config.ContainersWhitelist)}"
				.Trim();
		}


		IList<ContainerListResponse> instances;
		if (_dockerClient == null) return null;
		try
		{
			instances = await _dockerClient.Containers.ListContainersAsync(
				new ContainersListParameters
				{
					All = true
				});
		}
		catch (TimeoutException)
		{
			return "Unable to communicate with Docker API";
		}

		// Restart all instances defined in the whitelist
		if (instance.ToLower().Equals("all"))
		{
			List<string> restartTasks = [];
			foreach (var containerName in _config?.ContainersWhitelist ?? [])
			{
				var container = instances.FirstOrDefault(cont => string.Join(", ", cont.Names).TrimStart('/') == containerName);
				if (container == null)
				{
					Logger.Warning($"Could not find instance associated with whitelist container {containerName}, skipping restart");
					continue;
				}

				RestartInstance(container.ID, containerName);
				restartTasks.Add(containerName);
			}

			var instanceList = string.Join("\n", restartTasks);
			return $"Restarting all {restartTasks.Count} instances:\n{instanceList}\n\nPlease allow up to five minutes before yelling at your local server administrator.";
		}

		// Restart only the specified instances
		{
			var container = instances.FirstOrDefault(cont => string.Join(", ", cont.Names).TrimStart('/') == instance);
			if (container == null)
			{
				return $"Instance '{instance}' does not exist.";
			}

			RestartInstance(container.ID, instance);
			return $"Instance '{instance}' restarting, please allow up to five minutes before yelling at your local server administrator.";
		}
	}

	private async Task HandleSlashCommands(SocketSlashCommand command)
	{
		switch (command.Data.Name)
		{
			case "sessions":
			{
				var sessions = _discordInterface?.CurrentSessions ??
				               new Dictionary<string, Dictionary<string, SessionData>>();
				var response = HandleSessionsCommand(sessions);
				await command.RespondAsync(response, ephemeral: true);
				break;
			}
			case "week":
			{
				var response = HandleWeekCommand();
				await command.RespondAsync(response);
				break;
			}
			case "restart":
			{
				var opt = command.Data.Options.FirstOrDefault();
				var instance = opt?.Value.ToString();
				var response = await HandleRestartCommand(instance);
				if (response != null)
				{
					await command.RespondAsync(response);
				}
				break;
			}
			default:
			{
				return;
			}
		}
	}

	private async void RestartInstance(string containerId, string instanceName)
	{
		if (_dockerClient == null) return;

		try
		{
			await _dockerClient.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters());
		}
		catch (Exception ex)
		{
			Logger.Error($"Failed to restart container {instanceName}. Exception: {ex}");
			await (_discordInterface?.SendMessage($"Container restart failed: {ex.Message}") ?? Task.CompletedTask);
			await (_fluxerInterface?.SendMessage($"Container restart failed: {ex.Message}") ?? Task.CompletedTask);
		}

		await (_discordInterface?.SendMessage($"Container '{instanceName}' restarted successfully.") ?? Task.CompletedTask);
		await (_fluxerInterface?.SendMessage($"Container '{instanceName}' restarted successfully.") ?? Task.CompletedTask);

	}

	static DockerClient CreateDockerClient()
	{
		return new DockerClientConfiguration(new Uri(GetClientUri())).CreateClient();

		string GetClientUri()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				return "npipe://./pipe/docker_engine";
			}

			var podmanPath = $"/run/user/{geteuid()}/podman/podman.sock";
			return File.Exists(podmanPath) ? $"unix:{podmanPath}" : "unix:/var/run/docker.sock";
		}

		[DllImport("libc")]
		static extern uint geteuid();
	}

	private static int GetIso8601WeekOfYear(DateTime time)
	{
		// Seriously cheat.  If its Monday, Tuesday or Wednesday, then it'll 
		// be the same week# as whatever Thursday, Friday or Saturday are,
		// and we always get those right
		var day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(time);
		if (day is >= DayOfWeek.Monday and <= DayOfWeek.Wednesday)
		{
			time = time.AddDays(3);
		}

		// Return the week of our adjusted day
		return CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(time, CalendarWeekRule.FirstFourDayWeek,
			DayOfWeek.Monday);
	}

	private void OnCancel(object? sender, ConsoleCancelEventArgs args)
	{
		_running = false;
		_dataReceiver?.Dispose();
		_discordInterface?.Dispose();
		_fluxerInterface?.Dispose();
	}
}