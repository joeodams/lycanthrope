#nullable enable

using lycanthrope.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace lycanthrope.Services;

public sealed class SignalRLobbyNotificationService(IHubContext<LobbyHub> hubContext)
    : ILobbyNotificationService
{
    public Task NotifyLobbyUpdatedAsync(Guid lobbyId) =>
        hubContext
            .Clients.Group(LobbyHub.GroupName(lobbyId))
            .SendAsync(LobbyHub.LobbyUpdatedMethod);
}
