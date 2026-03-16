#nullable enable

namespace lycanthrope.Interfaces;

public interface ILobbyNotificationService
{
    Task NotifyLobbyUpdatedAsync(Guid lobbyId);
}
