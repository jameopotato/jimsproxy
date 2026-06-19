local ADDON_NAME, namespace = ...

namespace.VERSION = (GetAddOnMetadata and GetAddOnMetadata(ADDON_NAME, "Version")) or "?"

print("|cFF00FF00[JimsPlus]|r v" .. namespace.VERSION .. " loaded")

C_ChatInfo.RegisterAddonMessagePrefix("JP")

namespace.modules = {}

function namespace:RegisterModule(name, initFunc)
    self.modules[name] = { init = initFunc, enabled = true }
end

local f = CreateFrame("Frame")
f:RegisterEvent("ADDON_LOADED")
f:SetScript("OnEvent", function(_, _, addon)
    if addon ~= ADDON_NAME then return end
    JimsPlusDB = JimsPlusDB or {}
    if JimsPlusDB.petFix == nil then JimsPlusDB.petFix = true end
    if JimsPlusDB.taxiFix == nil then JimsPlusDB.taxiFix = true end
    if JimsPlusDB.bagSortOrder == nil then JimsPlusDB.bagSortOrder = false end
    namespace.db = JimsPlusDB
end)
