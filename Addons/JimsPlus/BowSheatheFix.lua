local ADDON_NAME, namespace = ...

-- JimsProxy (bow sheathe fix): 1.14 client bug — with a bow equipped, the client's own
-- DELAYED post-combat auto-sheathe drops the 2H mainhand (and the quiver) from the back
-- until a manual unsheathe (guns and crossbows are unaffected). Field-observed: an EARLY
-- sheathe renders correctly every time (looting right after combat forces one and always
-- fixes it), while late repair nudges are unreliable. So this module mimics looting:
-- shortly after combat ends with bow + 2H and weapons still drawn, sheathe proactively —
-- the buggy auto-sheathe then never fires on a drawn weapon. If combat ends with weapons
-- already away (a possibly-bugged mid-fight sheathe), do an immediate draw+re-sheathe
-- nudge instead — timing matters, so it fires right at combat end.

local SHEATHED = 1 -- GetSheathState(): 1 = weapons away, 2 = melee drawn, 3 = ranged drawn
local PROACTIVE_DELAY = 5.0 -- seconds after combat before we sheathe ourselves
local TICK = 0.25 -- watch granularity: a client auto-sheathe inside the window is repaired within one tick

local watcher

local function Enabled()
    local db = namespace.db or JimsPlusDB or {}
    return db.bowSheatheFix ~= false
end

local function HasBowAnd2H()
    local _, class = UnitClass("player")
    if class ~= "HUNTER" then return false end -- warriors/rogues with a stat-stick bow: the nudge kept drawing it
    local ranged = GetInventoryItemID("player", 18)
    if not ranged then return false end
    local classID, subclassID = select(6, GetItemInfoInstant(ranged))
    if classID ~= 2 or subclassID ~= 2 then return false end -- bows only
    local main = GetInventoryItemID("player", 16)
    if not main then return false end
    local equipLoc = select(4, GetItemInfoInstant(main))
    return equipLoc == "INVTYPE_2HWEAPON" -- covers 2H axes/swords/maces, polearms, staves
end

local function StopWatch()
    if watcher then
        watcher:Cancel()
        watcher = nil
    end
end

-- Draw + re-sheathe repair for a sheathe the client already performed (possibly bugged).
-- Must run promptly after that sheathe — late nudges fail (field-observed).
local function RepairNudge()
    ToggleSheath()
    C_Timer.After(0.3, function()
        if not InCombatLockdown() and GetSheathState() ~= SHEATHED then
            ToggleSheath()
        end
    end)
end

local function OnCombatEnd()
    if not Enabled() then return end
    if not GetSheathState or not ToggleSheath then return end
    if not HasBowAnd2H() then return end

    if GetSheathState() == SHEATHED then
        -- Weapons already away: the bugged sheathe may have happened mid-fight.
        RepairNudge()
        return
    end

    -- Weapons still drawn: watch for up to PROACTIVE_DELAY. If the client's own (buggy)
    -- auto-sheathe fires inside the window, repair it within one tick — right away, per
    -- the field rule. If nothing happened by the deadline, sheathe deliberately ourselves
    -- (the same early sheathe looting forces, which always renders correctly).
    StopWatch()
    local elapsed = 0
    watcher = C_Timer.NewTicker(TICK, function()
        elapsed = elapsed + TICK
        if InCombatLockdown() then StopWatch() return end -- back in combat, next regen handles it
        if not Enabled() or not HasBowAnd2H() then StopWatch() return end
        if GetSheathState() == SHEATHED then
            StopWatch()
            RepairNudge()
        elseif elapsed >= PROACTIVE_DELAY then
            StopWatch()
            ToggleSheath()
        end
    end)
end

local f = CreateFrame("Frame")
f:RegisterEvent("PLAYER_REGEN_ENABLED")
f:SetScript("OnEvent", OnCombatEnd)
