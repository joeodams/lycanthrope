using lycanthrope.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace lycanthrope.Services
{
    // [Authorize]
    public class LobbyHub : Hub
    {
        private readonly ILobbyService lobbyService;

        public LobbyHub(ILobbyService lobbyService)
        {
            this.lobbyService = lobbyService;
        }

        // Broadcast when a player joins
        public async Task PlayerJoined(string lobbyId, string playerName)
        {
            await Clients.Group(lobbyId).SendAsync("ReceivePlayerJoined", playerName);
        }

        // On connect logic
        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext().Request;

            string lobbyId = Context.GetHttpContext().Request.Query["lobbyId"];
            await Groups.AddToGroupAsync(Context.ConnectionId, lobbyId);
            await base.OnConnectedAsync();
        }

        // Remove users from the group when they disconnect
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            string lobbyId = Context.GetHttpContext().Request.Query["lobbyId"];
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, lobbyId);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
