-- JimsPlus BagnonSort
-- Two hooks into Bagnon's (Wildpants) client-side bag sorter. Bagnon's own files
-- are never modified, so this survives Bagnon updates, and both hooks no-op when
-- Bagnon isn't installed.
--
-- 1. LOOP BREAKER (always on): Bagnon's sorter has no retry cap or backoff — a
--    move that never takes effect is recomputed and re-sent every 50ms forever
--    (Wildpants/api/sorting.lua: Iterate -> Move -> Delay(0.05, 'Run')). Any move
--    the proxy or server keeps rejecting becomes endless "That item cannot go in
--    that container" spam until /reload. After 3 identical attempted moves in one
--    sort run, the item is blacklisted for the rest of the run (marked sorted and
--    made unstackable in the in-memory model) so the sort converges and finishes.
--
-- 2. CUSTOM SORT ORDER (Options toggle, default on): replaces the sort comparator
--    with a category ranking — permanent fixtures (hearthstone), then profession/
--    gathering tools, quest items, soulbound (BoP) gear, other gear, consumables,
--    everything else, junk last. Within a category Bagnon's original ordering
--    applies. Lower rank lands closer to backpack slot 1. The Sorting module is
--    shared, so the order also applies to Bagnon's bank (and guild bank) sort.
--
-- Note: Bagnon prefers the server-side SortBags() when the client provides it
-- (Wildpants/classes/inventory.lua, serverSort default true). 1.14.2 has no
-- SortBags, so the client-side sorter — and these hooks — always run for the
-- inventory; if a future client build adds SortBags, both hooks silently stop
-- applying to the inventory sort and this file needs revisiting.

local _, namespace = ...

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
-- quality, equip, bind — any of which may still be nil while item data loads).
-- "Soulbound gear" is approximated by BoP bind type: BoP items in bags are
-- necessarily soulbound; carried BoE gear ranks with other gear instead.
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

-- Item tables are rebuilt from scratch on every sort pass, so a rank stashed on
-- the table can't go stale; this keeps rank computation O(n) per pass instead of
-- O(n log n) comparator calls.
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
    if type(Sorting.Start) ~= "function" or type(Sorting.Move) ~= "function"
        or type(Sorting.GetSpaces) ~= "function" or type(Sorting.Rule) ~= "function" then
        return
    end
    Sorting.jpHooked = true

    ------------------------------------------------------------- loop breaker
    local attempts, blacklist = {}, {}

    local origStart = Sorting.Start
    Sorting.Start = function(self, ...)
        wipe(attempts)
        wipe(blacklist)
        return origStart(self, ...)
    end

    local origMove = Sorting.Move
    Sorting.Move = function(self, from, to)
        local id = from and from.item and from.item.id
        if not id then
            return origMove(self, from, to)
        end
        -- Deliberately no check of the DESTINATION against the blacklist: Iterate
        -- schedules its re-run Delay unconditionally after every attempted move, so
        -- silently skipping a move here would spin the sorter forever. A mover whose
        -- swap with a blacklisted occupant keeps failing blacklists itself via its
        -- own attempt count instead, which is bounded and terminates.
        if blacklist[id] then return end
        local sig = tostring(from.bag) .. ":" .. tostring(from.slot) .. ":"
            .. tostring(to.bag) .. ":" .. tostring(to.slot) .. ":" .. id
        if (attempts[sig] or 0) >= 3 then
            -- The exact same move was sent 3 times without taking effect: it is
            -- being rejected. Give up on this item for the rest of the run.
            blacklist[id] = true
            return
        end
        local moved = origMove(self, from, to)
        if moved then
            attempts[sig] = (attempts[sig] or 0) + 1
        end
        return moved
    end

    local origGetSpaces = Sorting.GetSpaces
    Sorting.GetSpaces = function(self, ...)
        local spaces = origGetSpaces(self, ...)
        if next(blacklist) and type(spaces) == "table" then
            for _, space in ipairs(spaces) do
                local item = space.item
                if item and item.id and blacklist[item.id] then
                    item.sorted = true -- skipped by every placement pass
                    item.stack = nil   -- and by the stack-merge pass
                end
            end
        end
        return spaces
    end

    ------------------------------------------------------------- custom order
    local origRule = Sorting.Rule
    Sorting.Rule = function(a, b)
        local db = namespace.db
        if db and db.bagSortOrder == false then
            return origRule(a, b)
        end
        local ra, rb = GetRank(a), GetRank(b)
        if ra ~= rb then
            return ra < rb
        end
        return origRule(a, b)
    end
end

-- PLAYER_LOGIN covers the normal case (fires after all non-LoD addons load);
-- the ADDON_LOADED fallback covers Bagnon being loaded late (load-on-demand
-- packaging or an addon manager enabling it mid-session).
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
