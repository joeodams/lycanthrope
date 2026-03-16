#nullable enable

namespace lycanthrope.Models;

public class Player
{
    public Player()
    {
        Id = Guid.NewGuid();
        Name = string.Empty;
        JoinedAtUtc = DateTimeOffset.UtcNow;
    }

    public Player(Guid id, string name)
    {
        Id = id;
        Name = name;
        JoinedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; set; }

    public string Name { get; set; }

    public bool Ready { get; set; }

    public Role Role { get; set; }

    public bool Alive { get; set; }

    public bool IsBot { get; set; }

    public DateTimeOffset JoinedAtUtc { get; set; }
}
