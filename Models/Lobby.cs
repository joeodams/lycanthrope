namespace lycanthrope.Models
{
    public class Lobby(Guid id)
    {
        public Guid Id { get; set; } = id;

        public List<Player> Players { get; set; } = [];
    }
}
