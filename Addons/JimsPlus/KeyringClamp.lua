-- JimsPlus KeyringClamp
-- Two client-side fixes for the keyring (container -2) on the 1.14 client + Kronos:
--   1. SIZE: the client reports the keyring as 32 slots, but Kronos only accepts 12; slots 13-32
--      bounce. Clamp GetContainerNumSlots(-2) down to 12 so the UI/addons only see the real slots.
--   2. FAMILY: the client reports the keyring's bag family as 0 (general) instead of 9 (keys-only),
--      so Bagnon's sort (Wildpants/api/sorting.lua) treats it as normal storage and loops forever
--      trying to move non-keys into it (server rejects WrongBagType; Bagnon retries). Force
--      GetContainerNumFreeSlots(-2) to report family 9 -- Bagnon's FitsIn then only admits keys
--      (GetItemFamily==256) into the keyring, so non-keys are never targeted at it.
-- The proxy separately unlocks any item dropped on a phantom slot. Clamps DOWN only.

local KEYRING_SIZE = 12
local KEYRING_FAMILY = 9
local KEYRING = (type(KEYRING_CONTAINER) == "number") and KEYRING_CONTAINER or -2

local function clamp(orig, bag, ...)
    local n = orig(bag, ...)
    if bag == KEYRING and type(n) == "number" and n > KEYRING_SIZE then
        return KEYRING_SIZE
    end
    return n
end

local function clampFree(orig, bag, ...)
    if bag == KEYRING then
        local free = orig(bag, ...)
        return free, KEYRING_FAMILY
    end
    return orig(bag, ...)
end

if C_Container and type(C_Container.GetContainerNumSlots) == "function" then
    local orig = C_Container.GetContainerNumSlots
    C_Container.GetContainerNumSlots = function(bag, ...) return clamp(orig, bag, ...) end
end

if type(GetContainerNumSlots) == "function" then
    local orig = GetContainerNumSlots
    GetContainerNumSlots = function(bag, ...) return clamp(orig, bag, ...) end
end

if C_Container and type(C_Container.GetContainerNumFreeSlots) == "function" then
    local orig = C_Container.GetContainerNumFreeSlots
    C_Container.GetContainerNumFreeSlots = function(bag, ...) return clampFree(orig, bag, ...) end
end

if type(GetContainerNumFreeSlots) == "function" then
    local orig = GetContainerNumFreeSlots
    GetContainerNumFreeSlots = function(bag, ...) return clampFree(orig, bag, ...) end
end
