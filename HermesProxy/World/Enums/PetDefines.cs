using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HermesProxy.World.Enums;

public enum PetTalk
{
    SpecialSpell = 0,
    Attack = 1
}

public enum CommandStates
{
    Stay = 0,
    Follow = 1,
    Attack = 2,
    Abandon = 3,
    MoveTo = 4
}

[Flags]
public enum PetModeFlags
{
    Unknown1       = 0x0010000,
    Unknown2       = 0x0020000,
    Unknown3       = 0x0040000,
    Unknown4       = 0x0080000,
    Unknown5       = 0x0100000,
    Unknown6       = 0x0200000,
    Unknown7       = 0x0400000,
    Unknown8       = 0x0800000,
    Unknown9       = 0x1000000,
    Unknown10      = 0x2000000,
    Unknown11      = 0x4000000,
    DisableActions = 0x8000000
}

public enum PetNameInvalidReason
{
    // custom, not sent
    Success = 0,

    Invalid = 1,
    NoName = 2,
    TooShort = 3,
    TooLong = 4,
    MixedLanguages = 6,
    Profane = 7,
    Reserved = 8,
    ThreeConsecutive = 11,
    InvalidSpace = 12,
    ConsecutiveSpaces = 13,
    RussianConsecutiveSilentCharacters = 14,
    RussianSilentCharacterAtBeginningOrEnd = 15,
    DeclensionDoesntMatchBaseName = 16
}

// JimsProxy: pet action-bar slot types. Every SMSG_PET_SPELLS button and every
// CMSG_PET_SET_ACTION value is packed as (action | type << 24); the layout and
// the values are the same on 1.12 and 1.14 (TrinityCore ActiveStates).
public enum ActiveStates : byte
{
    Decide   = 0x00,  // custom: the client decides on use
    Passive  = 0x01,  // passive ability, or an empty slot (action 0)
    Reaction = 0x06,  // Aggressive / Defensive / Passive stance (ReactStates)
    Command  = 0x07,  // Attack / Follow / Stay (CommandStates)
    Disabled = 0x81,  // castable, autocast off
    Enabled  = 0xC1   // castable, autocast on
}
