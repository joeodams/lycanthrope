#nullable enable

namespace lycanthrope.Models;

public sealed class GameView
{
    public Lobby Lobby { get; set; } = null!;

    public Player CurrentPlayer { get; set; } = null!;

    public NightAction? SubmittedNightAction { get; set; }

    public Guid? SubmittedVoteTargetId { get; set; }

    public string PrivateNote { get; set; } = string.Empty;

    public int RequiredNightActions { get; set; }

    public int SubmittedNightActions { get; set; }

    public int RequiredVotes { get; set; }

    public int SubmittedVotes { get; set; }
}
