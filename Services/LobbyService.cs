#nullable enable

using System.Globalization;
using lycanthrope.Interfaces;
using lycanthrope.Models;
using StackExchange.Redis;

public interface IGameEngineService
{
    Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player player);
    Task AdvancePhaseAsync(Guid lobbyId);
    Task<Lobby> GetLobbyByIdAsync(Guid lobbyId);
    Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId);
    Task StartGameAsync(Guid lobbyId, Guid requestedByPlayerId);
    Task SubmitNightActionAsync(Guid lobbyId, NightAction action);
    Task SubmitVoteAsync(Guid lobbyId, Guid voter, Guid target);
    Task TogglePlayerReadyAsync(Guid lobbyId, Guid playerId);
}

public class GameEngineService : IGameEngineService
{
    private const int MinimumPlayersToStart = 4;
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
        var role = ReadOptionalEnum<Role>(existingHash, "Role") ?? Role.Villager;

        await _db.HashSetAsync(
            playerKey,
            [
                new("Id", player.Id.ToString()),
                new("Name", player.Name),
                new("Ready", RedisBool.FromBool(ready)),
                new("Role", role.ToString()),
                new("Alive", RedisBool.FromBool(alive)),
                new("Lobby", lobbyId.ToString()),
                new("JoinedAtUtc", joinedAtUtc.UtcDateTime.ToString("O")),
            ]
        );

        await _db.SetAddAsync(LSet(lobbyId), playerKey);

        var hostPlayerId = meta.HostPlayerId;
        if (
            hostPlayerId is null
            || !await _db.SetContainsAsync(LSet(lobbyId), PKey(hostPlayerId.Value))
        )
        {
            hostPlayerId = player.Id;
        }

        await SaveLobbyMeta(lobbyId, meta.Phase, meta.Day, meta.Winner, hostPlayerId);

        var lobby = await GetLobbyByIdAsync(lobbyId);
        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
        return lobby;
    }

    public async Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId)
    {
        var meta = await LoadLobbyMeta(lobbyId);
        var playerKey = PKey(playerId);

        await _db.SetRemoveAsync(LSet(lobbyId), playerKey);
        await _db.KeyDeleteAsync(playerKey);

        var remainingPlayers = await LoadPlayersAsync(lobbyId);
        if (remainingPlayers.Count == 0)
        {
            await DeleteLobbyAsync(lobbyId);
            await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
            return;
        }

        var hostPlayerId = meta.HostPlayerId;
        if (
            hostPlayerId is null
            || hostPlayerId == playerId
            || !remainingPlayers.Any(player => player.Id == hostPlayerId.Value)
        )
        {
            hostPlayerId = remainingPlayers.OrderBy(player => player.JoinedAtUtc).First().Id;
        }

        await SaveLobbyMeta(lobbyId, meta.Phase, meta.Day, meta.Winner, hostPlayerId);
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
        if (lobby.Phase != Phase.Setup)
        {
            throw new InvalidOperationException("This lobby has already started.");
        }

        if (lobby.HostPlayerId != requestedByPlayerId)
        {
            throw new InvalidOperationException("Only the host can start the game.");
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

        await AssignRolesAsync(lobby);
        await SaveLobbyMeta(lobbyId, Phase.Night, 0, null, lobby.HostPlayerId);
        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    public async Task SubmitNightActionAsync(Guid lobbyId, NightAction action) =>
        await _db.ListRightPushAsync(Acts(lobbyId), Serialize(action));

    public async Task SubmitVoteAsync(Guid lobbyId, Guid voter, Guid target) =>
        await _db.HashSetAsync(Votes(lobbyId), voter.ToString(), target.ToString());

    public async Task AdvancePhaseAsync(Guid lobbyId)
    {
        var meta = await LoadLobbyMeta(lobbyId);

        switch (meta.Phase)
        {
            case Phase.Night:
                await ResolveNight(lobbyId);
                meta = await LoadLobbyMeta(lobbyId);
                if (meta.Winner is null)
                {
                    await SaveLobbyMeta(lobbyId, Phase.Dawn, meta.Day, null, meta.HostPlayerId);
                }
                break;

            case Phase.Dawn:
                await SaveLobbyMeta(lobbyId, Phase.Day, meta.Day, null, meta.HostPlayerId);
                break;

            case Phase.Day:
                await ResolveVote(lobbyId);
                meta = await LoadLobbyMeta(lobbyId);
                if (meta.Winner is null)
                {
                    await SaveLobbyMeta(
                        lobbyId,
                        Phase.Night,
                        meta.Day + 1,
                        null,
                        meta.HostPlayerId
                    );
                }
                break;
        }

        await _notifications.NotifyLobbyUpdatedAsync(lobbyId);
    }

    private async Task ResolveNight(Guid lobbyId)
    {
        var actions = (await _db.ListRangeAsync(Acts(lobbyId)))
            .Select(value => Deserialize<NightAction>(value))
            .ToList();

        await _db.KeyDeleteAsync(Acts(lobbyId));

        var protectId = actions.FirstOrDefault(action => action.Type == NightAct.Protect)?.TargetId;
        var killId = actions.FirstOrDefault(action => action.Type == NightAct.Kill)?.TargetId;

        if (killId is not null && killId != protectId)
        {
            var playerKey = PKey(killId.Value);
            await _db.HashSetAsync(playerKey, "Alive", RedisBool.FromBool(false));
        }

        await CheckWin(lobbyId);
    }

    private async Task ResolveVote(Guid lobbyId)
    {
        var votes = await _db.HashGetAllAsync(Votes(lobbyId));
        await _db.KeyDeleteAsync(Votes(lobbyId));
        if (votes.Length == 0)
        {
            return;
        }

        var lynchedId = votes
            .GroupBy(vote => vote.Value)
            .OrderByDescending(group => group.Count())
            .First()
            .Key.ToString();

        if (string.IsNullOrWhiteSpace(lynchedId))
        {
            return;
        }

        var playerKey = PKey(Guid.Parse(lynchedId));
        await _db.HashSetAsync(playerKey, "Alive", RedisBool.FromBool(false));

        await CheckWin(lobbyId);
    }

    private async Task CheckWin(Guid lobbyId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        var wolves = lobby.Players.Count(player => player.Role == Role.Werewolf && player.Alive);
        var village = lobby.Players.Count(player => player.Role != Role.Werewolf && player.Alive);

        var winner =
            wolves == 0 ? "Villagers"
            : wolves >= village ? "Werewolves"
            : null;

        if (winner is not null)
        {
            await SaveLobbyMeta(
                lobbyId,
                Phase.GameOver,
                lobby.DayCount,
                winner,
                lobby.HostPlayerId
            );
        }
    }

    private async Task AssignRolesAsync(Lobby lobby)
    {
        var random = new Random();
        var shuffled = lobby.Players.OrderBy(_ => random.Next()).ToList();

        shuffled[0].Role = Role.Werewolf;
        shuffled[1].Role = Role.Werewolf;
        shuffled[2].Role = Role.Seer;
        shuffled[3].Role = Role.Doctor;

        await Task.WhenAll(
            shuffled.Select(player =>
                _db.HashSetAsync(PKey(player.Id), "Role", player.Role.ToString())
            )
        );
    }

    public async Task<Lobby> GetLobbyByIdAsync(Guid lobbyId)
    {
        var players = await LoadPlayersAsync(lobbyId);
        var meta = await LoadLobbyMeta(lobbyId);

        return new Lobby(lobbyId)
        {
            Players = players.OrderBy(player => player.JoinedAtUtc).ToList(),
            Phase = meta.Phase,
            DayCount = meta.Day,
            Winner = meta.Winner,
            HostPlayerId = meta.HostPlayerId,
        };
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

    private record Meta(Phase Phase, int Day, string? Winner, Guid? HostPlayerId);

    private Task SaveLobbyMeta(Guid id, Phase phase, int day, string? winner, Guid? hostPlayerId) =>
        _db.HashSetAsync(
            LMeta(id),
            new HashEntry[]
            {
                new("Phase", phase.ToString()),
                new("Day", day),
                new("Winner", winner ?? string.Empty),
                new("HostPlayerId", hostPlayerId?.ToString() ?? string.Empty),
            }
        );

    private async Task<Meta> LoadLobbyMeta(Guid id)
    {
        var hash = await _db.HashGetAllAsync(LMeta(id));
        if (hash.Length == 0)
        {
            return new Meta(Phase.Setup, 0, null, null);
        }

        var winner = GetHashValueOrDefault(hash, "Winner");
        var hostPlayerIdValue = GetHashValueOrDefault(hash, "HostPlayerId");

        return new Meta(
            Enum.Parse<Phase>(GetRequiredHashValue(hash, "Phase")),
            int.Parse(GetRequiredHashValue(hash, "Day")),
            string.IsNullOrWhiteSpace(winner) ? null : winner,
            Guid.TryParse(hostPlayerIdValue, out var hostPlayerId) ? hostPlayerId : null
        );
    }

    private Task DeleteLobbyAsync(Guid id) =>
        _db.KeyDeleteAsync([LSet(id), LMeta(id), Acts(id), Votes(id)]);

    private static string PKey(Guid id) => $"player:{id}";

    private static string LSet(Guid id) => $"lobby:{id}:players";

    private static string LMeta(Guid id) => $"lobby:{id}:meta";

    private static string Acts(Guid id) => $"lobby:{id}:acts";

    private static string Votes(Guid id) => $"lobby:{id}:votes";

    private static Player MapPlayer(HashEntry[] hash)
    {
        return new Player(
            Guid.Parse(GetRequiredHashValue(hash, "Id")),
            GetRequiredHashValue(hash, "Name")
        )
        {
            Alive = hash.Single(entry => entry.Name == "Alive").Value.ToBool(),
            Ready = hash.Single(entry => entry.Name == "Ready").Value.ToBool(),
            Role = Enum.Parse<Role>(GetRequiredHashValue(hash, "Role")),
            JoinedAtUtc = ReadOptionalDateTimeOffset(hash, "JoinedAtUtc") ?? DateTimeOffset.UtcNow,
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

public enum Role
{
    Villager,
    Werewolf,
    Seer,
    Doctor,
}

public enum Phase
{
    Setup,
    Night,
    Dawn,
    Day,
    GameOver,
}

public enum NightAct
{
    Kill,
    Protect,
    Inspect,
}

public record NightAction(Guid ActorId, NightAct Type, Guid? TargetId);
