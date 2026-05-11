local ADDON_NAME, namespace = ...

local function CreateCheckbox(parent, yOffset, label, tooltip)
    local cb = CreateFrame("CheckButton", nil, parent, "InterfaceOptionsCheckButtonTemplate")
    cb:SetPoint("TOPLEFT", parent, "TOPLEFT", 16, yOffset)
    cb.Text:SetText(label)
    if tooltip then
        cb.tooltipText = tooltip
    end
    return cb
end

local function CreateHeader(parent, text, yOffset)
    local fs = parent:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
    fs:SetPoint("TOPLEFT", parent, "TOPLEFT", 16, yOffset)
    fs:SetText(text)
    return fs
end

local function CreateDescription(parent, text, yOffset)
    local fs = parent:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    fs:SetPoint("TOPLEFT", parent, "TOPLEFT", 18, yOffset)
    fs:SetPoint("RIGHT", parent, "RIGHT", -16, 0)
    fs:SetJustifyH("LEFT")
    fs:SetText(text)
    return fs
end

---------------------------------------------------------------------------
-- Panel
---------------------------------------------------------------------------
local panel = CreateFrame("Frame", "JimsPlusOptionsPanel", UIParent)
panel.name = "JimsPlus"

local title = panel:CreateFontString(nil, "ARTWORK", "GameFontNormalLarge")
title:SetPoint("TOPLEFT", 16, -16)
title:SetText("|cFF00FF00JimsPlus|r Settings")

local subtitle = panel:CreateFontString(nil, "ARTWORK", "GameFontHighlightSmall")
subtitle:SetPoint("TOPLEFT", title, "BOTTOMLEFT", 0, -4)
subtitle:SetText("Build " .. namespace.BUILD)

---------------------------------------------------------------------------
-- Client Fixes (always on by default — these fix real bugs)
---------------------------------------------------------------------------
local y = -60
CreateHeader(panel, "Client Fixes", y)
y = y - 18
CreateDescription(panel, "These fix bugs in the 1.14 client when connected to a vanilla server. All enabled by default.", y)
y = y - 22

local cbPetFix = CreateCheckbox(panel, y,
    "Pet UI crash fix  |cFFFF6600(reload required)|r",
    "Prevents Lua errors when opening the pet stats or stable UI.\nHunter pets from vanilla servers don't have a player class,\nwhich crashes the 1.14 client's FrameXML code.\n\nChanges take effect after /reload.")
y = y - 28

local cbTaxiFix = CreateCheckbox(panel, y,
    "Hide early-landing button",
    "Hides the \"Stop at next flight path\" button during flights.\nVanilla servers don't support early landing — clicking it\ndoes nothing. This removes the misleading button.")
y = y - 28

local cbTooltipFix = CreateCheckbox(panel, y,
    "Off-class armor / weapon red text  |cFFFF6600(reload required)|r",
    "Recolors armor type and weapon type to red on item tooltips and vendor\nrows when your class can't use the item (e.g. \"Mail\" on a rogue, \"Plate\"\non a hunter), based on the proficiencies you've actually trained.\n\nThe 1.14 Classic Era client gets this signal from a hardcoded table\nthe proxy can't reach over the wire — this addon does the recolor\nclient-side.\n\nChanges take effect after /reload.")
y = y - 40

---------------------------------------------------------------------------
-- Cast Bars
---------------------------------------------------------------------------
CreateHeader(panel, "Cast Bars", y)
y = y - 18
CreateDescription(panel, "Show cast bars for other players and NPCs. The vanilla server sends cast data through the proxy, but the 1.14 client doesn't display it natively — these fill that gap.", y)
y = y - 30

local castbarUnits = {
    { key = "target",    label = "Target",     tooltip = "Shows what your current target is casting.\nEssential for interrupting enemy spells." },
    { key = "nameplate", label = "Nameplates",  tooltip = "Shows cast bars on nameplates above characters' heads.\nUseful in dungeons and PvP to see multiple casts at once." },
    { key = "focus",     label = "Focus",       tooltip = "Shows what your focus target is casting.\nUseful for watching a specific mob while targeting another." },
    { key = "player",    label = "Player  |cFF888888(reskins Blizzard bar)|r", tooltip = "Replaces the default Blizzard cast bar with the JimsPlus style.\nPurely cosmetic — disable if you prefer the default look\nor use another cast bar addon." },
    { key = "party",     label = "Party members", tooltip = "Shows cast bars on party member frames.\nUseful for seeing when your healer is casting." },
}

local castbarCBs = {}
for _, info in ipairs(castbarUnits) do
    castbarCBs[info.key] = CreateCheckbox(panel, y, info.label, info.tooltip)
    y = y - 28
end

---------------------------------------------------------------------------
-- Sync checkboxes from saved state
---------------------------------------------------------------------------
local function RefreshCheckboxes()
    local db = namespace.db or JimsPlusDB or {}
    cbPetFix:SetChecked(db.petFix == true)
    cbTaxiFix:SetChecked(db.taxiFix == true)
    cbTooltipFix:SetChecked(db.tooltipFix == true)

    local cdb = JimsPlusCastbars and JimsPlusCastbars.db
    if cdb then
        for _, info in ipairs(castbarUnits) do
            local unitDB = cdb[info.key]
            castbarCBs[info.key]:SetChecked(unitDB and unitDB.enabled and true or false)
        end
    end
end
panel:SetScript("OnShow", RefreshCheckboxes)

local initFrame = CreateFrame("Frame")
initFrame:RegisterEvent("PLAYER_LOGIN")
initFrame:SetScript("OnEvent", function()
    RefreshCheckboxes()
    initFrame:UnregisterAllEvents()
end)

---------------------------------------------------------------------------
-- OnClick handlers
---------------------------------------------------------------------------
cbPetFix:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.petFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Pet UI fix " .. (enabled and "enabled" or "disabled") .. ". Type /reload to apply.")
end)

cbTaxiFix:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.taxiFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Early-landing button " .. (enabled and "hidden" or "shown") .. ". Type /reload to apply.")
end)

cbTooltipFix:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.tooltipFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Off-class armor red text " .. (enabled and "enabled" or "disabled") .. ". Type /reload to apply.")
end)

for _, info in ipairs(castbarUnits) do
    local key = info.key
    castbarCBs[key]:SetScript("OnClick", function(self)
        local enabled = self:GetChecked() and true or false
        local cba = JimsPlusCastbars
        if cba and cba.db and cba.db[key] then
            cba.db[key].enabled = enabled
            cba:ToggleUnitEvents(true)
            if key == "player" and enabled then
                cba:SkinPlayerCastbar()
            end
        end
    end)
end

---------------------------------------------------------------------------
-- Register
---------------------------------------------------------------------------
InterfaceOptions_AddCategory(panel)

SLASH_JIMSPLUS1 = "/jimsplus"
SLASH_JIMSPLUS2 = "/jp"
SlashCmdList["JIMSPLUS"] = function()
    InterfaceOptionsFrame_OpenToCategory(panel)
    InterfaceOptionsFrame_OpenToCategory(panel)
end
