local ADDON_NAME, namespace = ...

namespace.VERSION = (GetAddOnMetadata and GetAddOnMetadata(ADDON_NAME, "Version")) or "?"

print("|cFF00FF00[JimsPlus]|r v" .. namespace.VERSION .. " loaded")

C_ChatInfo.RegisterAddonMessagePrefix("JP")

namespace.modules = {}

function namespace:RegisterModule(name, initFunc)
    self.modules[name] = { init = initFunc, enabled = true }
end

-- JimsProxy (Performance Mode): suspend/restore the cast-bar engine (the one heavy, per-frame
-- module) for crowded fights. This is more than the per-unit castbar checkboxes: it also stops
-- the proxy sideband and the per-event tracking. All other JimsPlus fixes stay active.
function namespace:SetPerformanceMode(on)
    on = on and true or false
    if self.db then self.db.performanceMode = on end
    local cb = JimsPlusCastbars
    if cb then
        if on then cb:SuspendCastbars() else cb:ResumeCastbars() end
    end
    print("|cFF00FF00[JimsPlus]|r Performance Mode " ..
        (on and "|cFFFFD100ON|r — cast bars suspended (other fixes stay active)"
             or "|cFF00FF00OFF|r — cast bars restored") .. ".")
end

local f = CreateFrame("Frame")
f:RegisterEvent("ADDON_LOADED")
f:SetScript("OnEvent", function(_, _, addon)
    if addon ~= ADDON_NAME then return end
    JimsPlusDB = JimsPlusDB or {}
    if JimsPlusDB.petFix == nil then JimsPlusDB.petFix = true end
    if JimsPlusDB.taxiFix == nil then JimsPlusDB.taxiFix = true end
    if JimsPlusDB.bagSortOrder == nil then JimsPlusDB.bagSortOrder = false end
    if JimsPlusDB.performanceMode == nil then JimsPlusDB.performanceMode = false end
    if JimsPlusDB.apiCompat == nil then JimsPlusDB.apiCompat = true end
    namespace.db = JimsPlusDB
end)
