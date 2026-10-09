local ADDON_NAME, namespace = ...

local function CreateCheckbox(parent, yOffset, label, tooltip, xOffset)
    local cb = CreateFrame("CheckButton", nil, parent, "InterfaceOptionsCheckButtonTemplate")
    cb:SetPoint("TOPLEFT", parent, "TOPLEFT", xOffset or 16, yOffset)
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

local function CreateButton(parent, yOffset, label, width, tooltip)
    local btn = CreateFrame("Button", nil, parent, "UIPanelButtonTemplate")
    btn:SetPoint("TOPLEFT", parent, "TOPLEFT", 16, yOffset)
    btn:SetSize(width or 160, 24)
    btn:SetText(label)
    if tooltip then
        btn:HookScript("OnEnter", function(self)
            GameTooltip:SetOwner(self, "ANCHOR_RIGHT")
            GameTooltip:SetText(tooltip, 1, 1, 1, 1, true)
            GameTooltip:Show()
        end)
        btn:HookScript("OnLeave", function() GameTooltip:Hide() end)
    end
    return btn
end

---------------------------------------------------------------------------
-- Keyring cleanup: move non-key items out of the keyring into free bag slots.
-- Older characters can have non-keys stuck in the keyring from a past client bug;
-- the proxy now blocks new ones, this button clears existing ones. Keys (item
-- class 13) are left in place. One move per timer tick so each settles server-side.
---------------------------------------------------------------------------
local KEYRING = (type(KEYRING_CONTAINER) == "number") and KEYRING_CONTAINER or -2

local function KR_NumSlots(bag)
    if C_Container and C_Container.GetContainerNumSlots then return C_Container.GetContainerNumSlots(bag) or 0 end
    return (GetContainerNumSlots and GetContainerNumSlots(bag)) or 0
end
local function KR_ItemID(bag, slot)
    if C_Container and C_Container.GetContainerItemID then return C_Container.GetContainerItemID(bag, slot) end
    return GetContainerItemID and GetContainerItemID(bag, slot)
end
local function KR_Pickup(bag, slot)
    if C_Container and C_Container.PickupContainerItem then return C_Container.PickupContainerItem(bag, slot) end
    if PickupContainerItem then return PickupContainerItem(bag, slot) end
end

local function KR_IsNonKey(id)
    if not id then return false end
    local classID = select(6, GetItemInfoInstant(id))
    if classID == nil then return false end -- unknown item: leave it alone
    if classID == 13 then return false end -- 13 = Key
    -- Keyring bag family (bit 256): Kronos marks some non-Key-class items keyring-able
    -- (e.g. Alarm-O-Bot) and the proxy's item hotfixes pass that through — those belong
    -- in the keyring, leave them there.
    local family = GetItemFamily(id)
    return not (family and bit.band(family, 256) ~= 0)
end

local function KR_FirstFreeBagSlot()
    for bag = 0, 4 do
        for slot = 1, KR_NumSlots(bag) do
            if not KR_ItemID(bag, slot) then return bag, slot end
        end
    end
end

local KR_cleaning, KR_steps = false, 0
local function KR_Step()
    KR_steps = KR_steps + 1
    if KR_steps > 40 then KR_cleaning = false; return end
    for slot = 1, KR_NumSlots(KEYRING) do
        if KR_IsNonKey(KR_ItemID(KEYRING, slot)) then
            local bag, bslot = KR_FirstFreeBagSlot()
            if not bag then
                print("|cFF00FF00[JimsPlus]|r Keyring cleanup: bags are full - free a slot and click again.")
                KR_cleaning = false
                return
            end
            ClearCursor()
            KR_Pickup(KEYRING, slot)
            KR_Pickup(bag, bslot)
            ClearCursor()
            C_Timer.After(0.2, KR_Step)
            return
        end
    end
    if KR_cleaning then print("|cFF00FF00[JimsPlus]|r Keyring cleanup complete.") end
    KR_cleaning = false
end

local function KR_StartCleanup()
    if InCombatLockdown() then
        print("|cFF00FF00[JimsPlus]|r Can't clean the keyring in combat.")
        return
    end
    if KR_cleaning then return end
    KR_cleaning, KR_steps = true, 0
    KR_Step()
end

---------------------------------------------------------------------------
-- Panels: the "JimsPlus" page carries the client fixes, with a Pet Bar and a
-- Bags section; Cast Bars is the one sub-page in the Interface Options left
-- tree (it is the largest, most distinct group). Kept deliberately shallow.
---------------------------------------------------------------------------
local root = CreateFrame("Frame", "JimsPlusOptionsPanel", UIParent)
root.name = "JimsPlus"
do
    local title = root:CreateFontString(nil, "ARTWORK", "GameFontNormalLarge")
    title:SetPoint("TOPLEFT", 16, -16)
    title:SetText("|cFF00FF00JimsPlus|r Settings")
    local subtitle = root:CreateFontString(nil, "ARTWORK", "GameFontHighlightSmall")
    subtitle:SetPoint("TOPLEFT", title, "BOTTOMLEFT", 0, -4)
    subtitle:SetText("v" .. (namespace.VERSION or "?"))
end

local panelCast = CreateFrame("Frame", nil, UIParent)
panelCast.name = "Cast Bars"
panelCast.parent = "JimsPlus"
do
    local t = panelCast:CreateFontString(nil, "ARTWORK", "GameFontNormalLarge")
    t:SetPoint("TOPLEFT", 16, -16)
    t:SetText("|cFF00FF00JimsPlus|r  Cast Bars")
end

local function OpenPanel(p)
    -- Classic InterfaceOptionsFrame_OpenToCategory needs two calls to actually open.
    InterfaceOptionsFrame_OpenToCategory(p)
    InterfaceOptionsFrame_OpenToCategory(p)
end

-- Top-right shortcut to the Cast Bars sub-page (also reachable from the left tree).
local btnCastTop = CreateButton(root, -16, "Cast Bars  >", 120,
    "Open the Cast Bars page: cast bars for targets, nameplates, focus,\nparty members, and your own bar, plus Performance Mode.")
btnCastTop:ClearAllPoints()
btnCastTop:SetPoint("TOPRIGHT", root, "TOPRIGHT", -16, -16)
btnCastTop:SetScript("OnClick", function() OpenPanel(panelCast) end)

-- A section header with a thin rule beneath it, so the groups read as distinct blocks.
local function CreateSection(parent, text, yOffset)
    local fs = CreateHeader(parent, text, yOffset)
    local line = parent:CreateTexture(nil, "ARTWORK")
    line:SetColorTexture(1, 0.82, 0, 0.30)
    line:SetHeight(1)
    line:SetPoint("TOPLEFT", fs, "BOTTOMLEFT", 0, -5)
    line:SetPoint("RIGHT", parent, "RIGHT", -16, 0)
    return fs
end

---------------------------------------------------------------------------
-- Main page: Client Fixes
---------------------------------------------------------------------------
local y = -56
CreateSection(root, "Client Fixes", y)
y = y - 26
CreateDescription(root, "These fix bugs in the 1.14 client when connected to a vanilla server. The greyed ones are always on.", y)
y = y - 26

local cbPetFix = CreateCheckbox(root, y,
    "Pet UI crash fix  |cFF888888(always on)|r",
    "Prevents Lua errors when opening the pet stats or stable UI.\nHunter pets from vanilla servers don't have a player class,\nwhich crashes the 1.14 client's FrameXML code.\n\nThis fix cannot be disabled — without it the pet UI is broken.")
cbPetFix:SetChecked(true)
cbPetFix:Disable()
cbPetFix.Text:SetTextColor(0.5, 0.5, 0.5)
y = y - 28

local cbTaxiFix = CreateCheckbox(root, y,
    "Hide early-landing button  |cFF888888(always on)|r",
    "Hides the \"Stop at next flight path\" button during flights.\nVanilla servers don't support early landing — clicking it\ndoes nothing.\n\nThis fix cannot be disabled — the button has no function on vanilla servers.")
cbTaxiFix:SetChecked(true)
cbTaxiFix:Disable()
cbTaxiFix.Text:SetTextColor(0.5, 0.5, 0.5)
y = y - 28

local cbTooltipFix = CreateCheckbox(root, y,
    "Off-class armor / weapon red text  |cFFFF6600(reload required)|r",
    "Recolors armor type and weapon type to red on item tooltips and vendor\nrows when your class can't use the item (e.g. \"Mail\" on a rogue, \"Plate\"\non a hunter), based on the proficiencies you've actually trained.\n\nThe 1.14 Classic Era client gets this signal from a hardcoded table\nthe proxy can't reach over the wire — this addon does the recolor\nclient-side.\n\nChanges take effect after /reload.")
y = y - 28

local cbMoonkinSound = CreateCheckbox(root, y,
    "Moonkin Form sound  |cFF888888(Druid only)|r",
    "Gives Moonkin Form a distinct transformation sound instead\nof reusing the Bear Form sound.\n\nOnly affects Druid characters.")
y = y - 28

local cbApiCompat = CreateCheckbox(root, y,
    "Modern addon API shims  |cFFFF6600(reload)|r",
    "Fills in modern APIs that are missing on the 1.14.2 client so newer\naddons and WeakAuras packs stop erroring: C_Container, the\nC_Spell range check, vehicle API stubs, and a scroll-frame helper.\n\nOnly ever adds what is missing; never overrides anything the\nclient already provides.\n\nBased on HermesCompat by techgeekpr (MIT).\n\nOff by default; turn it on only if an addon you use needs it.\nChanges take effect after /reload.")
y = y - 28

local cbBowSheathe = CreateCheckbox(root, y,
    "Show 2H weapon on back with a bow",
    "Fixes a 1.14 client bug where a sheathed two-handed weapon\n(and your quiver) disappears from your back after combat while\na bow is equipped (guns and crossbows are unaffected).\n\nYour character puts the bow away a few seconds after combat\nends; if the client sheathes on its own first, the addon instantly\nre-sheathes to restore the models.\n\nTakes effect immediately.")
y = y - 42

---------------------------------------------------------------------------
-- Main page: Pet Bar (hunters & warlocks)
---------------------------------------------------------------------------
CreateSection(root, "Pet Bar", y)
y = y - 26
CreateDescription(root, "Keep the pet's command and stance buttons on the bar; the 1.14 client can't drag them back once a spell knocks one off.", y)
y = y - 28

local cbPetLock = CreateCheckbox(root, y,
    "Lock pet command & stance buttons",
    "Hunters and warlocks: keep Attack, Follow, Stay and the\nAggressive/Defensive/Passive buttons on the pet bar, and\nput them back if a spell gets dropped onto one and knocks\nit off. Off by default. Turn it on only if you've lost those\nbuttons or want them protected.\n\nShortcut: /jp petlock")
y = y - 32

local btnPetReset = CreateButton(root, y, "Reset Pet Bar", 150,
    "Rebuilds your pet's command and stance buttons right now,\nwithout a resummon. Works best with the lock above turned\non, otherwise it comes back the next time you summon.\n\nShortcut: /jp petreset")
btnPetReset:SetScript("OnClick", function() namespace:ResetPetBar() end)
y = y - 42

---------------------------------------------------------------------------
-- Main page: Bags
---------------------------------------------------------------------------
CreateSection(root, "Bags", y)
y = y - 26
local btnBagAudit = CreateButton(root, y, "Bag Audit", 150,
    "Moves non-key items out of your keyring and into your bags.\nUse it if a character has items stuck in the keyring from a past\nbug. Keys are left in place.")
btnBagAudit:SetScript("OnClick", KR_StartCleanup)
y = y - 30

local cbBagSort = CreateCheckbox(root, y,
    "Custom bag sort order  |cFF888888(Bagnon)|r",
    "Changes Bagnon's sort button to a curated layout:\npermanent fixtures (hearthstone) first, then profession and\ngathering tools, quest items, soulbound gear, other gear,\nconsumables, everything else, and junk last.\n\nAlso applies to Bagnon's bank sort. Takes effect on the\nnext sort — no reload needed.")

---------------------------------------------------------------------------
-- Cast Bars sub-page (reached from the "Cast Bars" entry in the left tree)
---------------------------------------------------------------------------
y = -44
CreateDescription(panelCast, "Show cast bars for other players and NPCs. The vanilla server sends cast data through the proxy, but the 1.14 client doesn't display it natively — these fill that gap.", y)
y = y - 34

local castbarUnits = {
    { key = "target",    label = "Target",     tooltip = "Shows what your current target is casting.\nEssential for interrupting enemy spells." },
    { key = "nameplate", label = "Nameplates",  tooltip = "Shows cast bars on nameplates above characters' heads.\nUseful in dungeons and PvP to see multiple casts at once." },
    { key = "focus",     label = "Focus",       tooltip = "Shows what your focus target is casting.\nUseful for watching a specific mob while targeting another." },
    { key = "player",    label = "Player  |cFF888888(reskins Blizzard bar)|r", tooltip = "Replaces the default Blizzard cast bar with the JimsPlus style.\nAlso fixes incorrect icons on tradeskill craft bars.\n\nDisable if you prefer the default look or use another cast bar addon." },
    { key = "party",     label = "Party members", tooltip = "Shows cast bars on party member frames.\nUseful for seeing when your healer is casting." },
}

local castbarCBs = {}
for _, info in ipairs(castbarUnits) do
    castbarCBs[info.key] = CreateCheckbox(panelCast, y, info.label, info.tooltip)
    y = y - 28
end

y = y - 8
local cbPerf = CreateCheckbox(panelCast, y,
    "Performance Mode  |cFFFFD100(suspends cast bars)|r",
    "For crowded fights (battlegrounds, big pulls) where FPS tanks: fully suspends the\ncast-bar engine — stops rendering, stops per-event tracking, and tells the proxy to\nstop sending cast data entirely. Toggle on when it's a zerg, off when it clears.\n\nAll other JimsPlus fixes stay active. Shortcut: /jp perf")

---------------------------------------------------------------------------
-- Sync checkbox state from saved variables
---------------------------------------------------------------------------
local function RefreshCheckboxes()
    local db = namespace.db or JimsPlusDB or {}
    cbTooltipFix:SetChecked(db.tooltipFix == true)
    cbMoonkinSound:SetChecked(db.moonkinSound ~= false)
    cbApiCompat:SetChecked(db.apiCompat == true)
    cbBowSheathe:SetChecked(db.bowSheatheFix ~= false)
    cbBagSort:SetChecked(db.bagSortOrder ~= false)

    local cdb = JimsPlusCastbars and JimsPlusCastbars.db
    if cdb then
        for _, info in ipairs(castbarUnits) do
            local unitDB = cdb[info.key]
            castbarCBs[info.key]:SetChecked(unitDB and unitDB.enabled and true or false)
        end
    end

    cbPerf:SetChecked((namespace.db and namespace.db.performanceMode) and true or false)
    cbPetLock:SetChecked(JimsPlusCharDB and JimsPlusCharDB.petBarLock and true or false)
end
root:SetScript("OnShow", RefreshCheckboxes)
panelCast:SetScript("OnShow", RefreshCheckboxes)

local initFrame = CreateFrame("Frame")
initFrame:RegisterEvent("PLAYER_LOGIN")
initFrame:SetScript("OnEvent", function()
    -- Defer to next frame so CastBars.lua's PLAYER_LOGIN handler runs first
    -- and populates JimsPlusCastbars.db before we read it.
    C_Timer.After(0, RefreshCheckboxes)
    initFrame:UnregisterAllEvents()
end)

---------------------------------------------------------------------------
-- OnClick handlers
---------------------------------------------------------------------------
cbTooltipFix:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.tooltipFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Off-class armor red text " .. (enabled and "enabled" or "disabled") .. ". Type /reload to apply.")
end)

cbMoonkinSound:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.moonkinSound = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Moonkin Form sound " .. (enabled and "enabled" or "disabled") .. ". Type /reload to apply.")
end)

cbApiCompat:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.apiCompat = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Modern addon API shims " .. (enabled and "enabled" or "disabled") .. ". Type /reload to apply.")
end)

-- JimsProxy (bow sheathe fix): purely addon-side (BowSheatheFix.lua re-sheathe nudge), read live from db
cbBowSheathe:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.bowSheatheFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Bow 2H-sheathe fix " .. (enabled and "enabled" or "disabled") .. ".")
end)

cbBagSort:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.bagSortOrder = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Custom bag sort order " .. (enabled and "enabled" or "disabled") .. ".")
end)

cbPerf:SetScript("OnClick", function(self)
    namespace:SetPerformanceMode(self:GetChecked() and true or false)
end)

cbPetLock:SetScript("OnClick", function(self)
    namespace:SetPetBarLock(self:GetChecked() and true or false)
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
-- Register (parent first, then the one sub-page)
---------------------------------------------------------------------------
InterfaceOptions_AddCategory(root)
InterfaceOptions_AddCategory(panelCast)

SLASH_JIMSPLUS1 = "/jimsplus"
SLASH_JIMSPLUS2 = "/jp"
SlashCmdList["JIMSPLUS"] = function(msg)
    msg = (msg or ""):lower():gsub("%s+", "")
    if msg == "perf" or msg == "performance" then
        namespace:SetPerformanceMode(not (namespace.db and namespace.db.performanceMode))
        RefreshCheckboxes()
        return
    end
    if msg == "petreset" or msg == "pet" then
        namespace:ResetPetBar()
        return
    end
    if msg == "petlock" then
        namespace:SetPetBarLock(not namespace:IsPetBarLocked())
        RefreshCheckboxes()
        return
    end
    OpenPanel(root)
end
