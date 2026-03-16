#nullable enable

using lycanthrope.Interfaces;
using lycanthrope.Models;
using Microsoft.AspNetCore.SignalR;

namespace lycanthrope.Services;

public sealed class LobbyHub(IGameEngineService gameEngineService, ILogger<LobbyHub> logger) : Hub
{
    public const string LobbyUpdatedMethod = "LobbyUpdated";
    private const string IntentionalLeaveKey = "intentionalLeave";
    private const string PageTransitionKey = "pageTransition";

    public static string GroupName(Guid lobbyId) => $"lobby:{lobbyId:N}";

    public override async Task OnConnectedAsync()
    {
        if (!TryGetConnectionContext(out var lobbyId, out var playerId, out var playerName))
        {
            logger.LogWarning(
                "Rejected lobby connection {ConnectionId} due to missing query parameters.",
                Context.ConnectionId
            );
            throw new HubException("Missing lobby connection details.");
        }

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(lobbyId));
            await gameEngineService.AddPlayerToLobbyAsync(
                lobbyId,
                new Player(playerId, playerName)
            );
            await base.OnConnectedAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(lobbyId));
            logger.LogWarning(
                ex,
                "Unable to join lobby {LobbyId} for player {PlayerId}.",
                lobbyId,
                playerId
            );
            throw new HubException(ex.Message);
        }
    }

    public async Task LeaveLobby()
    {
        if (!TryGetConnectionContext(out var lobbyId, out var playerId, out _))
        {
            return;
        }

        Context.Items[IntentionalLeaveKey] = true;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(lobbyId));
        await gameEngineService.RemovePlayerFromLobbyAsync(lobbyId, playerId);
    }

    public Task PrepareForPageTransition()
    {
        Context.Items[PageTransitionKey] = true;
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var hasConnectionContext = TryGetConnectionContext(
            out var lobbyId,
            out var playerId,
            out _
        );

        if (hasConnectionContext)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(lobbyId));
        }

        if (!HasLeftIntentionally() && !IsChangingPages() && hasConnectionContext)
        {
            await gameEngineService.RemovePlayerFromLobbyAsync(lobbyId, playerId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private bool HasLeftIntentionally() =>
        Context.Items.TryGetValue(IntentionalLeaveKey, out var value) && value is true;

    private bool IsChangingPages() =>
        Context.Items.TryGetValue(PageTransitionKey, out var value) && value is true;

    private bool TryGetConnectionContext(out Guid lobbyId, out Guid playerId, out string playerName)
    {
        lobbyId = Guid.Empty;
        playerId = Guid.Empty;
        playerName = string.Empty;

        var request = Context.GetHttpContext()?.Request;
        if (request is null)
        {
            return false;
        }

        if (!Guid.TryParse(request.Query["lobbyId"], out lobbyId))
        {
            return false;
        }

        if (!Guid.TryParse(request.Query["playerId"], out playerId))
        {
            return false;
        }

        playerName = request.Query["playerName"].ToString();
        return !string.IsNullOrWhiteSpace(playerName);
    }
}
