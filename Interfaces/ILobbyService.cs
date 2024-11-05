using lycanthrope.Models;

namespace lycanthrope.Interfaces
{
    public interface ILobbyService
    {
        Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player player);

        Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId);

        Task<Lobby> GetOrCreateLobbyByIdAsync(Guid lobbyId);

        Task<Lobby> GetLobbyByIdAsync(Guid lobbyId);
    }
}
