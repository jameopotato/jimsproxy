local ADDON_NAME, namespace = ...

-- JimsPlus PetBar (opt-in)
-- Asks the proxy to keep the pet's command buttons (Attack/Follow/Stay) and stance
-- buttons (Aggressive/Defensive/Passive) in place, and to rebuild them if a spell was
-- dropped onto one and knocked it off. The proxy does the work; this file is just the
-- switch. It talks over the JP addon sideband (both ends ours), never touches a secure
-- pet button, and is OFF by default, so a player who doesn't turn it on sees no change.
--
-- The lock is stored per character and re-asserted to the proxy on every login, because
-- the proxy flag is per session.

local function Send(msg)
    if C_ChatInfo and C_ChatInfo.SendAddonMessage then
        C_ChatInfo.SendAddonMessage("JP", msg, "WHISPER", UnitName("player"))
    end
end

function namespace:SetPetBarLock(on)
    on = on and true or false
    if JimsPlusCharDB then JimsPlusCharDB.petBarLock = on end
    Send("P\tLOCK\t" .. (on and "1" or "0"))
    print("|cFF00FF00[JimsPlus]|r Pet command and stance buttons " ..
        (on and "|cFFFFD100locked|r. The proxy keeps them in place and rebuilds them if they get knocked off."
             or "|cFF00FF00unlocked|r. The pet bar is left alone."))
end

function namespace:ResetPetBar()
    if not UnitExists("pet") then
        print("|cFF00FF00[JimsPlus]|r No pet out. Summon or call your pet first.")
        return
    end
    Send("P\tRESET")
end

function namespace:IsPetBarLocked()
    return JimsPlusCharDB and JimsPlusCharDB.petBarLock and true or false
end

local f = CreateFrame("Frame")
f:RegisterEvent("ADDON_LOADED")
f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:SetScript("OnEvent", function(_, event, addon)
    if event == "ADDON_LOADED" then
        if addon ~= ADDON_NAME then return end
        JimsPlusCharDB = JimsPlusCharDB or {}
        if JimsPlusCharDB.petBarLock == nil then JimsPlusCharDB.petBarLock = false end
    elseif event == "PLAYER_ENTERING_WORLD" then
        -- Re-assert the saved lock to the proxy (its flag is per session, default off).
        if JimsPlusCharDB and JimsPlusCharDB.petBarLock then
            Send("P\tLOCK\t1")
        end
    end
end)
