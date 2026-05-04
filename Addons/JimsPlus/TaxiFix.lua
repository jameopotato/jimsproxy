-- JimsPlus TaxiFix
-- Hide the "Stop at next flight path" button on the 1.14 TaxiFrame.
--
-- Vanilla 1.12 servers have no concept of early landing — the server is
-- committed to the full SplineTimeFull and ignores any opcode trying to cut
-- a flight short. The proxy's HandleTaxiRequestEarlyLanding logs the click
-- and otherwise no-ops; the original dismount Task fires on schedule so the
-- player still lands cleanly at the original destination. Hiding the button
-- avoids the confusing "I clicked it and nothing happened" user experience.

local function HideEarlyLandingButton()
    -- Button name varies across 1.14.x client versions and reskins; try the
    -- known candidates and Hide whichever exists. Lookup via _G is safe even
    -- when the button doesn't exist on the running client.
    local names = {
        "TaxiFrameStopButton",
        "TaxiRequestEarlyLandingButton",
    }
    for _, n in ipairs(names) do
        local btn = _G[n]
        if btn then
            if btn.Hide then btn:Hide() end
            if btn.Disable then btn:Disable() end
        end
    end
end

local f = CreateFrame("Frame")
-- TAXIMAP_OPENED fires every time the player talks to a flight master, AFTER
-- TaxiFrame is shown — so the button (if it exists) has been parented and
-- positioned. PLAYER_LOGIN covers the case where the frame was created at
-- load time.
f:RegisterEvent("PLAYER_LOGIN")
f:RegisterEvent("TAXIMAP_OPENED")
f:SetScript("OnEvent", function()
    HideEarlyLandingButton()
end)

print("|cFF00FF00[JimsPlus]|r TaxiFix loaded (early-landing button hidden — vanilla servers don't support early landing)")
