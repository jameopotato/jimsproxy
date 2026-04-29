local ADDON_NAME, namespace = ...

-- Hotfix for a FrameXML bug present in Classic Era 1.14.2 (build 42597):
--   Interface_Vanilla\FrameXML\PetPaperDollFrame.lua:149
--     local classFileName = select(2, UnitClass("pet"));
--     classStatText = _G[strupper(classFileName).."_"..frame.stat.."_".."TOOLTIP"];
-- When the modern client returns nil for the pet class file name (which it
-- does for hunter pets coming through legacy Vanilla servers — pets have
-- creature classes, not player classes), strupper(nil) errors with
--   "bad argument #1 to 'strupper' (string expected, got nil)"
-- and the Lua error popup spams every frame the pet pane is open.
--
-- Blizzard fixed this post-1.14.2 by adding `if(classFileName) then` around
-- the assignment (visible in the Gethe wow-ui-source classic_era branch),
-- but the user's installed client doesn't have that guard.
--
-- We wrap PetPaperDollFrame_SetStats in pcall so the function still runs as
-- far as it can, but a strupper-nil failure inside it is swallowed instead
-- of bubbling up as a Lua error popup. The pet stat panel may render with
-- missing tooltips for the affected stat, but everything else (cast bar,
-- pet bar, pet HP) keeps working.

-- Sister bug in Interface_Vanilla\FrameXML\PetStable.lua:168 — same root cause:
-- `family` (and similar fields) come back nil for legacy-server hunter pets,
-- and PetStable_Update does string concatenation without a guard:
--     PetStableCurrentPet.tooltipSubtext = level.." "..family.." "..loyalty
-- which throws "attempt to concatenate a nil value".
--
-- Both functions are wrapped in pcall so a nil-arg failure inside FrameXML
-- code is silently absorbed instead of bubbling up as a Lua error popup.
-- The frame may render with missing fields (a stat tooltip or stable subtext),
-- but every other pet UI keeps working.

local installed = {}

local function WrapInPcall(funcName)
    if installed[funcName] then return true end
    local original = _G[funcName]
    if type(original) ~= "function" then
        return false
    end
    _G[funcName] = function(...)
        local ok, err = pcall(original, ...)
        if not ok and namespace.PET_FIX_DEBUG then
            print("|cFFFFAA00[JimsPlus]|r " .. funcName .. " swallowed: " .. tostring(err))
        end
    end
    installed[funcName] = true
    return true
end

local TARGETS = {
    "PetPaperDollFrame_SetStats",
    "PetStable_Update",
}

local function InstallAll()
    local installedAny = false
    for _, name in ipairs(TARGETS) do
        if WrapInPcall(name) then
            installedAny = true
        end
    end
    return installedAny
end

local installer = CreateFrame("Frame")
installer:RegisterEvent("PLAYER_LOGIN")
installer:RegisterEvent("ADDON_LOADED")
installer:SetScript("OnEvent", function(self, event, addonName)
    InstallAll()
    -- Only stop listening once every target has been wrapped. Some FrameXML
    -- functions are defined lazily when the related panel is first shown
    -- (e.g. PetStable_Update is in Blizzard_PetStables which loads on demand).
    local allDone = true
    for _, name in ipairs(TARGETS) do
        if not installed[name] then
            allDone = false
            break
        end
    end
    if allDone then
        self:UnregisterAllEvents()
    end
end)
