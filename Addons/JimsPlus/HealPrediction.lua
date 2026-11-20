local ADDON_NAME, namespace = ...

-- JimsPlus incoming heals on the default unit frames (opt-in, one switch per kind of frame)
--
-- The 1.14.2 client's own frames never draw incoming heals. The raid frames carry the textures
-- for the bars but nothing sizes or shows them; the party, player and target frames have none.
-- This file draws the bars on Blizzard's raid frames, party frames (raid-style or not), player
-- frame and target frame, each behind its own switch: to the right of the health fill, the
-- player's own heal first and other healers' after it, for the part that fits into the
-- missing health.
--
-- Where the amounts come from, best source first:
--
-- 1. LibHealComm-4.0, when an addon on this client has loaded it (unit frame and heal addons
--    carry it). It knows the player's own heals and every heal another player's HealComm addon
--    announced, whether that healer is in view or not. The proxy translates the 1.12 form of
--    those announcements, so 1.12 healers are included.
--
-- 2. Without the library: the game's own number (UnitGetIncomingHeals). A vanilla server never
--    fills it, so the proxy is asked to add the amount a healer's addon announced to that
--    healer's cast, for healers in view. It sends the healer's cast start a second time to do
--    it, which puts a second "begins to cast" line in the combat log: JimsPlus cast bars know
--    to ignore it; other cast-bar addons that read the combat log restart that bar once. The
--    player's own heals have no amount in this mode.
--
-- A healer who runs no HealComm addon announces nothing, so no source has an amount for them.
--
-- Account-wide: one main switch, off by default, and under it one switch per kind of frame, on
-- by default; a kind is drawn while both are on. The proxy's flag only lasts a session, so the
-- choice is sent at every loading screen. Talks over the JP addon sideband (both ends ours).

local UnitGetIncomingHeals = UnitGetIncomingHeals
local UnitHealth, UnitHealthMax, UnitGUID = UnitHealth, UnitHealthMax, UnitGUID
local UnitExists, UnitIsDeadOrGhost, UnitIsUnit = UnitExists, UnitIsDeadOrGhost, UnitIsUnit

-- Each kind of frame keeps its own health fill; the bars are tinted with the stock colours for
-- the player's and for another player's incoming heal.
local RAID_TEXTURE = "Interface\\RaidFrame\\Raid-Bar-Hp-Fill"
local UNIT_TEXTURE = "Interface\\TargetingFrame\\UI-StatusBar"
local MY_R, MY_G, MY_B = 0.0, 0.827, 0.765
local OTHER_R, OTHER_G, OTHER_B = 0.0, 0.631, 0.557

-- LibHealComm-4.0's flag for heals with a cast: direct heals and channels, not HoT ticks.
local CASTED_HEALS = 0x03
local LIB_EVENTS = {
    "HealComm_HealStarted", "HealComm_HealStopped", "HealComm_HealDelayed",
    "HealComm_HealUpdated", "HealComm_ModifierChanged", "HealComm_GUIDDisappeared",
}

-- Blizzard's raid frames (CompactRaidFrame<n>, CompactRaidGroup<g>Member<m>) and raid-style
-- party frames (CompactPartyFrameMember<n>) are created on demand and reused, so the list is
-- rebuilt on every roster change.
local RAID_GROUPS, GROUP_SIZE = 8, 5
local PARTY_MEMBERS = 4

-- The main switch and the four frame switches under it, and where each is saved.
local SHOW_KEY = "healShow"
local KINDS = { "raid", "party", "player", "target" }
local DB_KEYS = { raid = "healRaid", party = "healParty", player = "healPlayer", target = "healTarget" }
local KIND_NAMES = {
    raid = "raid frames", party = "party frames", player = "the player frame", target = "the target frame",
}

local function DB()
    return namespace.db or JimsPlusDB
end

local function IsShown()
    local db = DB()
    return db and db[SHOW_KEY] and true or false
end

local function IsTicked(kind)
    local db = DB()
    return db and db[DB_KEYS[kind]] and true or false
end

-- A kind of frame is drawn while the main switch is on and its own switch is ticked.
local function IsOn(kind)
    return IsShown() and IsTicked(kind)
end

local function AnyOn()
    for _, kind in ipairs(KINDS) do
        if IsOn(kind) then return true end
    end
    return false
end

-- One entry per frame that can show the bars:
--   frame, healthBar, mine, other (the two textures), texture (their fill),
--   unit (fixed unit, for the party, player and target frames; raid frames name theirs themselves)
local targets = {}
local addedBars = {} -- [health bar] = { mine = texture, other = texture }, made once
local styled = setmetatable({}, { __mode = "k" }) -- [texture] = true once it has a fill
local shown = setmetatable({}, { __mode = "k" })  -- [texture] = true while we show it
local drawing = false
local lib            -- LibHealComm-4.0, once an addon has loaded it
local libOwner = {}  -- identity for the library's callbacks
local playerGUID

local function FindLibrary()
    if not lib then
        local stub = _G.LibStub
        lib = stub and stub("LibHealComm-4.0", true) or nil
    end
    return lib
end

local function AddCompact(frame)
    if frame and frame.myHealPrediction and frame.otherHealPrediction and frame.healthBar then
        targets[#targets + 1] = {
            frame = frame, healthBar = frame.healthBar,
            mine = frame.myHealPrediction, other = frame.otherHealPrediction,
            texture = RAID_TEXTURE,
        }
    end
end

-- The party, player and target frames have no textures for this, so two are added to the
-- health bar. They sit on the bar itself, under the frame's border art, like the health fill.
local function AddUnitFrame(frame, healthBar, unit)
    if not frame or not healthBar then return end
    local bars = addedBars[healthBar]
    if not bars then
        bars = {
            mine = healthBar:CreateTexture(nil, "ARTWORK", nil, 1),
            other = healthBar:CreateTexture(nil, "ARTWORK", nil, 1),
        }
        bars.mine:Hide()
        bars.other:Hide()
        addedBars[healthBar] = bars
    end
    targets[#targets + 1] = {
        frame = frame, healthBar = healthBar,
        mine = bars.mine, other = bars.other,
        texture = UNIT_TEXTURE, unit = unit,
    }
end

-- Rebuilds the list from the switches that are on. Raid-style party frames belong to both
-- the raid and the party switch: they are party frames that look like raid frames.
local function Scan()
    wipe(targets)
    local raid, party = IsOn("raid"), IsOn("party")
    if raid then
        for i = 1, 40 do
            AddCompact(_G["CompactRaidFrame" .. i])
        end
        for group = 1, RAID_GROUPS do
            for member = 1, GROUP_SIZE do
                AddCompact(_G["CompactRaidGroup" .. group .. "Member" .. member])
            end
        end
    end
    if raid or party then
        for i = 1, 5 do
            AddCompact(_G["CompactPartyFrameMember" .. i])
        end
    end
    if party then
        for index = 1, PARTY_MEMBERS do
            AddUnitFrame(_G["PartyMemberFrame" .. index], _G["PartyMemberFrame" .. index .. "HealthBar"], "party" .. index)
        end
    end
    if IsOn("player") then
        AddUnitFrame(_G.PlayerFrame, _G.PlayerFrameHealthBar, "player")
    end
    if IsOn("target") then
        AddUnitFrame(_G.TargetFrame, _G.TargetFrameHealthBar, "target")
    end
end

local function HideBar(bar)
    if shown[bar] then
        bar:Hide()
        shown[bar] = nil
    end
end

local function HideBars(target)
    HideBar(target.mine)
    HideBar(target.other)
end

-- Puts `bar` to the right of `anchor`, `amount` health wide. Returns the bar, to stack the next on.
local function PlaceBar(bar, anchor, amount, maxHealth, width, texture, r, g, b)
    if not styled[bar] then
        bar:SetTexture(texture)
        bar:SetVertexColor(r, g, b)
        styled[bar] = true
    end
    bar:ClearAllPoints()
    bar:SetPoint("TOPLEFT", anchor, "TOPRIGHT", 0, 0)
    bar:SetPoint("BOTTOMLEFT", anchor, "BOTTOMRIGHT", 0, 0)
    bar:SetWidth(amount / maxHealth * width)
    bar:Show()
    shown[bar] = true
    return bar
end

-- The player's own incoming heal on `unit`, and everyone else's.
local function Amounts(unit)
    if lib then
        local guid = UnitGUID(unit)
        if not guid then return 0, 0 end
        local modifier = lib:GetHealModifier(guid) or 1
        local mine = playerGUID and lib:GetHealAmount(guid, CASTED_HEALS, nil, playerGUID) or 0
        local others = lib:GetOthersHealAmount(guid, CASTED_HEALS) or 0
        return mine * modifier, others * modifier
    end
    return 0, UnitGetIncomingHeals(unit) or 0
end

local function UnitOf(target)
    return target.unit or target.frame.displayedUnit or target.frame.unit
end

local function UpdateTarget(target)
    local unit = UnitOf(target)
    if not unit or not target.frame:IsShown() or not UnitExists(unit) or UnitIsDeadOrGhost(unit) then
        return HideBars(target)
    end

    local maxHealth = UnitHealthMax(unit) or 0
    local width = target.healthBar:GetWidth() or 0
    local fill = target.healthBar:GetStatusBarTexture()
    if maxHealth <= 0 or width <= 0 or not fill then
        return HideBars(target)
    end

    -- Only the part that fits: the frames have no room past full health.
    local room = maxHealth - (UnitHealth(unit) or 0)
    local mine, others = Amounts(unit)
    if mine > room then mine = room end
    room = room - mine
    if others > room then others = room end

    local anchor = fill
    if mine > 0 then
        anchor = PlaceBar(target.mine, anchor, mine, maxHealth, width, target.texture, MY_R, MY_G, MY_B)
    else
        HideBar(target.mine)
    end
    if others > 0 then
        PlaceBar(target.other, anchor, others, maxHealth, width, target.texture, OTHER_R, OTHER_G, OTHER_B)
    else
        HideBar(target.other)
    end
end

local function UpdateAll()
    for i = 1, #targets do
        UpdateTarget(targets[i])
    end
end

local function UpdateUnit(unit)
    if not unit then return end
    for i = 1, #targets do
        local target = targets[i]
        local targetUnit = UnitOf(target)
        if targetUnit and (targetUnit == unit or UnitIsUnit(targetUnit, unit)) then
            UpdateTarget(target)
        end
    end
end

local ListenToLibrary -- defined below

local function Rescan()
    if not drawing then return end
    ListenToLibrary()
    Scan()
    UpdateAll()
end

local UNIT_EVENTS = { "UNIT_HEAL_PREDICTION", "UNIT_HEALTH", "UNIT_HEALTH_FREQUENT", "UNIT_MAXHEALTH" }
local ROSTER_EVENTS = { "GROUP_ROSTER_UPDATE", "PLAYER_REGEN_ENABLED", "PLAYER_TARGET_CHANGED" }

local drawFrame = CreateFrame("Frame")
drawFrame:SetScript("OnEvent", function(_, event, unit)
    if event == "GROUP_ROSTER_UPDATE" then
        -- Blizzard reassigns its frames on the same event; look again once it has.
        C_Timer.After(0.2, Rescan)
    elseif event == "PLAYER_REGEN_ENABLED" then
        -- Frames Blizzard could not create or reassign in combat appear now.
        C_Timer.After(0.2, Rescan)
    elseif event == "PLAYER_TARGET_CHANGED" then
        UpdateUnit("target")
    else
        UpdateUnit(unit)
    end
end)

-- The library says a heal started, stopped or changed. It names the targets by GUID; the
-- frames are few, so all of them are refreshed.
local function OnLibraryEvent()
    if drawing then UpdateAll() end
end

local listening = false
function ListenToLibrary()
    if listening or not FindLibrary() then return end
    listening = true
    for _, event in ipairs(LIB_EVENTS) do
        pcall(lib.RegisterCallback, libOwner, event, OnLibraryEvent)
    end
end

local function SetDrawing(on)
    on = on and true or false
    if on == drawing then
        if on then Rescan() end
        return
    end
    drawing = on
    if on then
        playerGUID = UnitGUID("player")
        for _, event in ipairs(UNIT_EVENTS) do
            -- pcall: an event name this client does not know raises an error.
            pcall(drawFrame.RegisterEvent, drawFrame, event)
        end
        for _, event in ipairs(ROSTER_EVENTS) do
            drawFrame:RegisterEvent(event)
        end
        ListenToLibrary()
        Scan()
        UpdateAll()
    else
        drawFrame:UnregisterAllEvents()
        if listening then
            listening = false
            for _, event in ipairs(LIB_EVENTS) do
                pcall(lib.UnregisterCallback, libOwner, event)
            end
        end
        for i = 1, #targets do
            HideBars(targets[i])
        end
        wipe(targets)
    end
end

-- The proxy's amount is only needed when no library on this client has the heals already;
-- asking for it then would cost other cast-bar addons a bar restart for nothing.
local function Send(on)
    if C_ChatInfo and C_ChatInfo.SendAddonMessage then
        local wanted = on and not FindLibrary()
        C_ChatInfo.SendAddonMessage("JP", "H\tAMOUNT\t" .. (wanted and "1" or "0"), "WHISPER", UnitName("player"))
    end
end

-- Applies the switches: hides what is showing, then draws again for the kinds that are on.
local function Apply()
    for i = 1, #targets do
        HideBars(targets[i])
    end
    SetDrawing(AnyOn())
end

-- The main switch.
function namespace:IsIncomingHealsShown()
    return IsShown()
end

-- A frame switch as ticked, whatever the main switch says (the settings page shows it greyed).
function namespace:IsIncomingHealsKindTicked(kind)
    return IsTicked(kind)
end

-- The main switch; /jp heals flips it.
function namespace:SetIncomingHealsShown(on)
    on = on and true or false
    local db = DB()
    if db then db[SHOW_KEY] = on end
    Send(AnyOn())
    Apply()
    print("|cFF00FF00[JimsPlus]|r Incoming heals on the default Blizzard frames: " ..
        (on and "|cFFFFD100on|r." or "|cFF00FF00off|r."))
end

-- One frame switch under the main one.
function namespace:SetIncomingHeals(kind, on)
    local key = DB_KEYS[kind]
    if not key then return end
    on = on and true or false
    local db = DB()
    if db then db[key] = on end
    Send(AnyOn())
    Apply()
    print("|cFF00FF00[JimsPlus]|r Incoming heals on " .. KIND_NAMES[kind] .. ": " ..
        (on and "|cFFFFD100on|r." or "|cFF00FF00off|r."))
end

local f = CreateFrame("Frame")
f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:SetScript("OnEvent", function()
    -- The proxy's flag is per session and starts off: send the saved choice every time.
    local on = AnyOn()
    Send(on)
    SetDrawing(on)
    if on then
        -- The raid frames are built a moment after the loading screen ends.
        C_Timer.After(1, Rescan)
    end
end)
