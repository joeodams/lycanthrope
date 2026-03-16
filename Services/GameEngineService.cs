#nullable enable

using System.Globalization;
using lycanthrope.Interfaces;
using lycanthrope.Models;
using StackExchange.Redis;
using Role = lycanthrope.Models.Role;

namespace lycanthrope.Services;

public class GameEngineService : IGameEngineService
{
    private const int MinimumPlayersToStart = 4;
    private const string BotNamePrefix = "Bot";
    private const int ChatMessageLimit = 40;
    private const int MaxChatMessageLength = 280;
    private readonly IDatabase _db;
    private readonly ILobbyNotificationService _notifications;

    public GameEngineService(IDatabase database, ILobbyNotificationService notifications)
    {
        _db = database;
        _notifications = notifications;
    }

    public async Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player player)
    {
        var meta = await LoadLobbyMeta(lobbyId);
        var playerKey = PKey(player.Id);
        var existingHash = await _db.HashGetAllAsync(playerKey);
        var playerExists = existingHash.Length > 0;

        if (!playerExists && meta.Phase != Phase.Setup)
        {
            throw new InvalidOperationException("This lobby is already in progress.");
        }

        var joinedAtUtc =
            ReadOptionalDateTimeOffset(existingHash, "JoinedAtUtc") ?? player.JoinedAtUtc;
        var ready = ReadOptionalBoolean(existingHash, "Ready") ?? player.Ready;
        var alive = ReadOptionalBoolean(existingHash, "Alive") ?? true;
        var isBot = ReadOptionalBoolean(existingHash, "IsBot") ?? player.IsBot;
        var role = ReadOptionalEnum<Role>(existingHash, "Role") ?? Role.Villager;

        await _db.HashSetAsync(
            playerKey,
            [
                new("Id", player.Id.ToString()),
                new("Name", player.Name),
                new("Ready", RedisBool.FromBool(ready)),
                new("Role", role.ToString()),
                new("Alive", RedisBool.FromBool(alive)),
                new("IsBot", RedisBool.FromBool(isBot)),
                new("Lobby", lobbyId.ToString()),
                new("JoinedAtUtc", joinedAtUtc.UtcDateTime.ToString("O")),
            ]
        );

        await _db.SetAddAsync(LSet(lobbyId), playerKey);
        var hostPlayerId = SelectPreferredHostPlayerId(
            await LoadPlayersAsync(lobbyId),
            meta.HostPlayerId
        );

        await SaveLobbyMeta(
            lobbyId,
            meta.Phase,
            meta.Day,
            meta.Winner,
            hostPlayerId,
            meta.LatestEvent
        );

        if (!playerExists)
        {
            await AppendSystemMessageAsync(lobbyId, $"{player.Name} joined the lobby.");
        }

        var lobby = await GetLobbyByIdAsync(lobbyId);
        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
        return lobby;
    }

    public async Task FillSeatsAsync(Guid lobbyId, Guid requestedByPlayerId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        EnsureHost(lobby, requestedByPlayerId);

        if (lobby.Phase != Phase.Setup)
        {
            throw new InvalidOperationException("Seats can only be filled before the game starts.");
        }

        var seatsToFill = MinimumPlayersToStart - lobby.Players.Count;
        if (seatsToFill <= 0)
        {
            throw new InvalidOperationException("This lobby already has enough players to start.");
        }

        var existingNames = lobby
            .Players.Select(player => player.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < seatsToFill; index++)
        {
            var bot = CreateBotPlayer(existingNames);
            existingNames.Add(bot.Name);
            await AddPlayerToLobbyAsync(lobbyId, bot);
        }
    }

    public async Task<Guid> CreateDemoGameAsync(Player player)
    {
        var trimmedName = player.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Enter a player name before starting a demo game.");
        }

        var demoPlayer = new Player(player.Id, trimmedName) { Ready = true };
        var lobbyId = Guid.NewGuid();

        await AddPlayerToLobbyAsync(lobbyId, demoPlayer);
        await FillSeatsAsync(lobbyId, demoPlayer.Id);
        await StartGameAsync(lobbyId, demoPlayer.Id);

        return lobbyId;
    }

    public async Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId)
    {
        var meta = await LoadLobbyMeta(lobbyId);
        var playerKey = PKey(playerId);
        var existingHash = await _db.HashGetAllAsync(playerKey);
        var playerName = GetHashValueOrDefault(existingHash, "Name") ?? "A player";

        await _db.SetRemoveAsync(LSet(lobbyId), playerKey);
        await _db.KeyDeleteAsync(playerKey);
        await _db.HashDeleteAsync(Acts(lobbyId), playerId.ToString());
        await _db.HashDeleteAsync(Votes(lobbyId), playerId.ToString());

        var remainingPlayers = await LoadPlayersAsync(lobbyId);
        if (remainingPlayers.Count == 0)
        {
            await DeleteLobbyAsync(lobbyId);
            await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
            return;
        }

        var hostPlayerId = SelectPreferredHostPlayerId(remainingPlayers, meta.HostPlayerId);

        await SaveLobbyMeta(
            lobbyId,
            meta.Phase,
            meta.Day,
            meta.Winner,
            hostPlayerId,
            meta.LatestEvent
        );

        await AppendSystemMessageAsync(
            lobbyId,
            meta.Phase == Phase.Setup
                ? $"{playerName} left the lobby."
                : $"{playerName} left the game."
        );

        if (meta.Phase is not Phase.Setup and not Phase.GameOver)
        {
            await TryDeclareWinnerAsync(
                lobbyId,
                $"{playerName} left the game.",
                hostPlayerId,
                meta.Day
            );
        }

        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task TogglePlayerReadyAsync(Guid lobbyId, Guid playerId)
    {
        var meta = await LoadLobbyMeta(lobbyId);
        if (meta.Phase != Phase.Setup)
        {
            throw new InvalidOperationException(
                "Ready state can only be changed before the game starts."
            );
        }

        var playerKey = PKey(playerId);
        if (!await _db.SetContainsAsync(LSet(lobbyId), playerKey))
        {
            throw new InvalidOperationException("You are no longer in this lobby.");
        }

        var current = (await _db.HashGetAsync(playerKey, "Ready")).ToBool();
        await _db.HashSetAsync(playerKey, "Ready", RedisBool.FromBool(!current));
        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task StartGameAsync(Guid lobbyId, Guid requestedByPlayerId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        EnsureHost(lobby, requestedByPlayerId);
        EnsureLobbyCanStart(lobby);

        await BeginGameAsync(
            lobby,
            Phase.Night,
            1,
            "The first night begins. Fate will go as it will.",
            populateBotNightActions: true,
            populateBotVotes: false
        );
        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task SendChatMessageAsync(Guid lobbyId, Guid playerId, string message)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        var sender = GetRequiredPlayer(lobby, playerId);
        EnsureChatAllowed(lobby, sender);

        await AppendChatMessageAsync(
            lobbyId,
            new ChatMessage
            {
                SenderPlayerId = sender.Id,
                SenderName = sender.Name,
                Body = NormalizeChatMessage(message),
                IsSystem = false,
            }
        );

        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task SubmitNightActionAsync(Guid lobbyId, NightAction action)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        if (lobby.Phase != Phase.Night)
        {
            throw new InvalidOperationException("Night actions can only be submitted at night.");
        }

        var actor = GetRequiredPlayer(lobby, action.ActorId);
        if (!actor.Alive)
        {
            throw new InvalidOperationException("You can no longer act in this game.");
        }

        var expectedAction = GetNightActionForRole(actor.Role);
        if (expectedAction is null)
        {
            throw new InvalidOperationException("Your role has no night action.");
        }

        if (expectedAction != action.Type)
        {
            throw new InvalidOperationException("That action does not match your role.");
        }

        if (action.TargetId is null)
        {
            throw new InvalidOperationException("Choose a target first.");
        }

        var target = GetRequiredPlayer(lobby, action.TargetId.Value);
        if (!target.Alive)
        {
            throw new InvalidOperationException("You can only target living players.");
        }

        ValidateNightTarget(actor, target, action.Type);

        await _db.HashSetAsync(Acts(lobbyId), action.ActorId.ToString(), Serialize(action));
        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task SubmitVoteAsync(Guid lobbyId, Guid voter, Guid target)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        if (lobby.Phase != Phase.Day)
        {
            throw new InvalidOperationException("Votes can only be submitted during the day.");
        }

        var votingPlayer = GetRequiredPlayer(lobby, voter);
        var targetPlayer = GetRequiredPlayer(lobby, target);

        if (!votingPlayer.Alive)
        {
            throw new InvalidOperationException("Only living players may vote.");
        }

        if (!targetPlayer.Alive)
        {
            throw new InvalidOperationException("You can only vote for a living player.");
        }

        if (voter == target)
        {
            throw new InvalidOperationException("You cannot vote for yourself.");
        }

        await _db.HashSetAsync(Votes(lobbyId), voter.ToString(), target.ToString());
        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task AdvancePhaseAsync(Guid lobbyId, Guid requestedByPlayerId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        EnsureHost(lobby, requestedByPlayerId);

        switch (lobby.Phase)
        {
            case Phase.Setup:
                throw new InvalidOperationException("Start the game from the lobby first.");

            case Phase.Night:
                var nightSummary = await ResolveNightAsync(lobby);
                var nightMeta = await LoadLobbyMeta(lobbyId);
                if (nightMeta.Winner is null)
                {
                    await SaveLobbyMeta(
                        lobbyId,
                        Phase.Dawn,
                        nightMeta.Day,
                        null,
                        nightMeta.HostPlayerId,
                        nightSummary
                    );
                }
                break;

            case Phase.Dawn:
                await SaveLobbyMeta(
                    lobbyId,
                    Phase.Day,
                    lobby.DayCount,
                    null,
                    lobby.HostPlayerId,
                    lobby.LatestEvent
                );
                await AppendSystemMessageAsync(
                    lobbyId,
                    $"Day {lobby.DayCount} begins. Discuss and vote."
                );
                await PopulateBotVotesAsync(lobbyId);
                break;

            case Phase.Day:
                var voteSummary = await ResolveVoteAsync(lobby);
                var dayMeta = await LoadLobbyMeta(lobbyId);
                if (dayMeta.Winner is null)
                {
                    await SaveLobbyMeta(
                        lobbyId,
                        Phase.Night,
                        dayMeta.Day + 1,
                        null,
                        dayMeta.HostPlayerId,
                        voteSummary
                    );
                    await AppendSystemMessageAsync(
                        lobbyId,
                        $"Night {dayMeta.Day + 1} begins. Fate will go as it will."
                    );
                    await PopulateBotNightActionsAsync(lobbyId);
                }
                break;

            case Phase.GameOver:
                throw new InvalidOperationException("The game is already over.");
        }

        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task<GameView> GetGameViewAsync(Guid lobbyId, Guid playerId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        var currentPlayer = GetRequiredPlayer(lobby, playerId);
        var submittedNightAction = await LoadNightActionAsync(lobbyId, playerId);
        var submittedVoteTargetId = await LoadSubmittedVoteAsync(lobbyId, playerId);
        var storedPrivateNote = (await _db.HashGetAsync(PKey(playerId), "PrivateNote")).ToString();

        return new GameView
        {
            Lobby = lobby,
            CurrentPlayer = currentPlayer,
            SubmittedNightAction = submittedNightAction,
            SubmittedVoteTargetId = submittedVoteTargetId,
            PrivateNote = BuildPrivateNote(lobby, currentPlayer, storedPrivateNote),
            RequiredNightActions = GetNightActors(lobby).Count,
            SubmittedNightActions = (int)await _db.HashLengthAsync(Acts(lobbyId)),
            RequiredVotes = lobby.Players.Count(player => player.Alive),
            SubmittedVotes = (int)await _db.HashLengthAsync(Votes(lobbyId)),
        };
    }

    public async Task<Lobby> GetLobbyByIdAsync(Guid lobbyId)
    {
        var players = await LoadPlayersAsync(lobbyId);
        var meta = await LoadLobbyMeta(lobbyId);
        var recentMessages = await LoadRecentMessagesAsync(lobbyId);

        return new Lobby(lobbyId)
        {
            Players = players.OrderBy(player => player.JoinedAtUtc).ToList(),
            Phase = meta.Phase,
            DayCount = meta.Day,
            Winner = meta.Winner,
            HostPlayerId = meta.HostPlayerId,
            LatestEvent = meta.LatestEvent,
            RecentMessages = recentMessages,
        };
    }

    private async Task<string> ResolveNightAsync(Lobby lobby)
    {
        var actions = await LoadNightActionsAsync(lobby.Id);
        await _db.KeyDeleteAsync(Acts(lobby.Id));
        await ClearPrivateNotesAsync(lobby);
        var validTargetIds = lobby
            .Players.Where(player => player.Alive)
            .Select(player => player.Id)
            .ToHashSet();

        foreach (
            var action in actions.Where(action =>
                action.Type == NightAct.Inspect && action.TargetId is not null
            )
        )
        {
            var actor = lobby.Players.FirstOrDefault(player => player.Id == action.ActorId);
            var target = lobby.Players.FirstOrDefault(player => player.Id == action.TargetId);

            if (actor is null || target is null || !actor.Alive || !target.Alive)
            {
                continue;
            }

            await SetPrivateNoteAsync(
                actor.Id,
                $"Your vision reveals that {target.Name} is a {target.Role}."
            );
        }

        var protectId = SelectLeadingTargetId(
            actions
                .Where(action => action.Type == NightAct.Protect)
                .Select(action => action.TargetId),
            validTargetIds
        );
        var killId = SelectLeadingTargetId(
            actions.Where(action => action.Type == NightAct.Kill).Select(action => action.TargetId),
            validTargetIds
        );

        string summary;
        if (killId is null || killId == protectId)
        {
            summary = "Dawn breaks. No one died in the night.";
        }
        else
        {
            var target = GetRequiredPlayer(lobby, killId.Value);
            await _db.HashSetAsync(PKey(target.Id), "Alive", RedisBool.FromBool(false));
            summary = $"Dawn breaks. {target.Name} was killed in the night.";
        }

        await AppendSystemMessageAsync(lobby.Id, summary);
        await TryDeclareWinnerAsync(lobby.Id, summary, lobby.HostPlayerId, lobby.DayCount);
        return summary;
    }

    private async Task<string> ResolveVoteAsync(Lobby lobby)
    {
        var allVotes = await _db.HashGetAllAsync(Votes(lobby.Id));
        await _db.KeyDeleteAsync(Votes(lobby.Id));
        var validTargetIds = lobby
            .Players.Where(player => player.Alive)
            .Select(player => player.Id)
            .ToHashSet();

        var lynchedId = SelectLeadingTargetId(
            allVotes.Select(vote =>
                Guid.TryParse(vote.Value.ToString(), out var id) ? id : (Guid?)null
            ),
            validTargetIds
        );

        string summary;
        if (lynchedId is null)
        {
            summary = "The village failed to agree on a lynch.";
        }
        else
        {
            var target = GetRequiredPlayer(lobby, lynchedId.Value);
            await _db.HashSetAsync(PKey(target.Id), "Alive", RedisBool.FromBool(false));
            summary = $"The village cast out {target.Name}.";
        }

        await AppendSystemMessageAsync(lobby.Id, summary);
        await TryDeclareWinnerAsync(lobby.Id, summary, lobby.HostPlayerId, lobby.DayCount);
        return summary;
    }

    private async Task<bool> TryDeclareWinnerAsync(
        Guid lobbyId,
        string summary,
        Guid? hostPlayerId,
        int day
    )
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        var wolves = lobby.Players.Count(player => player.Role == Role.Werewolf && player.Alive);
        var village = lobby.Players.Count(player => player.Role != Role.Werewolf && player.Alive);

        var winner =
            wolves == 0 ? "Villagers"
            : wolves >= village ? "Werewolves"
            : null;

        if (winner is null)
        {
            return false;
        }

        await SaveLobbyMeta(
            lobbyId,
            Phase.GameOver,
            day,
            winner,
            hostPlayerId,
            $"{summary} {winner} win."
        );
        await AppendSystemMessageAsync(lobbyId, $"{winner} win.");
        return true;
    }

    private async Task AssignRolesAsync(Lobby lobby)
    {
        var random = new Random();
        var shuffled = lobby.Players.OrderBy(_ => random.Next()).ToList();
        var roles = BuildRoleDeck(shuffled.Count);

        for (var index = 0; index < shuffled.Count; index++)
        {
            shuffled[index].Role = roles[index];
        }

        await Task.WhenAll(
            shuffled.Select(player =>
                _db.HashSetAsync(
                    PKey(player.Id),
                    [
                        new("Role", player.Role.ToString()),
                        new("Alive", RedisBool.FromBool(true)),
                        new("Ready", RedisBool.FromBool(false)),
                        new("PrivateNote", string.Empty),
                    ]
                )
            )
        );
    }

    private static List<Role> BuildRoleDeck(int playerCount)
    {
        var roles = new List<Role> { Role.Werewolf, Role.Seer, Role.Doctor };

        if (playerCount >= 6)
        {
            roles.Add(Role.Werewolf);
        }

        while (roles.Count < playerCount)
        {
            roles.Add(Role.Villager);
        }

        return roles;
    }

    private async Task PopulateBotNightActionsAsync(Guid lobbyId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        if (lobby.Phase != Phase.Night)
        {
            return;
        }

        var livingBots = lobby.Players.Where(player => player.IsBot && player.Alive).ToList();
        if (livingBots.Count == 0)
        {
            return;
        }

        var submittedActorIds = await LoadSubmittedActorIdsAsync(Acts(lobbyId));
        var werewolfTarget = PickRandomTarget(
            lobby.Players.Where(player => player.Alive && player.Role != Role.Werewolf).ToList()
        );

        foreach (var bot in livingBots)
        {
            if (submittedActorIds.Contains(bot.Id))
            {
                continue;
            }

            var actionType = GetNightActionForRole(bot.Role);
            if (actionType is null)
            {
                continue;
            }

            var targetId = actionType switch
            {
                NightAct.Kill => werewolfTarget,
                NightAct.Inspect => PickRandomTarget(
                    lobby.Players.Where(player => player.Alive && player.Id != bot.Id).ToList()
                ),
                NightAct.Protect => PickRandomTarget(
                    lobby.Players.Where(player => player.Alive).ToList()
                ),
                _ => null,
            };

            if (targetId is null)
            {
                continue;
            }

            await _db.HashSetAsync(
                Acts(lobbyId),
                bot.Id.ToString(),
                Serialize(new NightAction(bot.Id, actionType.Value, targetId))
            );
        }
    }

    private async Task PopulateBotVotesAsync(Guid lobbyId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        if (lobby.Phase != Phase.Day)
        {
            return;
        }

        var livingBots = lobby.Players.Where(player => player.IsBot && player.Alive).ToList();
        if (livingBots.Count == 0)
        {
            return;
        }

        var submittedVoterIds = await LoadSubmittedActorIdsAsync(Votes(lobbyId));
        var commonTarget = PickRandomTarget(lobby.Players.Where(player => player.Alive).ToList());

        foreach (var bot in livingBots)
        {
            if (submittedVoterIds.Contains(bot.Id))
            {
                continue;
            }

            var eligibleTargets = lobby
                .Players.Where(player => player.Alive && player.Id != bot.Id)
                .ToList();
            var targetId =
                commonTarget == bot.Id
                    ? PickRandomTarget(eligibleTargets)
                    : commonTarget ?? PickRandomTarget(eligibleTargets);

            if (targetId is null)
            {
                continue;
            }

            await _db.HashSetAsync(Votes(lobbyId), bot.Id.ToString(), targetId.ToString());
        }
    }

    private async Task<List<Player>> LoadPlayersAsync(Guid lobbyId)
    {
        var players = new List<Player>();

        foreach (var playerKey in await _db.SetMembersAsync(LSet(lobbyId)))
        {
            var playerHash = await _db.HashGetAllAsync(playerKey.ToString());
            if (playerHash.Length > 0)
            {
                players.Add(MapPlayer(playerHash));
            }
        }

        return players;
    }

    private async Task<List<NightAction>> LoadNightActionsAsync(Guid lobbyId)
    {
        var allActions = await _db.HashGetAllAsync(Acts(lobbyId));
        return allActions
            .Where(action => action.Value.HasValue)
            .Select(action => Deserialize<NightAction>(action.Value))
            .ToList();
    }

    private async Task<NightAction?> LoadNightActionAsync(Guid lobbyId, Guid playerId)
    {
        var value = await _db.HashGetAsync(Acts(lobbyId), playerId.ToString());
        return value.HasValue ? Deserialize<NightAction>(value) : null;
    }

    private async Task<Guid?> LoadSubmittedVoteAsync(Guid lobbyId, Guid playerId)
    {
        var value = await _db.HashGetAsync(Votes(lobbyId), playerId.ToString());
        return Guid.TryParse(value.ToString(), out var targetId) ? targetId : null;
    }

    private async Task<HashSet<Guid>> LoadSubmittedActorIdsAsync(string key)
    {
        var entries = await _db.HashGetAllAsync(key);
        return entries
            .Select(entry => Guid.TryParse(entry.Name.ToString(), out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();
    }

    private async Task<List<ChatMessage>> LoadRecentMessagesAsync(Guid lobbyId)
    {
        var entries = await _db.ListRangeAsync(Chat(lobbyId));
        return entries
            .Where(entry => entry.HasValue)
            .Select(Deserialize<ChatMessage>)
            .OrderByDescending(message => message.CreatedAtUtc)
            .ToList();
    }

    private async Task ClearRoundStateAsync(Guid lobbyId) =>
        await _db.KeyDeleteAsync([Acts(lobbyId), Votes(lobbyId)]);

    private async Task BeginGameAsync(
        Lobby lobby,
        Phase openingPhase,
        int openingDay,
        string latestEvent,
        bool populateBotNightActions,
        bool populateBotVotes
    )
    {
        await ClearRoundStateAsync(lobby.Id);
        await AssignRolesAsync(lobby);
        await ClearPrivateNotesAsync(lobby);

        await SaveLobbyMeta(
            lobby.Id,
            openingPhase,
            openingDay,
            null,
            lobby.HostPlayerId,
            latestEvent
        );
        await AppendSystemMessageAsync(lobby.Id, latestEvent);

        if (populateBotNightActions)
        {
            await PopulateBotNightActionsAsync(lobby.Id);
        }

        if (populateBotVotes)
        {
            await PopulateBotVotesAsync(lobby.Id);
        }
    }

    private async Task ClearPrivateNotesAsync(Lobby lobby) =>
        await Task.WhenAll(
            lobby.Players.Select(player =>
                _db.HashSetAsync(PKey(player.Id), "PrivateNote", string.Empty)
            )
        );

    private Task SetPrivateNoteAsync(Guid playerId, string note) =>
        _db.HashSetAsync(PKey(playerId), "PrivateNote", note);

    private static List<Player> GetNightActors(Lobby lobby) =>
        lobby
            .Players.Where(player => player.Alive && GetNightActionForRole(player.Role) is not null)
            .ToList();

    private static string BuildPrivateNote(Lobby lobby, Player currentPlayer, string? storedNote)
    {
        var notes = new List<string>();

        if (currentPlayer.Role == Role.Werewolf)
        {
            var packmates = lobby
                .Players.Where(player =>
                    player.Role == Role.Werewolf && player.Id != currentPlayer.Id && player.Alive
                )
                .Select(player => player.Name)
                .ToList();

            if (packmates.Count > 0)
            {
                notes.Add($"Your packmate is {string.Join(", ", packmates)}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(storedNote))
        {
            notes.Add(storedNote);
        }

        return string.Join(" ", notes);
    }

    private static Player GetRequiredPlayer(Lobby lobby, Guid playerId) =>
        lobby.Players.FirstOrDefault(player => player.Id == playerId)
        ?? throw new InvalidOperationException("That player is no longer in the game.");

    private static void EnsureChatAllowed(Lobby lobby, Player player)
    {
        if (player.IsBot)
        {
            throw new InvalidOperationException("Bots cannot send chat messages.");
        }

        if (lobby.Phase == Phase.Night)
        {
            throw new InvalidOperationException("Public chat is closed during the night.");
        }

        if (
            lobby.Phase
            is not Phase.Setup
                and not Phase.Dawn
                and not Phase.Day
                and not Phase.GameOver
        )
        {
            throw new InvalidOperationException("Chat is unavailable right now.");
        }

        if (!player.Alive && lobby.Phase != Phase.GameOver)
        {
            throw new InvalidOperationException(
                "You can watch the discussion, but only living players can speak."
            );
        }
    }

    private static void EnsureHost(Lobby lobby, Guid requestedByPlayerId)
    {
        if (lobby.HostPlayerId != requestedByPlayerId)
        {
            throw new InvalidOperationException("Only the host can do that.");
        }
    }

    private static void EnsureLobbyCanStart(Lobby lobby)
    {
        if (lobby.Phase != Phase.Setup)
        {
            throw new InvalidOperationException("This lobby has already started.");
        }

        if (lobby.Players.Count < MinimumPlayersToStart)
        {
            throw new InvalidOperationException("At least four players are required to start.");
        }

        if (lobby.Players.Any(player => !player.Ready))
        {
            throw new InvalidOperationException(
                "All players must be ready before the game starts."
            );
        }
    }

    private static Guid? SelectPreferredHostPlayerId(
        IReadOnlyCollection<Player> players,
        Guid? currentHostPlayerId
    )
    {
        if (players.Count == 0)
        {
            return null;
        }

        var currentHost = currentHostPlayerId is Guid hostId
            ? players.FirstOrDefault(player => player.Id == hostId)
            : null;

        if (currentHost is not null && (!currentHost.IsBot || players.All(player => player.IsBot)))
        {
            return currentHost.Id;
        }

        return players
            .OrderBy(player => player.IsBot)
            .ThenBy(player => player.JoinedAtUtc)
            .Select(player => (Guid?)player.Id)
            .First();
    }

    private static NightAct? GetNightActionForRole(Role role) =>
        role switch
        {
            Role.Werewolf => NightAct.Kill,
            Role.Seer => NightAct.Inspect,
            Role.Doctor => NightAct.Protect,
            _ => null,
        };

    private static void ValidateNightTarget(Player actor, Player target, NightAct actionType)
    {
        if (actionType == NightAct.Protect)
        {
            return;
        }

        if (actor.Id == target.Id)
        {
            throw new InvalidOperationException("You cannot target yourself.");
        }

        if (actionType == NightAct.Kill && target.Role == Role.Werewolf)
        {
            throw new InvalidOperationException("Werewolves cannot target each other.");
        }
    }

    private static Guid? SelectLeadingTargetId(
        IEnumerable<Guid?> targets,
        HashSet<Guid> validTargetIds
    )
    {
        var groupedTargets = targets
            .Where(target => target is not null)
            .Select(target => target!.Value)
            .Where(validTargetIds.Contains)
            .GroupBy(target => target)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .ToList();

        if (groupedTargets.Count == 0)
        {
            return null;
        }

        if (groupedTargets.Count > 1 && groupedTargets[0].Count() == groupedTargets[1].Count())
        {
            return null;
        }

        return groupedTargets[0].Key;
    }

    private static Guid? PickRandomTarget(IReadOnlyList<Player> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates[Random.Shared.Next(candidates.Count)].Id;
    }

    private async Task AppendSystemMessageAsync(Guid lobbyId, string message) =>
        await AppendChatMessageAsync(
            lobbyId,
            new ChatMessage
            {
                SenderName = "System",
                Body = NormalizeChatMessage(message),
                IsSystem = true,
            }
        );

    private async Task AppendChatMessageAsync(Guid lobbyId, ChatMessage message)
    {
        await _db.ListRightPushAsync(Chat(lobbyId), Serialize(message));
        await _db.ListTrimAsync(Chat(lobbyId), -ChatMessageLimit, -1);
    }

    private static string NormalizeChatMessage(string message)
    {
        var trimmed = message.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Enter a message before sending.");
        }

        return trimmed.Length <= MaxChatMessageLength ? trimmed : trimmed[..MaxChatMessageLength];
    }

    private record Meta(
        Phase Phase,
        int Day,
        string? Winner,
        Guid? HostPlayerId,
        string LatestEvent
    );

    private Task SaveLobbyMeta(
        Guid id,
        Phase phase,
        int day,
        string? winner,
        Guid? hostPlayerId,
        string? latestEvent
    ) =>
        _db.HashSetAsync(
            LMeta(id),
            [
                new("Phase", phase.ToString()),
                new("Day", day),
                new("Winner", winner ?? string.Empty),
                new("HostPlayerId", hostPlayerId?.ToString() ?? string.Empty),
                new("LatestEvent", latestEvent ?? string.Empty),
            ]
        );

    private async Task<Meta> LoadLobbyMeta(Guid id)
    {
        var hash = await _db.HashGetAllAsync(LMeta(id));
        if (hash.Length == 0)
        {
            return new Meta(Phase.Setup, 0, null, null, string.Empty);
        }

        var winner = GetHashValueOrDefault(hash, "Winner");
        var hostPlayerIdValue = GetHashValueOrDefault(hash, "HostPlayerId");
        var latestEvent = GetHashValueOrDefault(hash, "LatestEvent") ?? string.Empty;

        return new Meta(
            Enum.Parse<Phase>(GetRequiredHashValue(hash, "Phase")),
            int.Parse(GetRequiredHashValue(hash, "Day")),
            string.IsNullOrWhiteSpace(winner) ? null : winner,
            Guid.TryParse(hostPlayerIdValue, out var hostPlayerId) ? hostPlayerId : null,
            latestEvent
        );
    }

    private Task DeleteLobbyAsync(Guid id) =>
        _db.KeyDeleteAsync([LSet(id), LMeta(id), Acts(id), Votes(id), Chat(id)]);

    private static string PKey(Guid id) => $"player:{id}";

    private static string LSet(Guid id) => $"lobby:{id}:players";

    private static string LMeta(Guid id) => $"lobby:{id}:meta";

    private static string Acts(Guid id) => $"lobby:{id}:acts";

    private static string Votes(Guid id) => $"lobby:{id}:votes";

    private static string Chat(Guid id) => $"lobby:{id}:chat";

    private static Player MapPlayer(HashEntry[] hash)
    {
        return new Player(
            Guid.Parse(GetRequiredHashValue(hash, "Id")),
            GetRequiredHashValue(hash, "Name")
        )
        {
            Alive = hash.Single(entry => entry.Name == "Alive").Value.ToBool(),
            IsBot = ReadOptionalBoolean(hash, "IsBot") ?? false,
            Ready = hash.Single(entry => entry.Name == "Ready").Value.ToBool(),
            Role = Enum.Parse<Role>(GetRequiredHashValue(hash, "Role")),
            JoinedAtUtc = ReadOptionalDateTimeOffset(hash, "JoinedAtUtc") ?? DateTimeOffset.UtcNow,
        };
    }

    private static Player CreateBotPlayer(HashSet<string> existingNames)
    {
        var suffix = 1;
        string name;

        do
        {
            name = $"{BotNamePrefix} {suffix}";
            suffix++;
        } while (existingNames.Contains(name));

        return new Player(Guid.NewGuid(), name)
        {
            Alive = true,
            IsBot = true,
            Ready = true,
        };
    }

    private static string GetRequiredHashValue(HashEntry[] hash, RedisValue name)
    {
        var value = GetHashValueOrDefault(hash, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Expected Redis field '{name}' to have a value."
            );
    }

    private static string? GetHashValueOrDefault(HashEntry[] hash, RedisValue name)
    {
        var entry = hash.FirstOrDefault(item => item.Name == name);
        if (!entry.Name.HasValue || !entry.Value.HasValue)
        {
            return null;
        }

        var value = entry.Value.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? ReadOptionalBoolean(HashEntry[] hash, RedisValue name)
    {
        var value = GetHashValueOrDefault(hash, name);
        return value is null ? null : new RedisValue(value).ToBool();
    }

    private static TEnum? ReadOptionalEnum<TEnum>(HashEntry[] hash, RedisValue name)
        where TEnum : struct
    {
        var value = GetHashValueOrDefault(hash, name);
        return Enum.TryParse<TEnum>(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(HashEntry[] hash, RedisValue name)
    {
        var value = GetHashValueOrDefault(hash, name);
        if (
            DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed
            )
        )
        {
            return parsed;
        }

        return null;
    }

    private static string Serialize<T>(T obj) => System.Text.Json.JsonSerializer.Serialize(obj);

    private static T Deserialize<T>(RedisValue value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(value!)!;
}

static class RedisBool
{
    public static bool ToBool(this RedisValue value) =>
        value.HasValue
        && (value == "1" || value.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

    public static RedisValue FromBool(bool value) => value ? "1" : "0";
}
