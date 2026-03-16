#nullable enable

namespace lycanthrope.Models
{
    public class Lobby(Guid id)
    {
        public Guid Id { get; set; } = id;

        public List<Player> Players { get; set; } = [];

        public int DayCount { get; set; }

        public Phase Phase { get; set; }

        public string? Winner { get; set; }

        public Guid? HostPlayerId { get; set; }
    }
}
