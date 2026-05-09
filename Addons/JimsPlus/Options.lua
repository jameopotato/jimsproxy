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
-- Module toggles
---------------------------------------------------------------------------
local y = -60
CreateHeader(panel, "Module Toggles", y)
y = y - 26

local cbPetFix = CreateCheckbox(panel, y,
    "PetFix — Pet UI crash fix  |cFFFF6600(reload required)|r",
    "Wraps PetPaperDollFrame_SetStats and PetStable_Update in pcall\nto prevent nil-class crashes on hunter pets.\n\nChanges take effect after /reload.")
y = y - 28

local cbTaxiFix = CreateCheckbox(panel, y,
    "TaxiFix — Hide early-landing button",
    "Hides the in-flight Leave Vehicle button.\nVanilla servers don't support early landing.")
y = y - 40

---------------------------------------------------------------------------
-- Castbar frame toggles
---------------------------------------------------------------------------
CreateHeader(panel, "Castbar Frames", y)
y = y - 26

local castbarUnits = {
    { key = "target",    label = "Target castbar" },
    { key = "nameplate", label = "Nameplate castbars" },
    { key = "player",    label = "Player castbar  |cFF888888(skins Blizzard bar)|r" },
    { key = "focus",     label = "Focus castbar" },
    { key = "party",     label = "Party castbars" },
}

local castbarCBs = {}
for _, info in ipairs(castbarUnits) do
    castbarCBs[info.key] = CreateCheckbox(panel, y, info.label)
    y = y - 28
end

---------------------------------------------------------------------------
-- Sync checkboxes from saved state when shown
---------------------------------------------------------------------------
panel:SetScript("OnShow", function()
    local db = namespace.db or {}
    cbPetFix:SetChecked(db.petFix ~= false)
    cbTaxiFix:SetChecked(db.taxiFix ~= false)

    local cdb = JimsPlusCastbars and JimsPlusCastbars.db
    if cdb then
        for _, info in ipairs(castbarUnits) do
            local unitDB = cdb[info.key]
            castbarCBs[info.key]:SetChecked(unitDB and unitDB.enabled)
        end
    end
end)

---------------------------------------------------------------------------
-- OnClick handlers
---------------------------------------------------------------------------
cbPetFix:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.petFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r PetFix " .. (enabled and "enabled" or "disabled") .. ". Type /reload to apply.")
end)

cbTaxiFix:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.taxiFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r TaxiFix " .. (enabled and "enabled" or "disabled") .. ". Type /reload to apply.")
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
