namespace lycanthrope.Models;

public class Player
{
    public Player() => Id = Guid.NewGuid();

    public Player(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; set; }

    public string Name { get; set; }
}
