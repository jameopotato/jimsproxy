using System;
using Framework.Util;

namespace HermesProxy.World.Enums;

[Flags]
public enum GameObjectFlagsLegacy : uint
{
    InUse           = 0x0001,               // disables interaction while animated
    Locked          = 0x0002,               // requires key/spell/event to open; tooltip shows "Locked"
    InteractCond    = 0x0004,               // condition required to interact
    Transport       = 0x0008,               // any kind of transport (elevator, boat)
    NoInteract      = 0x0010,               // players cannot right-click; spell-cursor OPEN_LOCK still permitted in vanilla
    Nodespawn       = 0x0020,               // never despawn (doors etc.)
    Triggered       = 0x0040                // typically summoned, triggered by spell or event; no modern equivalent
};

[Flags]
public enum GameObjectFlagsModern : uint
{
    InUse           = 0x0001,
    Locked          = 0x0002,
    InteractCond    = 0x0004,
    Transport       = 0x0008,
    NotSelectable   = 0x0010,               // not selectable EVEN IN GM MODE; blocks spell cursor too
    Nodespawn       = 0x0020,
    AiObstacle      = 0x0040,               // registers in client AIObstacleMgr; vanilla bit 0x40 means TRIGGERED, not this
    FreezeAnimation = 0x0080
};

public static class GameObjectFlagsExtensions
{
    // Translate vanilla GameObjectFlags to modern. Same-name bits (InUse, Locked, InteractCond,
    // Transport, Nodespawn) map by CastFlags<>; the differing bits get explicit handling.
    public static GameObjectFlagsModern ToModern(this GameObjectFlagsLegacy legacy)
    {
        var modern = legacy.CastFlags<GameObjectFlagsModern>();

        if (legacy.HasFlag(GameObjectFlagsLegacy.NoInteract))
            modern |= GameObjectFlagsModern.NotSelectable;

        return modern;
    }
}
