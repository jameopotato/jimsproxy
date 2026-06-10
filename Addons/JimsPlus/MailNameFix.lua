-- JimsPlus MailNameFix
-- Strip the redundant realm suffix from mail recipients on the 1.14 Classic Era client.
--
-- The modern client appends a realm suffix to mail-recipient autocomplete entries for
-- any character that isn't on your own account (friends + recently-interacted players).
-- The proxy's player GUIDs carry a 13-bit realm-id (1) that does not match the home
-- realm's virtual-realm address, so the client has no name for that realm and renders a
-- bare trailing dash, e.g. "Babalucci-". Your own alts are exempt (same account, no
-- suffix). Worse, the suffix is forwarded verbatim to the legacy 1.12 server on send,
-- which has no such player and rejects the mail.
--
-- Vanilla 1.12 character names never contain "-" (letters only) and this server is a
-- single realm, so the realm portion is always safe to strip. We clean it in two places:
--   1. The SendMail API, so delivery always uses the bare character name regardless of
--      how the recipient got into the To: box (typed, autocompleted, or pasted).
--   2. The shared autocomplete dropdown (display + the value inserted on select), so the
--      list shows clean names.

local function StripRealm(name)
    if type(name) ~= "string" then return name end
    local dash = name:find("-", 1, true)
    if dash then
        return name:sub(1, dash - 1)
    end
    return name
end

-- 1) Guarantee delivery: wrap SendMail so the recipient is always the bare name.
if type(SendMail) == "function" then
    local origSendMail = SendMail
    SendMail = function(recipient, subject, body)
        return origSendMail(StripRealm(recipient), subject, body)
    end
end

-- 2) Clean the shared autocomplete dropdown. Runs for every autocomplete editbox
--    (mail, whisper, who, invite); stripping "-" from a vanilla name is always safe,
--    so no per-box gating is needed. Fix both the visible button text and the stored
--    nameInfo the client inserts when an entry is chosen.
if type(AutoComplete_Update) == "function" then
    hooksecurefunc("AutoComplete_Update", function()
        local i = 1
        while true do
            local button = _G["AutoCompleteButton" .. i]
            if not button then break end
            if button:IsShown() then
                if button.nameInfo and type(button.nameInfo.name) == "string" then
                    button.nameInfo.name = StripRealm(button.nameInfo.name)
                end
                local txt = button:GetText()
                if txt then
                    local clean = StripRealm(txt)
                    if clean ~= txt then
                        button:SetText(clean)
                    end
                end
            end
            i = i + 1
        end
    end)
end
