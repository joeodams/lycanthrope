using System.Text.Json;
using lycanthrope.Interfaces;
using lycanthrope.Models;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace lycanthrope.Services
{
    public class LobbyService : ILobbyService
    {
        private readonly List<Lobby> _lobbies = [];

        public IDatabase Database { get; }

        public LobbyService(IDatabase database)
        {
            Database = database;

            // Database.KeyDelete("foo");
            // Database.

            // Console.WriteLine(Database.KeyExists("foo"));

            // Database.StringSet("foo", "bar");

            // var foo = Database.StringGet("foo");

            // Console.WriteLine(foo);
        }

        public async Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player player)
        {
            Console.WriteLine($"trying to add player {player.Name}");
            var lobby = await GetOrCreateLobbyByIdAsync(lobbyId);

            // prevent duplicate players
            var playerExists = lobby.Players.Any(p => p.Id == player.Id);
            if (!playerExists)
            {
                lobby.Players.Add(player);
            }

            var lobbyJson = JsonSerializer.Serialize(lobby);

            Console.WriteLine($"AddPlayer - lobby json: {lobbyJson}");

            Database.StringSet(lobbyId.ToString(), lobbyJson);

            return lobby;
        }

        public async Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId)
        {
            var lobby = await GetOrCreateLobbyByIdAsync(lobbyId);

            var player = lobby.Players.FirstOrDefault(p => p.Id == playerId);

            if (player != null)
            {
                lobby.Players.Remove(player);
            }
        }

        public Task<Lobby> GetOrCreateLobbyByIdAsync(Guid lobbyId)
        {
            Console.WriteLine("Trying to get lobby");
            var lobbyJson = Database.StringGet(lobbyId.ToString());
            // var lobby = _lobbies.FirstOrDefault(l => l.Id == lobbyId);
            Console.WriteLine($"Initial lobby value:   {lobbyJson}");

            Lobby lobby;

            if (lobbyJson == RedisValue.Null)
            {
                Console.WriteLine("Creating lobby");
                lobby = new Lobby(lobbyId);

                Database.StringSet(lobbyId.ToString(), JsonSerializer.Serialize(lobby));
                // _lobbies.Add(lobby);
            }
            else
            {
                Console.WriteLine("Deserialising lobby");

                lobby = JsonSerializer.Deserialize<Lobby>(lobbyJson);
            }
            return Task.FromResult(lobby);
        }

        public Task<Lobby> GetLobbyByIdAsync(Guid lobbyId)
        {
            var lobby = _lobbies.FirstOrDefault(l => l.Id == lobbyId);
            return Task.FromResult(lobby);
        }
    }
}
