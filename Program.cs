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

    // Datenspeicher
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
        _client.ButtonExecuted += ButtonHandler; // Fehler behoben: Methode ist jetzt definiert

        await _client.LoginAsync(TokenType.Bot, "DISCORD_TOKEN");
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

                // Commands registrieren
                string[] simpleCmds = { "test-start", "test-stop", "next", "close", "leave" };
                foreach (var name in simpleCmds) 
                    await guild.CreateApplicationCommandAsync(new SlashCommandBuilder().WithName(name).WithDescription(name).Build());
                
                await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                    .WithName("stats").WithDescription("Zeigt Historie").AddOption("nutzer", ApplicationCommandOptionType.User, "Spieler", true).Build());

                var tierCommand = new SlashCommandBuilder()
                    .WithName("tier").WithDescription("Rang setzen")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("set").WithDescription("Setzt Rang").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("nutzer", ApplicationCommandOptionType.User, "Spieler", true)
                        .AddOption("rang", ApplicationCommandOptionType.String, "Rang (z.B. HT4)", true)
                        .AddOption("score", ApplicationCommandOptionType.String, "Ergebnis (z.B. 3-0)", true)
                        .AddOption("vorheriger-rang", ApplicationCommandOptionType.String, "Vorheriger Rang", false));
                
                await guild.CreateApplicationCommandAsync(tierCommand.Build());
                Console.WriteLine("✅ Bot bereit.");
            } catch (Exception ex) { Console.WriteLine(ex.Message); }
        });
        await Task.CompletedTask;
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
            await command.ModifyOriginalResponseAsync(m => m.Content = "✅ Session gestartet.");
        }
        else if (command.Data.Name == "test-stop") {
            if (!isTester) return;
            await StopSession(command.Channel);
            await command.RespondAsync("🛑 Session beendet.", ephemeral: true);
        }
        else if (command.Data.Name == "next") {
            if (!isTester) return;
            if (_queueList.Count == 0) { await command.RespondAsync("❌ Queue leer!", ephemeral: true); return; }

            var nextId = _queueList[0]; _queueList.RemoveAt(0);
            
            // DM an neue Nr. 1
            if (_queueList.Count > 0) {
                var newFirst = guild.GetUser(_queueList[0]);
                if (newFirst != null) try { await newFirst.SendMessageAsync("🔔 **Du bist jetzt Platz 1!**"); } catch { }
            }

            var target = guild.GetUser(nextId);
            var ticket = await guild.CreateTextChannelAsync($"ticket-{target?.Username ?? "user"}", tcp => {
                tcp.PermissionOverwrites = new List<Overwrite> {
                    new Overwrite(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
                    new Overwrite(user.Id, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow)),
                    new Overwrite(nextId, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow))
                };
            });
            await ticket.SendMessageAsync($"👋 {target?.Mention}, willkommen zum Test!");
            await UpdateWaitlistDisplay(command.Channel, false); 
            await command.RespondAsync($"✅ Ticket: {ticket.Mention}", ephemeral: true);
        }
        else if (command.Data.Name == "stats") {
            var target = command.Data.Options.First().Value as SocketUser;
            if (target != null && _userStats.TryGetValue(target.Id, out var stats)) {
                await command.RespondAsync(embed: new EmbedBuilder().WithTitle($"Stats: {target.Username}").AddField("Letzter Test", stats.LastTest).AddField("Rang", stats.PrevRank).Build());
            } else await command.RespondAsync("Keine Daten.", ephemeral: true);
        }
        else if (command.Data.Name == "tier") { await HandleTierSet(command, guild); }
    }

    private async Task HandleTierSet(SocketSlashCommand command, SocketGuild guild)
    {
        var sub = command.Data.Options.First();
        var targetUser = guild.GetUser(((SocketUser)sub.Options.First(x => x.Name == "nutzer").Value).Id);
        var rank = sub.Options.First(x => x.Name == "rang").Value.ToString()?.ToUpper() ?? "";
        var score = sub.Options.First(x => x.Name == "score").Value.ToString() ?? "";

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
                .WithAuthor($"{targetUser.Username}'s Results 🏆", targetUser.GetAvatarUrl())
                .WithDescription($"**Tester:** <@{command.User.Id}>\n**Score:** {score}\n**Rank:** {rank}")
                .WithColor(isHigh ? Color.Red : Color.Blue)
                .WithThumbnailUrl(targetUser.GetAvatarUrl()).Build();

            var msg = await resChannel.SendMessageAsync($"<@{targetUser.Id}>", embed: eb);
            await msg.AddReactionsAsync(new IEmote[] { new Emoji("👑"), new Emoji("🥳"), new Emoji("😱"), new Emoji("😭"), new Emoji("😂"), new Emoji("💀") });
        }
        await command.RespondAsync("✅ Ergebnis gepostet!", ephemeral: true);
    }

    private async Task UpdateWaitlistDisplay(ISocketMessageChannel channel, bool shouldPing)
    {
        if (_lastWaitlistMessage != null) try { await _lastWaitlistMessage.DeleteAsync(); } catch { }

        if (!_isTestingActive) {
            var eb = new EmbedBuilder().WithTitle("No Testers Online").WithColor(Color.Red).Build();
            _lastWaitlistMessage = await channel.SendMessageAsync(embed: eb);
        } else {
            string q = _queueList.Count == 0 ? "Empty" : string.Join("\n", _queueList.Select((id, i) => $"{i+1}. <@{id}>"));
            var eb = new EmbedBuilder().WithTitle("Tester Available!").AddField("Queue", q).WithColor(Color.Orange).Build();
            var cb = new ComponentBuilder().WithButton("Join Queue", "join_queue", disabled: _queueList.Count >= _queueLimit).Build();
            _lastWaitlistMessage = await channel.SendMessageAsync(shouldPing ? $"<@&{pingRoleId}>" : null, embed: eb, components: cb);
        }
    }

    private async Task ButtonHandler(SocketMessageComponent component)
    {
        if (component.Data.CustomId == "join_queue") {
            if (!_queueList.Contains(component.User.Id)) {
                _queueList.Add(component.User.Id); await component.RespondAsync("✅ Beigetreten!", ephemeral: true);
                await UpdateWaitlistDisplay(component.Channel, false);
            } else await component.RespondAsync("Schon drin.", ephemeral: true);
        }
    }

    private async Task StopSession(ISocketMessageChannel channel) { _isTestingActive = false; _queueList.Clear(); await UpdateWaitlistDisplay(channel, false); }
    private Task Log(LogMessage msg) { Console.WriteLine(msg.ToString()); return Task.CompletedTask; }
}