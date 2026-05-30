-- JimsPlus AuctionsPaginationFix
-- Listens to JP_AH_TOTAL sideband from the proxy. The proxy walks all owner pages
-- server-side and combines them into a single SMSG response, so pagination buttons
-- on the 1.14 client UI are not needed; the FauxScrollFrame just scrolls through
-- the full list. This file's job is the diagnostic command + sideband tracking;
-- if it turns out the 1.14 client caps display at 50 items per SMSG, we'll re-add
-- the injected Next/Prev buttons here as a fallback.

local sidebandTotal = nil
local sidebandBatch = nil

local function HandleJpSideband(prefix, message)
    if prefix ~= "JP" then return end
    local cmd, totalStr, batchStr = strsplit("\t", message)
    if cmd ~= "AH_TOTAL" then return end
    sidebandTotal = tonumber(totalStr) or 0
    sidebandBatch = tonumber(batchStr) or 0
end

if C_ChatInfo and C_ChatInfo.RegisterAddonMessagePrefix then
    C_ChatInfo.RegisterAddonMessagePrefix("JP")
end

local f = CreateFrame("Frame")
f:RegisterEvent("CHAT_MSG_ADDON")
f:RegisterEvent("AUCTION_HOUSE_SHOW")
f:SetScript("OnEvent", function(_, event, arg1, arg2)
    if event == "CHAT_MSG_ADDON" then
        HandleJpSideband(arg1, arg2)
    elseif event == "AUCTION_HOUSE_SHOW" then
        -- aux fallback: aux's tabs/auctions/core.lua expects a `locked` table to exist before
        -- cancel_auction() runs, but only initializes it inside its AUCTION_OWNED_LIST_UPDATE
        -- handler. When the proxy's walk-and-combine delays the first OWNED_LIST_UPDATE,
        -- aux's Cancel button can race ahead and crash with "attempt to index global 'locked'".
        -- aux's module env inherits from _G via __index (per aux/libs/package.lua), so seeding
        -- _G.locked={} satisfies the read until aux's own handler runs and writes to its module
        -- env. Same for `refresh` so the on_update path doesn't read nil if it runs first.
        if _G.locked == nil then _G.locked = {} end
        if _G.refresh == nil then _G.refresh = false end
    end
end)

