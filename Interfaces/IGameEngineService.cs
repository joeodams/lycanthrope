#nullable enable

using lycanthrope.Models;

namespace lycanthrope.Interfaces;

public interface IGameEngineService
{
    Task<Lobby> AddPlayerToLobbyAsync(Guid lobbyId, Player player);

    Task AdvancePhaseAsync(Guid lobbyId, Guid requestedByPlayerId);

    Task<Guid> CreateDemoGameAsync(Player player);

    Task FillSeatsAsync(Guid lobbyId, Guid requestedByPlayerId);

    Task<GameView> GetGameViewAsync(Guid lobbyId, Guid playerId);

    Task<Lobby> GetLobbyByIdAsync(Guid lobbyId);

    Task RemovePlayerFromLobbyAsync(Guid lobbyId, Guid playerId);

    Task StartGameAsync(Guid lobbyId, Guid requestedByPlayerId);

    Task SendChatMessageAsync(Guid lobbyId, Guid playerId, string message);

    Task SubmitNightActionAsync(Guid lobbyId, NightAction action);

    Task SubmitVoteAsync(Guid lobbyId, Guid voter, Guid target);

    Task TogglePlayerReadyAsync(Guid lobbyId, Guid playerId);
}
