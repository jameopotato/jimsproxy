-- JimsPlus ApiCompat
-- Fills in modern client APIs that are missing on the 1.14.2 Classic Era client, so
-- newer addons and WeakAuras packs stop erroring with "attempt to call/index a nil
-- value". Every shim only installs when the API is actually missing; nothing the
-- client already provides is ever overridden or wrapped.
--
-- Adapted from HermesCompat by techgeekpr (written as a fix for valrios).
-- https://github.com/techgeekpr/HermesCompat
-- MIT License, Copyright (c) 2026 techgeekpr.
--
-- Deliberately NOT carried over from HermesCompat:
--   * The GetBattlefieldScore faction wrap. It papers over a proxy bug (jimsproxy
--     issue #505, cache-miss rows fabricated as Human/Warrior/Horde); the proxy is
--     the only layer with the data to fix it, and JimsPlus never replaces Blizzard
--     globals as a rule.
--   * The WeakAuras aurabar SetStatusBarTextureLSM patch. It edits WeakAuras
--     internals and tracks one ported WA build; users who need it can run
--     HermesCompat alongside JimsPlus (every shim on both sides is guarded, so
--     whichever loads first wins and the other no-ops).
--
-- Toggled by Options > "Modern addon API shims" (JimsPlusDB.apiCompat, default on,
-- /reload to apply). Shims install at our ADDON_LOADED, i.e. before any addon that
-- loads after JimsPlus and before PLAYER_LOGIN. Addons that loaded before us and
-- feature-detect at their own load time are out of reach either way; in practice
-- these APIs are called at runtime (events), where late definitions cover them.

local ADDON_NAME, namespace = ...

local function InstallShims()
    -- 1) C_Spell.IsSpellInRange: modern range API some WeakAuras call. Map to the
    --    classic global IsSpellInRange, which takes a spell NAME, so convert numeric
    --    spell ids via GetSpellInfo. Classic returns 1/0/nil, modern a boolean;
    --    nil (invalid/unknown spell) passes through as nil like the modern API.
    if C_Spell and not C_Spell.IsSpellInRange and IsSpellInRange then
        function C_Spell.IsSpellInRange(spell, unit)
            local name = spell
            if type(spell) == "number" then
                name = GetSpellInfo(spell)
            end
            if not name then return nil end
            local r = IsSpellInRange(name, unit)
            if r == nil then return nil end
            return r == 1 or r == true
        end
    end

    -- 2) UIPanelScrollFrame_OnLoad: FrameXML helper this client no longer defines,
    --    still referenced from some addons' XML scroll templates (e.g. NovaWorldBuffs).
    --    Minimal reimplementation of the old FrameXML behavior: wire up the scrollbar
    --    references and zero the range so the template starts inert instead of erroring.
    if not UIPanelScrollFrame_OnLoad then
        function UIPanelScrollFrame_OnLoad(self)
            local name = self.GetName and self:GetName()
            local scrollbar = self.ScrollBar or (name and _G[name .. "ScrollBar"])
            if scrollbar then
                self.ScrollBar = scrollbar
                local sbname = scrollbar.GetName and scrollbar:GetName()
                scrollbar.ScrollUpButton = scrollbar.ScrollUpButton or (sbname and _G[sbname .. "ScrollUpButton"])
                scrollbar.ScrollDownButton = scrollbar.ScrollDownButton or (sbname and _G[sbname .. "ScrollDownButton"])
                scrollbar:SetMinMaxValues(0, 0)
                scrollbar:SetValue(0)
            end
            self.offset = 0
        end
    end

    -- 3) C_Container: modern container namespace (reached era in 1.14.4; this client
    --    is 1.14.2, so it is absent and no Blizzard code can be reading it). Blizzard
    --    kept the classic globals' names when moving them into the namespace, so any
    --    unknown member auto-maps to the like-named global via __index (cached with
    --    rawset on first use; misses stay uncached so late-defined globals are found).
    --    The two calls whose return shape changed from multiple values to a table get
    --    explicit conversion wrappers.
    if not C_Container then
        local function itemInfo(bag, slot)
            local icon, count, locked, quality, readable, lootable, link, filtered,
                  noValue, itemID, isBound = GetContainerItemInfo(bag, slot)
            if icon == nil and link == nil and itemID == nil then return nil end
            return {
                iconFileID = icon, stackCount = count, isLocked = locked, quality = quality,
                isReadable = readable, hasLoot = lootable, hyperlink = link, isFiltered = filtered,
                hasNoValue = noValue, itemID = itemID, isBound = isBound,
            }
        end
        local function questInfo(bag, slot)
            local g = _G.GetContainerItemQuestInfo
            if g then
                local isQuestItem, questID, isActive = g(bag, slot)
                return { isQuestItem = isQuestItem, questID = questID, isActive = isActive }
            end
            return { isQuestItem = false } -- no classic equivalent; treat as non-quest
        end
        local special = {
            GetContainerItemInfo = GetContainerItemInfo and itemInfo or nil,
            GetContainerItemQuestInfo = questInfo,
        }
        C_Container = setmetatable({}, {
            __index = function(t, key)
                local fn = special[key]
                if fn == nil then fn = _G[key] end -- same-named classic global
                if fn ~= nil then rawset(t, key, fn) end
                return fn
            end,
        })
    end

    -- 4) Vehicle API: added in Wrath, so "no vehicle" is simply the correct answer on
    --    a vanilla server. Each is defined only when missing; on a client where these
    --    do not exist, no secure Blizzard code path can be reading them, so
    --    addon-defined stubs cannot taint anything.
    local function retFalse() return false end
    local function retNil() return nil end
    local function retZero() return 0 end
    local function noop() end

    UnitHasVehicleUI            = UnitHasVehicleUI            or retFalse
    UnitHasVehiclePlayerFrameUI = UnitHasVehiclePlayerFrameUI or retFalse
    UnitInVehicle               = UnitInVehicle               or retFalse
    UnitControllingVehicle      = UnitControllingVehicle      or retFalse
    UnitInVehicleControlSeat    = UnitInVehicleControlSeat    or retFalse
    UnitTargetsVehicleInRaidUI  = UnitTargetsVehicleInRaidUI  or retFalse
    CanExitVehicle              = CanExitVehicle              or retFalse
    CanSwitchVehicleSeats       = CanSwitchVehicleSeats       or retFalse
    UnitVehicleSkin             = UnitVehicleSkin             or retNil
    UnitVehicleSeatCount        = UnitVehicleSeatCount        or retZero
    HasVehicleActionBar         = HasVehicleActionBar         or retFalse
    HasOverrideActionBar        = HasOverrideActionBar        or retFalse
    HasTempShapeshiftActionBar  = HasTempShapeshiftActionBar  or retFalse
    GetVehicleBarIndex          = GetVehicleBarIndex          or retNil
    GetOverrideBarIndex         = GetOverrideBarIndex         or retNil
    GetTempShapeshiftBarIndex   = GetTempShapeshiftBarIndex   or retNil
    VehicleExit                 = VehicleExit                 or noop
    UnitSwitchToVehicleSeat     = UnitSwitchToVehicleSeat     or noop
end

local f = CreateFrame("Frame")
f:RegisterEvent("ADDON_LOADED")
f:SetScript("OnEvent", function(self, _, addon)
    if addon ~= ADDON_NAME then return end
    self:UnregisterEvent("ADDON_LOADED")
    -- Core.lua registered its ADDON_LOADED handler first (earlier file in the .toc),
    -- so namespace.db is populated by the time this runs.
    if namespace.db and namespace.db.apiCompat == false then return end
    InstallShims()
end)
