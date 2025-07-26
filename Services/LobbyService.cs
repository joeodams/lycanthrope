using lycanthrope.Interfaces;
using lycanthrope.Models;
using StackExchange.Redis;

public interface IGameEngineService
{
    Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player p);
    Task AdvancePhaseAsync(Guid lobbyId);
    Task<Lobby> GetLobbyByIdAsync(Guid lobbyId);
    Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId);
    Task StartGameAsync(Guid lobbyId);
    Task SubmitNightActionAsync(Guid lobbyId, NightAction a);
    Task SubmitVoteAsync(Guid lobbyId, Guid voter, Guid target);
    Task TogglePlayerReadyAsync(Guid lobbyId, Guid playerId);
}

public class GameEngineService : IGameEngineService
// or IGameEngine
{
    private readonly IDatabase _db;
    private readonly ISubscriber _pub;

    public GameEngineService(IDatabase database, ISubscriber subscriber)
    {
        _db = database;
        _pub = subscriber;
        // _pub = redis.GetSubscriber();
    }

    /* ───────────────────────── Player join/leave/ready ────────────────── */

    public async Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player p)
    {
        string pKey = PKey(p.Id);
        await _db.HashSetAsync(
            pKey,
            new HashEntry[]
            {
                new("Id", p.Id.ToString()),
                new("Name", p.Name),
                new("Ready", RedisBool.FromBool(p.Ready)),
                new("Role", Role.Villager.ToString()),
                new("Alive", "1"),
                new("Lobby", lobbyId.ToString()),
            }
        );
        await _db.SetAddAsync(LSet(lobbyId), pKey);
        await _pub.PublishAsync(Evt(lobbyId), $"PlayerJoined:{p.Name}");
        return await GetLobbyByIdAsync(lobbyId);
    }

    public async Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId)
    {
        string pKey = PKey(playerId);
        await _db.SetRemoveAsync(LSet(lobbyId), pKey);
        await _db.KeyDeleteAsync(pKey);
        await _pub.PublishAsync(Evt(lobbyId), $"PlayerLeft:{playerId}");
    }

    public async Task TogglePlayerReadyAsync(Guid lobbyId, Guid playerId)
    {
        string pKey = PKey(playerId);
        bool current = (await _db.HashGetAsync(pKey, "Ready")).ToBool();
        await _db.HashSetAsync(pKey, "Ready", RedisBool.FromBool(!current));
    }

    /* ───────────────────────── Game lifecycle ─────────────────────────── */

    public async Task StartGameAsync(Guid lobbyId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        AssignRoles(lobby);
        await SaveLobbyMeta(lobbyId, Phase.Night, 0, null);
        await _pub.PublishAsync(Evt(lobbyId), "Phase:Night");
    }

    public async Task SubmitNightActionAsync(Guid lobbyId, NightAction a)
    {
        await _db.ListRightPushAsync(Acts(lobbyId), Serialize(a));
    }

    public async Task SubmitVoteAsync(Guid lobbyId, Guid voter, Guid target) =>
        await _db.HashSetAsync(Votes(lobbyId), voter.ToString(), target.ToString());

    public async Task AdvancePhaseAsync(Guid lobbyId)
    {
        var meta = await LoadLobbyMeta(lobbyId);
        switch (meta.Phase)
        {
            case Phase.Night:
                await ResolveNight(lobbyId);
                await SaveLobbyMeta(lobbyId, Phase.Dawn, meta.Day, null);
                await _pub.PublishAsync(Evt(lobbyId), "Phase:Dawn");
                break;

            case Phase.Dawn:
                await SaveLobbyMeta(lobbyId, Phase.Day, meta.Day, null);
                await _pub.PublishAsync(Evt(lobbyId), "Phase:Day");
                break;

            case Phase.Day:
                await ResolveVote(lobbyId);
                meta = await LoadLobbyMeta(lobbyId);
                if (meta.Winner is null)
                {
                    await SaveLobbyMeta(lobbyId, Phase.Night, meta.Day + 1, null);
                    await _pub.PublishAsync(Evt(lobbyId), "Phase:Night");
                }
                break;
        }
    }

    /* ───────────────────────── Resolution helpers ─────────────────────── */

    private async Task ResolveNight(Guid lobbyId)
    {
        var acts = (await _db.ListRangeAsync(Acts(lobbyId)))
            .Select(x => Deserialize<NightAction>(x))
            .ToList();
        await _db.KeyDeleteAsync(Acts(lobbyId));

        Guid? protectId = acts.FirstOrDefault(a => a.Type == NightAct.Protect)?.TargetId;
        Guid? killId = acts.FirstOrDefault(a => a.Type == NightAct.Kill)?.TargetId;

        if (killId is not null && killId != protectId)
        {
            string pKey = PKey(killId.Value);
            await _db.HashSetAsync(pKey, "Alive", "0");
            await _pub.PublishAsync(Evt(lobbyId), $"Died:{killId}");
        }

        // TODO: handle inspect → send private message
        await CheckWin(lobbyId);
    }

    private async Task ResolveVote(Guid lobbyId)
    {
        var all = await _db.HashGetAllAsync(Votes(lobbyId));
        await _db.KeyDeleteAsync(Votes(lobbyId));
        if (all.Length == 0)
            return;

        var lynchedId = all.GroupBy(x => x.Value).OrderByDescending(g => g.Count()).First().Key;
        string pKey = PKey(Guid.Parse(lynchedId));
        await _db.HashSetAsync(pKey, "Alive", "0");
        await _pub.PublishAsync(Evt(lobbyId), $"Lynched:{lynchedId}");

        await CheckWin(lobbyId);
    }

    private async Task CheckWin(Guid lobbyId)
    {
        var lobby = await GetLobbyByIdAsync(lobbyId);
        int wolves = lobby.Players.Count(p => p.Role == Role.Werewolf && p.Alive);
        int village = lobby.Players.Count(p => p.Role != Role.Werewolf && p.Alive);

        string? winner =
            wolves == 0 ? "Villagers"
            : wolves >= village ? "Werewolves"
            : null;

        if (winner != null)
        {
            await SaveLobbyMeta(lobbyId, Phase.GameOver, lobby.DayCount, winner);
            await _pub.PublishAsync(Evt(lobbyId), $"GameOver:{winner}");
        }
    }

    private void AssignRoles(Lobby l)
    {
        var rnd = new Random();
        var shuffled = l.Players.OrderBy(_ => rnd.Next()).ToList();
        if (shuffled.Count < 4)
            return;

        shuffled[0].Role = Role.Werewolf;
        shuffled[1].Role = Role.Werewolf;
        shuffled[2].Role = Role.Seer;
        shuffled[3].Role = Role.Doctor;

        foreach (var p in shuffled)
            _db.HashSetAsync(PKey(p.Id), "Role", p.Role.ToString()).Wait();
    }

    /* ───────────────────────── Lobby load/save ────────────────────────── */

    public async Task<Lobby> GetLobbyByIdAsync(Guid lobbyId)
    {
        var players = new List<Player>();
        foreach (var pKey in await _db.SetMembersAsync(LSet(lobbyId)))
        {
            var h = await _db.HashGetAllAsync(pKey.ToString());
            players.Add(MapPlayer(h));
        }
        var meta = await LoadLobbyMeta(lobbyId);
        return new Lobby(lobbyId)
        {
            Players = players,
            Phase = meta.Phase,
            DayCount = meta.Day,
            Winner = meta.Winner,
        };
    }

    /* ───────────────────────── small meta struct ──────────────────────── */

    private record Meta(Phase Phase, int Day, string? Winner);

    private async Task SaveLobbyMeta(Guid id, Phase ph, int day, string? win) =>
        await _db.HashSetAsync(
            LMeta(id),
            new HashEntry[]
            {
                new("Phase", ph.ToString()),
                new("Day", day),
                new("Winner", win ?? ""),
            }
        );

    private async Task<Meta> LoadLobbyMeta(Guid id)
    {
        var h = await _db.HashGetAllAsync(LMeta(id));
        return h.Length == 0
            ? new Meta(Phase.Setup, 0, null)
            : new Meta(
                Enum.Parse<Phase>(h.First(e => e.Name == "Phase").Value),
                int.Parse(h.First(e => e.Name == "Day").Value),
                h.First(e => e.Name == "Winner").Value!
            );
    }

    /* ───────────────────────── key helpers ────────────────────────────── */

    static string PKey(Guid id) => $"player:{id}";

    static string LSet(Guid id) => $"lobby:{id}:players";

    static string LMeta(Guid id) => $"lobby:{id}:meta";

    static string Acts(Guid id) => $"lobby:{id}:acts";

    static string Votes(Guid id) => $"lobby:{id}:votes";

    static string Evt(Guid id) => $"lobby:{id}:events";

    /* ───────────────────────── mapping ────────────────────────────────── */

    private static Player MapPlayer(HashEntry[] h)
    {
        bool alive = h.Single(e => e.Name == "Alive").Value.ToBool();
        bool ready = h.Single(e => e.Name == "Ready").Value.ToBool();
        var role = Enum.Parse<Role>(h.Single(e => e.Name == "Role").Value);

        return new Player(
            Guid.Parse(h.Single(e => e.Name == "Id").Value),
            h.Single(e => e.Name == "Name").Value
        )
        {
            Alive = alive,
            Ready = ready,
            Role = role,
        };
    }

    /* ───────────────────────── JSON util ──────────────────────────────── */
    private static string Serialize<T>(T obj) => System.Text.Json.JsonSerializer.Serialize(obj);

    private static T Deserialize<T>(RedisValue v) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(v!)!;
}

static class RedisBool
{
    public static bool ToBool(this RedisValue v) =>
        v.HasValue && (v == "1" || v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

    public static RedisValue FromBool(bool b) => b ? "1" : "0";
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
