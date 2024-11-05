using lycanthrope.Interfaces;
using lycanthrope.Models;
using Microsoft.AspNetCore.SignalR;

namespace lycanthrope.Services
{
    public class LobbyService : ILobbyService
    {
        private readonly List<Lobby> _lobbies = [];

        public LobbyService() { }

        public async Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player player)
        {
            var lobby = await GetOrCreateLobbyByIdAsync(lobbyId);

            // prevent duplicate players
            var playerExists = lobby.Players.Any(p => p.Id == player.Id);
            if (!playerExists)
            {
                lobby.Players.Add(player);
            }

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
            var lobby = _lobbies.FirstOrDefault(l => l.Id == lobbyId);
            if (lobby == null)
            {
                lobby = new Lobby(lobbyId);
                _lobbies.Add(lobby);
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
