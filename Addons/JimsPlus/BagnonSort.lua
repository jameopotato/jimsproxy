-- JimsPlus BagnonSort
-- Paces Bagnon's (Wildpants) client-side bag sorter to dodge Kronos's item-move
-- anti-flood. Bagnon's Sort:Iterate fires every needed swap at once (~16 in <20ms);
-- the vmangos/Kronos server throttles that burst, bounces the overflow with
-- SMSG_INVENTORY_CHANGE_FAILURE, and Bagnon retries the identical batch -- so the
-- sort either crawls or never finishes (same anti-flood family as the over-speed-ping
-- guard that forces the single login ping). Throttling to one swap per PACE_MS lets
-- each land first try, so the sort completes cleanly -- just a couple seconds slower.
-- Bagnon's own files are never touched, and the hooks no-op when Bagnon isn't loaded.
--
-- Also carries an OPT-IN custom sort order (Options toggle, default OFF): a category
-- ranking. Parked off until it's revalidated against the paced sorter.

local _, namespace = ...

-- Sort-move interval (ms), pinned to Kronos's item-move anti-flood limit: one swap per
-- PACE_MS lands first try instead of being bounced in a burst. 30ms is the measured safe
-- floor (was a 30-200ms Options slider during tuning; now fixed).
local PACE_MS = 30

local FIXTURES = {
    [6948] = true, -- Hearthstone
}

local TOOLS = {
    [2901]  = true, -- Mining Pick
    [5956]  = true, -- Blacksmith Hammer
    [7005]  = true, -- Skinning Knife
    [4471]  = true, -- Flint and Tinder
    [6219]  = true, -- Arclight Spanner
    [10498] = true, -- Gyromatic Micro-Adjustor
    [6218]  = true, -- Runed Copper Rod
    [6339]  = true, -- Runed Silver Rod
    [11130] = true, -- Runed Golden Rod
    [11145] = true, -- Runed Truesilver Rod
    [16207] = true, -- Runed Arcanite Rod
    [9149]  = true, -- Philosopher's Stone
    [15846] = true, -- Salt Shaker
    [6256]  = true, -- Fishing Pole
    [6365]  = true, -- Strong Fishing Pole
    [6366]  = true, -- Darkwood Fishing Pole
    [6367]  = true, -- Big Iron Fishing Pole
    [12225] = true, -- Blump Family Fishing Pole
    [19022] = true, -- Nat Pagle's Extreme Angler FC-5000
    [19970] = true, -- Arcanite Fishing Pole
}

local QUESTITEM = (Enum and Enum.ItemClass and Enum.ItemClass.Questitem) or 12
local CONSUMABLE = (Enum and Enum.ItemClass and Enum.ItemClass.Consumable) or 0

-- Category rank for one of Bagnon's in-memory item tables (fields id, class,
-- quality, equip, bind -- any may still be nil while item data loads). "Soulbound
-- gear" is approximated by BoP bind type.
local function Rank(item)
    local id = item.id
    if FIXTURES[id] then return 0 end
    if TOOLS[id] then return 1 end
    if item.quality == 0 then return 7 end                   -- junk last
    if item.class == QUESTITEM then return 2 end
    local equip = item.equip
    if equip and equip ~= "" and equip ~= "INVTYPE_BAG" then -- equippable gear
        return item.bind == 1 and 3 or 4                     -- soulbound (BoP) first
    end
    if item.class == CONSUMABLE then return 5 end
    return 6
end

-- Item tables are rebuilt each sort pass, so a stashed rank can't go stale; keeps
-- rank computation O(n) per pass instead of O(n log n) comparator calls.
local function GetRank(item)
    local r = item.jpSortRank
    if r == nil then
        r = Rank(item)
        item.jpSortRank = r
    end
    return r
end

local function Hook()
    local Sorting = Bagnon and Bagnon.Sorting
    if not Sorting or Sorting.jpHooked then return end
    if type(Sorting.Move) ~= "function" or type(Sorting.Rule) ~= "function" then
        return
    end
    Sorting.jpHooked = true

    --------------------------------------------------------- anti-flood pacer
    local lastMoveMs = 0
    local origMove = Sorting.Move
    Sorting.Move = function(self, from, to)
        local now = GetTime() * 1000
        if now - lastMoveMs < PACE_MS then
            -- Too soon -- skip this swap. Bagnon's Iterate always re-schedules its
            -- Delay(0.05, 'Run'), so the same move is retried on the next pass; we're
            -- only metering how fast swaps actually reach the server. A skipped move
            -- sets no locks (origMove not called), so the slots stay free to retry.
            return
        end
        local moved = origMove(self, from, to)
        if moved then
            lastMoveMs = now
        end
        return moved
    end

    --------------------------------------------------------- custom order (opt-in, default OFF)
    local origRule = Sorting.Rule
    Sorting.Rule = function(a, b)
        local db = namespace.db
        if not (db and db.bagSortOrder) then
            return origRule(a, b)
        end
        local ra, rb = GetRank(a), GetRank(b)
        if ra ~= rb then
            return ra < rb
        end
        return origRule(a, b)
    end

    --------------------------------------------------------- keyring exclusion
    -- Drop the keyring (container -2) from the bag set before every sort. Bagnon's
    -- GetBagInfo only tags the keyring bag-family 9 (keys-only) when the client's
    -- KEYRING_CONTAINER constant matches; on 1.14 it falls through to family 0, so the
    -- sorter sees the keyring as general storage and moves the first non-key (e.g. Gold
    -- Ore) into keyring slot 1. Kronos rejects a non-key in the keyring (keys-only) and
    -- Bagnon retries the identical CMSG_SWAP_ITEM every 50ms forever -- the sort never
    -- finishes. KeyringClamp's family override can't help here: it hooks
    -- GetContainerNumFreeSlots, which GetBagInfo doesn't read for family. Keys never need
    -- sorting, so excluding the keyring makes a keyring-slot target impossible. Builds a
    -- new list (never mutates the frame's self.Bags, which still drives the display).
    if type(Sorting.Start) == "function" then
        local KEYRING = (type(KEYRING_CONTAINER) == "number") and KEYRING_CONTAINER or -2
        local origStart = Sorting.Start
        Sorting.Start = function(self, owner, bags)
            if type(bags) == "table" then
                local filtered = {}
                for _, bag in pairs(bags) do
                    if bag ~= KEYRING then
                        filtered[#filtered + 1] = bag
                    end
                end
                bags = filtered
            end
            return origStart(self, owner, bags)
        end
    end
end

-- PLAYER_LOGIN covers the normal case (after non-LoD addons load); ADDON_LOADED
-- covers Bagnon being loaded late.
local f = CreateFrame("Frame")
f:RegisterEvent("PLAYER_LOGIN")
f:RegisterEvent("ADDON_LOADED")
f:SetScript("OnEvent", function(self, event, name)
    if event == "ADDON_LOADED" and name ~= "Bagnon" then return end
    Hook()
    if Bagnon and Bagnon.Sorting and Bagnon.Sorting.jpHooked then
        self:UnregisterAllEvents()
    end
end)
