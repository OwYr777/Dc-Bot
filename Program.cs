using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class Program
{
    private DiscordSocketClient? _client;

    // --- DEINE IDs ---
    private ulong guildId = 1488598599943327956;
    private ulong resultsChannelId = 1488610001156182197;
    private ulong highResultsChannelId = 1488610071377346721;
    private ulong pingRoleId = 1488627841053495458; 

    private Dictionary<ulong, (DateTime LastTest, string PrevRank)> _userStats = new Dictionary<ulong, (DateTime, string)>();
    private List<ulong> _queueList = new List<ulong>();
    private bool _isTestingActive = false;
    private int _testsThisSession = 0;
    private int _queueLimit = 20;
    private IUserMessage? _lastWaitlistMessage;
    private string? _lastSessionTimestamp;
    private SocketGuildUser? _currentActiveTester;

    public static Task Main(string[] args) => new Program().MainAsync();

    public async Task MainAsync()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig { 
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers | GatewayIntents.MessageContent 
        });

        _client.Log += Log; 
        _client.Ready += ReadyAsync;
        _client.SlashCommandExecuted += SlashCommandHandler;
        _client.ButtonExecuted += ButtonHandler;

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(-1);
    }

    private async Task ReadyAsync()
    {
        _ = Task.Run(async () =>
        {
            try {
                var guild = _client!.GetGuild(guildId);
                if (guild == null) return;

                // Alle Commands registrieren
                string[] commands = { "test-start", "test-stop", "next", "leave", "close" };
                foreach (var name in commands) 
                    await guild.CreateApplicationCommandAsync(new SlashCommandBuilder().WithName(name).WithDescription($"Execute {name}").Build());
                
                await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                    .WithName("stats").WithDescription("View testing history").AddOption("user", ApplicationCommandOptionType.User, "The player", true).Build());

                var tierCommand = new SlashCommandBuilder()
                    .WithName("tier").WithDescription("Set rank")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("set").WithDescription("Set player rank").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("user", ApplicationCommandOptionType.User, "The player", true)
                        .AddOption("rank", ApplicationCommandOptionType.String, "Rank (e.g., HT4)", true)
                        .AddOption("score", ApplicationCommandOptionType.String, "Match score (e.g., 3-0)", true)
                        .AddOption("previous-rank", ApplicationCommandOptionType.String, "Previous rank", false));
                
                await guild.CreateApplicationCommandAsync(tierCommand.Build());
                Console.WriteLine("✅ System Ready & Timezone Fixed.");
            } catch (Exception ex) { Console.WriteLine(ex.Message); }
        });
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        var guild = _client!.GetGuild(guildId);
        var user = command.User as SocketGuildUser;
        if (user == null || guild == null) return;

        bool isTester = user.Roles.Any(r => r.Name == "Tester" || r.Name == "Admin") || user.GuildPermissions.Administrator;

        if (command.Data.Name == "test-start") {
            if (!isTester) return;
            _isTestingActive = true; _testsThisSession = 0; _currentActiveTester = user;
            await command.DeferAsync(ephemeral: true);
            await UpdateWaitlistDisplay(command.Channel, true); 
            await command.ModifyOriginalResponseAsync(m => m.Content = "✅ Session started.");
        }
        else if (command.Data.Name == "test-stop") {
            if (!isTester) return;
            await StopSession(command.Channel);
            await command.RespondAsync("🛑 Session ended.", ephemeral: true);
        }
        else if (command.Data.Name == "next") {
            if (!isTester) return;
            if (_queueList.Count == 0) { await command.RespondAsync("❌ Queue is empty!", ephemeral: true); return; }

            var nextId = _queueList[0]; 
            _queueList.RemoveAt(0);
            
            if (_queueList.Count > 0) {
                var newFirst = guild.GetUser(_queueList[0]);
                if (newFirst != null) try { await newFirst.SendMessageAsync("🔔 **You are now #1 in the queue!** Get ready."); } catch { }
            }

            var target = guild.GetUser(nextId);
            var ticket = await guild.CreateTextChannelAsync($"ticket-{target?.Username ?? "user"}", tcp => {
                tcp.PermissionOverwrites = new List<Overwrite> {
                    new Overwrite(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
                    new Overwrite(user.Id, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow)),
                    new Overwrite(nextId, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow))
                };
            });
            await ticket.SendMessageAsync($"👋 {target?.Mention}, welcome to your test!\nTester: {user.Mention}");
            await UpdateWaitlistDisplay(command.Channel, false); 
            await command.RespondAsync($"✅ Ticket: {ticket.Mention}", ephemeral: true);
        }
        else if (command.Data.Name == "close") {
            if (command.Channel is SocketTextChannel ticketChannel && ticketChannel.Name.StartsWith("ticket-")) {
                await command.RespondAsync("🔒 Closing ticket...");
                await Task.Delay(2000);
                await ticketChannel.DeleteAsync();
            } else await command.RespondAsync("❌ Not a ticket channel.", ephemeral: true);
        }
        else if (command.Data.Name == "stats") {
            var target = command.Data.Options.First().Value as SocketUser;
            if (target != null && _userStats.TryGetValue(target.Id, out var stats)) {
                var eb = new EmbedBuilder().WithTitle($"Stats: {target.Username}").AddField("Last Test", stats.LastTest.ToString("dd.MM.yyyy")).AddField("Rank", stats.PrevRank).WithColor(Color.Blue).Build();
                await command.RespondAsync(embed: eb);
            } else await command.RespondAsync("No data found.", ephemeral: true);
        }
        else if (command.Data.Name == "tier") { await HandleTierSet(command, guild); }
        else if (command.Data.Name == "leave") {
             if (_queueList.Contains(command.User.Id)) {
                _queueList.Remove(command.User.Id); 
                await UpdateWaitlistDisplay(command.Channel, false);
                await command.RespondAsync("👋 Left the queue.", ephemeral: true);
            } else await command.RespondAsync("You are not in the queue.", ephemeral: true);
        }
    }

    private async Task UpdateWaitlistDisplay(ISocketMessageChannel channel, bool shouldPing)
    {
        if (_lastWaitlistMessage != null) try { await _lastWaitlistMessage.DeleteAsync(); } catch { }

        if (!_isTestingActive) {
            var eb = new EmbedBuilder()
                .WithTitle("[1.21+] Minecraft Sword PvP Community")
                .WithDescription("**No Testers Online**\n\nNo testers for your region are available at this time.\nYou will be pinged when a tester is available.\nCheck back later!\n\n**Last testing session:** " + (_lastSessionTimestamp ?? "None"))
                .WithColor(Color.Red).Build();
            _lastWaitlistMessage = await channel.SendMessageAsync(embed: eb);
        } else {
            bool isFull = _queueList.Count >= _queueLimit;
            string q = _queueList.Count == 0 ? "Empty" : string.Join("\n", _queueList.Select((id, i) => $"{i + 1}. <@{id}>"));
            
            var eb = new EmbedBuilder()
                .WithTitle("Tester(s) Available!")
                .WithDescription(isFull ? "⚠️ **QUEUE FULL** (Limit: 20)" : "⌚ The queue updates every 1 minute.\nUse `/leave` if you wish to be removed from the waitlist or queue.")
                .AddField("Queue:", q)
                .AddField("Session Stats:", $"{_testsThisSession} Tests done")
                .AddField("Active Testers:", _currentActiveTester?.Mention ?? "None") 
                .WithColor(isFull ? Color.Red : new Color(255, 127, 0)).Build(); 

            var cb = new ComponentBuilder()
                .WithButton("Join Queue", "join_queue", ButtonStyle.Primary, disabled: isFull)
                .WithButton("Leave Queue", "leave_queue", ButtonStyle.Danger).Build();
            
            _lastWaitlistMessage = await channel.SendMessageAsync(shouldPing ? $"<@&{pingRoleId}>" : null, embed: eb, components: cb);
        }
    }

    private async Task StopSession(ISocketMessageChannel channel) { 
        _isTestingActive = false; 
        _queueList.Clear(); 
        
        // ZEIT-FIX: Deutschland Zeit berechnen
        var germanTime = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.Now, "Central European Standard Time");
        _lastSessionTimestamp = germanTime.ToString("dd. MMMM yyyy HH:mm"); 
        
        await UpdateWaitlistDisplay(channel, false); 
    }

    private async Task HandleTierSet(SocketSlashCommand command, SocketGuild guild)
    {
        var sub = command.Data.Options.First();
        var targetUser = guild.GetUser(((SocketUser)sub.Options.First(x => x.Name == "user").Value).Id);
        var rank = sub.Options.First(x => x.Name == "rank").Value.ToString()?.ToUpper() ?? "";
        var score = sub.Options.First(x => x.Name == "score").Value.ToString() ?? "N/A";
        string prev = sub.Options.Any(x => x.Name == "previous-rank") ? sub.Options.First(x => x.Name == "previous-rank").Value.ToString()! : "Unranked";

        if (targetUser == null) return;
        _userStats[targetUser.Id] = (DateTime.Now, rank);
        _testsThisSession++;

        bool isHigh = rank.StartsWith("HT");
        var resChannel = guild.GetTextChannel(isHigh ? highResultsChannelId : resultsChannelId);
        
        await targetUser.RemoveRolesAsync(targetUser.Roles.Where(r => r.Name.StartsWith("LT") || r.Name.StartsWith("HT")));
        var role = guild.Roles.FirstOrDefault(r => r.Name.Equals(rank, StringComparison.OrdinalIgnoreCase));
        if (role != null) await targetUser.AddRoleAsync(role);

        if (resChannel != null) {
            var eb = new EmbedBuilder()
                .WithAuthor($"{targetUser.Username}'s Test Results 🏆", targetUser.GetAvatarUrl() ?? targetUser.GetDefaultAvatarUrl())
                .WithColor(isHigh ? Color.Red : Color.Blue)
                .WithDescription($"**Tester:**\n<@{command.User.Id}>\n\n**Score:**\n{score}\n\n**Region:**\nEU\n\n**Username:**\n{targetUser.Username}\n\n**Previous Rank:**\n{prev}\n\n**Rank Earned:**\n{(isHigh ? "High Tier " : "Low Tier ")}{rank.Replace("HT","").Replace("LT","")}")
                .WithThumbnailUrl(targetUser.GetAvatarUrl() ?? targetUser.GetDefaultAvatarUrl())
                .Build();

            var msg = await resChannel.SendMessageAsync($"<@{targetUser.Id}>", embed: eb);
            await msg.AddReactionsAsync(new Emoji[] { new Emoji("👑"), new Emoji("🥳"), new Emoji("😱"), new Emoji("😭"), new Emoji("😂"), new Emoji("💀") });
        }
        await command.RespondAsync("✅ Result posted!", ephemeral: true);
    }

    private async Task ButtonHandler(SocketMessageComponent component)
    {
        if (component.Data.CustomId == "join_queue") {
            if (!_queueList.Contains(component.User.Id)) {
                _queueList.Add(component.User.Id); await component.RespondAsync("✅ Joined the queue!", ephemeral: true);
                await UpdateWaitlistDisplay(component.Channel, false);
            } else await component.RespondAsync("You are already in the queue.", ephemeral: true);
        } else if (component.Data.CustomId == "leave_queue") {
            if (_queueList.Contains(component.User.Id)) {
                _queueList.Remove(component.User.Id); await component.RespondAsync("👋 Left the queue.", ephemeral: true);
                await UpdateWaitlistDisplay(component.Channel, false);
            } else await component.RespondAsync("You are not in the queue.", ephemeral: true);
        }
    }

    private Task Log(LogMessage msg) { Console.WriteLine(msg.ToString()); return Task.CompletedTask; }
}