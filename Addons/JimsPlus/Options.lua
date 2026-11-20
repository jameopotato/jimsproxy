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
                print("|cFF00FF00[JimsPlus]|r Bag Audit stopped: your bags are full. Free a slot and click again.")
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
    if KR_cleaning then print("|cFF00FF00[JimsPlus]|r Bag Audit finished.") end
    KR_cleaning = false
end

local function KR_StartCleanup()
    if InCombatLockdown() then
        print("|cFF00FF00[JimsPlus]|r Bag Audit can't run in combat.")
        return
    end
    if KR_cleaning then return end
    KR_cleaning, KR_steps = true, 0
    KR_Step()
end

---------------------------------------------------------------------------
-- Panels: the "JimsPlus" page carries the client fixes (two columns, to leave room),
-- Incoming Heals, Pet Bar and Bags; Cast Bars is the one sub-page in the Interface
-- Options left tree (it is the largest, most distinct group). Kept deliberately shallow.
--
-- Wording follows the launcher's UI voice guide: sentence-case labels, Title Case
-- section headers and buttons, a plain first line in each tooltip with the detail
-- after it, what a setting does rather than how good it is, digits, no exclamation.
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
    "Open the Cast Bars page: which units get a cast bar, and\nPerformance Mode.")
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
-- Main page: Client Fixes (two columns)
---------------------------------------------------------------------------
local COLUMN_2 = 318

local y = -56
CreateSection(root, "Client Fixes", y)
y = y - 26
CreateDescription(root, "Fixes for 1.14 client bugs that show up on a vanilla server. The greyed ones are always on.", y)
y = y - 24

local cbPetFix = CreateCheckbox(root, y,
    "Pet UI crash fix  |cFF888888(always on)|r",
    "Stops the Lua errors when you open the pet stats or stable window.\n\nA pet from a vanilla server has no player class, and the 1.14\nclient's pet code expects one.\n\nAlways on. Without it those windows throw errors.")
cbPetFix:SetChecked(true)
cbPetFix:Disable()
cbPetFix.Text:SetTextColor(0.5, 0.5, 0.5)

local cbTaxiFix = CreateCheckbox(root, y,
    "Hide early-landing button  |cFF888888(always on)|r",
    "Hides the \"Stop at next flight path\" button during flights.\n\nA vanilla server has no early landing, so the button does nothing.\n\nAlways on.",
    COLUMN_2)
cbTaxiFix:SetChecked(true)
cbTaxiFix:Disable()
cbTaxiFix.Text:SetTextColor(0.5, 0.5, 0.5)
y = y - 28

local cbTooltipFix = CreateCheckbox(root, y,
    "Off-class armor / weapon red text  |cFFFF6600(reload required)|r",
    "Turns the armor or weapon type red on item tooltips and vendor rows\nwhen you can't use the item, for example \"Mail\" on a rogue. It goes\nby the proficiencies you've trained.\n\nThe 1.14 client takes this from a built-in table the proxy can't\nchange, so the addon recolors the text itself.\n\nOff by default. Takes effect after /reload.")
y = y - 28

local cbMoonkinSound = CreateCheckbox(root, y,
    "Moonkin Form sound  |cFF888888(Druid only)|r",
    "Plays Moonkin Form's own transformation sound. Without this the\n1.14 client plays the Bear Form sound.\n\nDruids only. On by default. Takes effect after /reload.")

local cbApiCompat = CreateCheckbox(root, y,
    "Modern addon API shims  |cFFFF6600(reload)|r",
    "Adds newer addon APIs the 1.14.2 client lacks, so addons and\nWeakAuras written for later clients stop erroring: C_Container, the\nC_Spell range check, vehicle API stubs and a scroll-frame helper.\n\nIt only adds what's missing. Nothing the client already has is\nreplaced.\n\nBased on HermesCompat by techgeekpr (MIT).\n\nOff by default. Turn it on if an addon you use needs it.\nTakes effect after /reload.",
    COLUMN_2)
y = y - 28

local cbBowSheathe = CreateCheckbox(root, y,
    "Show 2H weapon on back with a bow",
    "With a bow equipped, the 1.14 client drops your sheathed two-handed\nweapon and quiver from your back after combat. Guns and crossbows\naren't affected.\n\nYour character puts the bow away a few seconds after combat. If the\nclient sheathes first, the addon sheathes again and the models come\nback.\n\nOn by default. Takes effect immediately.")
y = y - 40

---------------------------------------------------------------------------
-- Main page: Incoming Heals
---------------------------------------------------------------------------
CreateSection(root, "Incoming Heals", y)
y = y - 26

local cbHealAmount = CreateCheckbox(root, y,
    "Show incoming heals on the default Blizzard raid frames",
    "Draws an incoming-heal bar on Blizzard's raid frames and raid-style\nparty frames while another player is casting a heal on that unit.\n\nThe amount comes from the healer's HealComm addon and reaches you\nthrough the proxy, so it shows for healers in view who run one. Your\nown heals aren't included.\n\nUnit frame addons that read the game's incoming heals get the same\namounts. Addons that use HealComm already show these heals and\ndon't need this.\n\nSide effect: cast-bar addons that read the combat log, other than\nJimsPlus, restart a healer's bar once, a fraction of a second into\nthe cast.\n\nOff by default. Takes effect immediately. Shortcut: /jp heals")
y = y - 40

---------------------------------------------------------------------------
-- Main page: Pet Bar (hunters & warlocks)
---------------------------------------------------------------------------
CreateSection(root, "Pet Bar", y)
y = y - 26
CreateDescription(root, "For hunters and warlocks. The 1.14 client can't put a pet command or stance button back on the bar once a spell has replaced it.", y)
y = y - 30

local cbPetLock = CreateCheckbox(root, y,
    "Lock pet command & stance buttons",
    "Keeps Attack, Follow, Stay and the Aggressive, Defensive and Passive\nbuttons on the pet bar, and puts one back if a spell is dropped\nonto it.\n\nOff by default. Shortcut: /jp petlock")
y = y - 32

local btnPetReset = CreateButton(root, y, "Reset Pet Bar", 150,
    "Puts your pet's command and stance buttons back now, without\nresummoning the pet.\n\nWith the lock above off, the bar can lose them again the next time\nyou summon.\n\nShortcut: /jp petreset")
btnPetReset:SetScript("OnClick", function() namespace:ResetPetBar() end)
y = y - 40

---------------------------------------------------------------------------
-- Main page: Bags
---------------------------------------------------------------------------
CreateSection(root, "Bags", y)
y = y - 26
local btnBagAudit = CreateButton(root, y, "Bag Audit", 150,
    "Moves items that aren't keys out of your keyring and into your bags.\nKeys stay where they are.\n\nFor characters with items stuck in the keyring from an older bug.\nNeeds free bag slots and can't run in combat.")
btnBagAudit:SetScript("OnClick", KR_StartCleanup)
y = y - 30

local cbBagSort = CreateCheckbox(root, y,
    "Custom bag sort order  |cFF888888(Bagnon)|r",
    "Changes the order Bagnon's sort button uses: permanent items such as\nthe hearthstone first, then profession and gathering tools, quest\nitems, soulbound gear, other gear, consumables, everything else, and\njunk last.\n\nApplies to Bagnon's bank sort too. Off by default. Takes effect on\nthe next sort.")

---------------------------------------------------------------------------
-- Cast Bars sub-page (reached from the "Cast Bars" entry in the left tree)
---------------------------------------------------------------------------
y = -44
CreateDescription(panelCast, "Cast bars for other players and NPCs, which the 1.14 client doesn't show by itself. The proxy sends these bars what the combat log doesn't carry: the server's exact cast time for every cast, when a channel starts and ends, and the heals of grouped healers you can't see.", y)
y = y - 46

local castbarUnits = {
    { key = "target",    label = "Target",     tooltip = "Shows what your target is casting." },
    { key = "nameplate", label = "Nameplates",  tooltip = "Shows a cast bar on each nameplate, so several casts are visible\nat once." },
    { key = "focus",     label = "Focus",       tooltip = "Shows what your focus is casting while you target something else." },
    { key = "player",    label = "Player  |cFF888888(reskins Blizzard bar)|r", tooltip = "Gives your own cast bar the JimsPlus look and corrects the icon on\ntradeskill casts.\n\nTurn it off to keep Blizzard's look, or if another addon styles your\ncast bar." },
    { key = "party",     label = "Party members", tooltip = "Shows a cast bar on each party member's frame, in groups of up to 5.\n\nFor a grouped healer you can't see, the bar shows the heal their\nHealComm addon announces: the spell's name when it is certain,\n\"Unknown Heal\" when it isn't." },
}

local castbarCBs = {}
for _, info in ipairs(castbarUnits) do
    castbarCBs[info.key] = CreateCheckbox(panelCast, y, info.label, info.tooltip)
    y = y - 28
end

y = y - 8
local cbPerf = CreateCheckbox(panelCast, y,
    "Performance Mode  |cFFFFD100(suspends cast bars)|r",
    "Stops all JimsPlus cast bars: nothing is drawn, no casts are tracked,\nand the proxy stops sending cast data. For crowded fights where the\nframe rate drops, such as battlegrounds and large pulls.\n\nEvery other JimsPlus fix stays on. Shortcut: /jp perf")

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
    cbHealAmount:SetChecked((namespace.db and namespace.db.nativeHealAmount) and true or false)
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
    print("|cFF00FF00[JimsPlus]|r Off-class armor red text: " .. (enabled and "on" or "off") .. ". Takes effect after /reload.")
end)

cbMoonkinSound:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.moonkinSound = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Moonkin Form sound: " .. (enabled and "on" or "off") .. ". Takes effect after /reload.")
end)

cbApiCompat:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.apiCompat = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Modern addon API shims: " .. (enabled and "on" or "off") .. ". Takes effect after /reload.")
end)

-- JimsProxy (bow sheathe fix): purely addon-side (BowSheatheFix.lua re-sheathe nudge), read live from db
cbBowSheathe:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.bowSheatheFix = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Bow 2H-sheathe fix: " .. (enabled and "on" or "off") .. ".")
end)

cbBagSort:SetScript("OnClick", function(self)
    local enabled = self:GetChecked() and true or false
    if namespace.db then
        namespace.db.bagSortOrder = enabled
    end
    print("|cFF00FF00[JimsPlus]|r Custom bag sort order: " .. (enabled and "on" or "off") .. ".")
end)

cbPerf:SetScript("OnClick", function(self)
    namespace:SetPerformanceMode(self:GetChecked() and true or false)
end)

cbPetLock:SetScript("OnClick", function(self)
    namespace:SetPetBarLock(self:GetChecked() and true or false)
end)

cbHealAmount:SetScript("OnClick", function(self)
    namespace:SetNativeHealAmount(self:GetChecked() and true or false)
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
    if msg == "heals" then
        namespace:SetNativeHealAmount(not namespace:IsNativeHealAmountOn())
        RefreshCheckboxes()
        return
    end
    OpenPanel(root)
end
