-- JimsPlus ZoneSync
-- Reports the player's current zone to the proxy on every overland zone
-- change so it can flip the General/Trade/LocalDefense channels to the new
-- zone-suffixed names.
--
-- Why: vmangos's Player::UpdateLocalChannels is empty server-side (comment:
-- "Updated client-side"). The 1.12 native client drives zone-channel rejoin
-- itself by sending CMSG_LEAVE_CHANNEL/CMSG_JOIN_CHANNEL on every zone
-- change. The 1.14 modern client doesn't emit those on zone change (the
-- modern wire protocol moved to server-tracked zone state, which vmangos
-- doesn't implement). Result: chat header keeps showing the old zone's
-- channel until relog.
--
-- The proxy intercepts the JP "Z\t<zoneName>" sideband, looks up the area
-- ID, and synthesizes the leave/join CMSGs to the legacy server. Server's
-- regular CMSG_JOIN_CHANNEL handler echoes back SMSG_CHANNEL_NOTIFY YouJoined,
-- which the proxy forwards to the modern client and the chat header flips.

local f = CreateFrame("Frame")
local lastSentZone

local function SendZone()
    local zone = GetRealZoneText()
    if not zone or zone == "" then return end
    if zone == lastSentZone then return end
    lastSentZone = zone
    C_ChatInfo.SendAddonMessage("JP", "Z\t" .. zone, "WHISPER", UnitName("player"))
end

f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:RegisterEvent("ZONE_CHANGED_NEW_AREA")
f:SetScript("OnEvent", SendZone)
