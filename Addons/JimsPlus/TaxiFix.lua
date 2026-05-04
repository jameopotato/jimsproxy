-- JimsPlus TaxiFix
-- Hide the in-flight "Leave Vehicle" button on the 1.14 Classic Era client.
--
-- The 1.14 modern client treats taxi flights as vehicles in its UI layer and
-- reuses the generic MainMenuBarVehicleLeaveButton during flight. Clicking it
-- sends CMSG_TAXI_REQUEST_EARLY_LANDING. Vanilla 1.12 servers have no concept
-- of early landing -- the legacy server is committed to SplineTimeFull and
-- ignores the opcode -- so the proxy logs and no-ops it. To avoid the
-- "I clicked it and nothing happened" UX, hide the button entirely.
--
-- Vanilla 1.12 has no real vehicles either (TBC+ feature), so the button has
-- no other reason to exist on this server. Permanent hide is safe.
--
-- Native vanilla mechanism for early landing remains: log out at the next
-- flight master.

local function Apply(btn)
    if not btn then return end
    btn:Hide()
    -- Prevent any other code (Blizzard FrameXML, Dominos, etc.) from showing
    -- it back. Re-hide on every OnShow attempt.
    btn:SetScript("OnShow", function(self) self:Hide() end)
end

local f = CreateFrame("Frame")
f:RegisterEvent("PLAYER_LOGIN")
f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:RegisterEvent("TAXIMAP_OPENED")
f:RegisterEvent("UPDATE_BONUS_ACTIONBAR")  -- fires when the vehicle bar transitions in
f:SetScript("OnEvent", function()
    -- Primary target: the in-flight leave-vehicle button (per /framestack on
    -- 1.14.2 Classic Era).
    Apply(_G["MainMenuBarVehicleLeaveButton"])
    -- Defensive: also catch alternate names that may exist on other client
    -- variants or addon-overridden bars (e.g. Dominos wraps but does not
    -- replace the named button, so the primary hide above still works).
    Apply(_G["TaxiFrameStopButton"])
    Apply(_G["TaxiRequestEarlyLandingButton"])
end)

