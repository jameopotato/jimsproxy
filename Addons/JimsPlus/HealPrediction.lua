local ADDON_NAME, namespace = ...

-- JimsPlus incoming heals on Blizzard frames (opt-in)
-- A vanilla server never tells the client how much a heal will heal, so Blizzard's own frames,
-- and any frame that reads the game's own incoming-heal number, show nothing for another
-- player's heal. With this on, the proxy adds the amount the healer's HealComm addon announced
-- to that healer's cast, for healers near you. Unit frame addons that use HealComm read the
-- announcement themselves and don't need it.
--
-- The cost: the proxy sends the healer's cast start a second time, which puts a second "begins
-- to cast" line in the combat log. JimsPlus cast bars know to ignore it; other cast-bar addons
-- that read the combat log restart that bar once.
--
-- Account-wide, off by default, and sent to the proxy at every loading screen because the
-- proxy's flag only lasts a session. Talks over the JP addon sideband (both ends ours).

local function Send(on)
    if C_ChatInfo and C_ChatInfo.SendAddonMessage then
        C_ChatInfo.SendAddonMessage("JP", "H\tAMOUNT\t" .. (on and "1" or "0"), "WHISPER", UnitName("player"))
    end
end

function namespace:IsNativeHealAmountOn()
    local db = self.db or JimsPlusDB
    return db and db.nativeHealAmount and true or false
end

function namespace:SetNativeHealAmount(on)
    on = on and true or false
    local db = self.db or JimsPlusDB
    if db then db.nativeHealAmount = on end
    Send(on)
    print("|cFF00FF00[JimsPlus]|r Other healers' incoming heals on Blizzard frames " ..
        (on and "|cFFFFD100on|r. Frames that use the game's own number now show heals from healers near you."
             or "|cFF00FF00off|r. Only unit frame addons that use HealComm show other healers' incoming heals."))
end

local f = CreateFrame("Frame")
f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:SetScript("OnEvent", function()
    -- The proxy's flag is per session and starts off: send the saved choice every time.
    Send(namespace:IsNativeHealAmountOn())
end)
