#nullable enable

namespace lycanthrope.Models;

public enum Role
{
    Villager,
    Werewolf,
    Seer,
    Doctor,
}

public enum Phase
{
    Setup,
    Night,
    Dawn,
    Day,
    GameOver,
}

public enum NightAct
{
    Kill,
    Protect,
    Inspect,
}

public record NightAction(Guid ActorId, NightAct Type, Guid? TargetId);
