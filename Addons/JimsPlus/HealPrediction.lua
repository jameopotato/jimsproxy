local ADDON_NAME, namespace = ...

-- JimsPlus incoming heals on raid frames (opt-in)
--
-- Two halves, both needed:
--
-- 1. The amount. A vanilla server never tells the client how much a heal will heal, so the
--    game's own incoming-heal number (UnitGetIncomingHeals) is empty for another player's heal.
--    With this on, the proxy adds the amount the healer's HealComm addon announced to that
--    healer's cast, for healers in view. It sends the healer's cast start a second time to do
--    it, which puts a second "begins to cast" line in the combat log: JimsPlus cast bars know to
--    ignore it; other cast-bar addons that read the combat log restart that bar once.
--
-- 2. The drawing. The 1.14.2 client's raid frames carry the textures for an incoming-heal bar
--    but nothing sizes or shows them, and the frames don't listen for UNIT_HEAL_PREDICTION. So
--    this file draws the bar: on Blizzard's raid frames and raid-style party frames, to the
--    right of the health fill, for the part of the heal that fits into the missing health.
--
-- Only other players' heals get an amount this way. The player's own heals have none.
--
-- Account-wide, off by default, and sent to the proxy at every loading screen because the
-- proxy's flag only lasts a session. Talks over the JP addon sideband (both ends ours).

local UnitGetIncomingHeals = UnitGetIncomingHeals
local UnitHealth, UnitHealthMax = UnitHealth, UnitHealthMax
local UnitExists, UnitIsDeadOrGhost, UnitIsUnit = UnitExists, UnitIsDeadOrGhost, UnitIsUnit

-- The raid frames' own health fill, tinted with the stock colour for another player's
-- incoming heal.
local BAR_TEXTURE = "Interface\\RaidFrame\\Raid-Bar-Hp-Fill"
local BAR_R, BAR_G, BAR_B = 0.0, 0.631, 0.557

-- Blizzard names its raid and raid-style party frames this way; they are created on demand and
-- reused, so the list is rebuilt on every roster change.
local FRAME_PATTERNS = {
    { "CompactRaidFrame%d", 40 },
    { "CompactPartyFrameMember%d", 5 },
}
local RAID_GROUPS, GROUP_SIZE = 8, 5

local frames = {}   -- array of the compact frames that exist right now
local styled = setmetatable({}, { __mode = "k" }) -- [frame] = true once its bar has a texture
local shown = setmetatable({}, { __mode = "k" })  -- [frame] = true while we show its bar
local drawing = false

local function Scan()
    wipe(frames)
    for _, pattern in ipairs(FRAME_PATTERNS) do
        for i = 1, pattern[2] do
            local frame = _G[pattern[1]:format(i)]
            if frame and frame.otherHealPrediction and frame.healthBar then
                frames[#frames + 1] = frame
            end
        end
    end
    for group = 1, RAID_GROUPS do
        for member = 1, GROUP_SIZE do
            local frame = _G["CompactRaidGroup" .. group .. "Member" .. member]
            if frame and frame.otherHealPrediction and frame.healthBar then
                frames[#frames + 1] = frame
            end
        end
    end
end

local function HideBar(frame)
    if shown[frame] then
        frame.otherHealPrediction:Hide()
        shown[frame] = nil
    end
end

local function UpdateFrame(frame)
    local unit = frame.displayedUnit or frame.unit
    if not unit or not frame:IsShown() or not UnitExists(unit) or UnitIsDeadOrGhost(unit) then
        return HideBar(frame)
    end

    local incoming = UnitGetIncomingHeals(unit) or 0
    local maxHealth = UnitHealthMax(unit) or 0
    if incoming <= 0 or maxHealth <= 0 then
        return HideBar(frame)
    end

    -- Only the part that fits: a raid frame has no room past full health.
    local missing = maxHealth - (UnitHealth(unit) or 0)
    if incoming > missing then incoming = missing end
    local width = frame.healthBar:GetWidth() or 0
    if incoming <= 0 or width <= 0 then
        return HideBar(frame)
    end

    local bar = frame.otherHealPrediction
    if not styled[frame] then
        bar:SetTexture(BAR_TEXTURE)
        bar:SetVertexColor(BAR_R, BAR_G, BAR_B)
        styled[frame] = true
    end

    local fill = frame.healthBar:GetStatusBarTexture()
    if not fill then
        return HideBar(frame)
    end
    bar:ClearAllPoints()
    bar:SetPoint("TOPLEFT", fill, "TOPRIGHT", 0, 0)
    bar:SetPoint("BOTTOMLEFT", fill, "BOTTOMRIGHT", 0, 0)
    bar:SetWidth(incoming / maxHealth * width)
    bar:Show()
    shown[frame] = true
end

local function UpdateAll()
    for i = 1, #frames do
        UpdateFrame(frames[i])
    end
end

local function UpdateUnit(unit)
    if not unit then return end
    for i = 1, #frames do
        local frame = frames[i]
        local frameUnit = frame.displayedUnit or frame.unit
        if frameUnit and (frameUnit == unit or UnitIsUnit(frameUnit, unit)) then
            UpdateFrame(frame)
        end
    end
end

local function Rescan()
    if not drawing then return end
    Scan()
    UpdateAll()
end

local UNIT_EVENTS = { "UNIT_HEAL_PREDICTION", "UNIT_HEALTH", "UNIT_HEALTH_FREQUENT", "UNIT_MAXHEALTH" }
local ROSTER_EVENTS = { "GROUP_ROSTER_UPDATE", "PLAYER_REGEN_ENABLED" }

local drawFrame = CreateFrame("Frame")
drawFrame:SetScript("OnEvent", function(_, event, unit)
    if event == "GROUP_ROSTER_UPDATE" then
        -- Blizzard reassigns its frames on the same event; look again once it has.
        C_Timer.After(0.2, Rescan)
    elseif event == "PLAYER_REGEN_ENABLED" then
        -- Frames Blizzard could not create or reassign in combat appear now.
        C_Timer.After(0.2, Rescan)
    else
        UpdateUnit(unit)
    end
end)

local function SetDrawing(on)
    on = on and true or false
    if on == drawing then
        if on then Rescan() end
        return
    end
    drawing = on
    if on then
        for _, event in ipairs(UNIT_EVENTS) do
            -- pcall: an event name this client does not know raises an error.
            pcall(drawFrame.RegisterEvent, drawFrame, event)
        end
        for _, event in ipairs(ROSTER_EVENTS) do
            drawFrame:RegisterEvent(event)
        end
        Scan()
        UpdateAll()
    else
        drawFrame:UnregisterAllEvents()
        for i = 1, #frames do
            HideBar(frames[i])
        end
        wipe(frames)
    end
end

local function Send(on)
    if C_ChatInfo and C_ChatInfo.SendAddonMessage then
        C_ChatInfo.SendAddonMessage("JP", "H\tAMOUNT\t" .. (on and "1" or "0"), "WHISPER", UnitName("player"))
    end
end

function namespace:IsNativeHealAmountOn()
    local db = self.db or JimsPlusDB
    return db and db.nativeHealAmount and true or false
end

function namespace:SetNativeHealAmount(on)
    on = on and true or false
    local db = self.db or JimsPlusDB
    if db then db.nativeHealAmount = on end
    Send(on)
    SetDrawing(on)
    print("|cFF00FF00[JimsPlus]|r Incoming heals on raid frames: " .. (on and "|cFFFFD100on|r." or "|cFF00FF00off|r."))
end

local f = CreateFrame("Frame")
f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:SetScript("OnEvent", function()
    -- The proxy's flag is per session and starts off: send the saved choice every time.
    local on = namespace:IsNativeHealAmountOn()
    Send(on)
    SetDrawing(on)
    if on then
        -- The raid frames are built a moment after the loading screen ends.
        C_Timer.After(1, Rescan)
    end
end)
