-- JimsPlus NameDashFix
-- Suppress the bare trailing realm dash ("Name-") the 1.14 Classic client renders for
-- any character that is NOT on your own account, across the UI surfaces that MailNameFix
-- does not cover: chat/emotes, the Friends list, and the mail inbox sender.
--
-- Why this lives in the addon and not the proxy: the proxy cannot stop it. The client
-- appends the realm separator itself, and it is NOT driven by any realm-id, realm-address,
-- or account-GUID field the proxy controls (each was built and tested live 2026-06-05 with
-- zero effect). Vanilla 1.12 character names are letters-only and this is a single realm,
-- so the realm portion is always empty/local and stripping at the first "-" is always safe.

local function StripRealm(name)
    if type(name) ~= "string" then return name end
    local dash = name:find("-", 1, true)
    if dash then
        return name:sub(1, dash - 1)
    end
    return name
end

-- 1) Chat sender names. The realm dash rides in on the author field of every player chat
--    event (whisper/say/guild/party/raid/channel). Strip it from the author BEFORE the
--    chat frame formats the line. Author-only: we never rewrite the message body here,
--    because that text is user-typed and may legitimately contain a "Name-..." substring
--    (the emote path below is the one exception, since emotes embed the performer name).
--
--    This deliberately does NOT wrap the global Ambiguate(). Ambiguate is read inside
--    secure UI paths -- notably the unit/guild right-click dropdown that drives Promote --
--    so replacing it with an insecure closure taints those paths and the protected action
--    is blocked (ADDON_ACTION_FORBIDDEN). There is no taint-safe way to wrap Ambiguate, so
--    each display surface is covered individually with safe mechanisms instead.
local function nameOnlyFilter(_, _, msg, author, ...)
    local cleanAuthor = StripRealm(author)
    if cleanAuthor ~= author then
        return false, msg, cleanAuthor, ...
    end
end

if type(ChatFrame_AddMessageEventFilter) == "function" then
    local NAME_EVENTS = {
        "CHAT_MSG_SAY", "CHAT_MSG_YELL",
        "CHAT_MSG_WHISPER", "CHAT_MSG_WHISPER_INFORM",
        "CHAT_MSG_GUILD", "CHAT_MSG_OFFICER",
        "CHAT_MSG_PARTY", "CHAT_MSG_PARTY_LEADER",
        "CHAT_MSG_RAID", "CHAT_MSG_RAID_LEADER", "CHAT_MSG_RAID_WARNING",
        "CHAT_MSG_CHANNEL",
    }
    for _, ev in ipairs(NAME_EVENTS) do
        ChatFrame_AddMessageEventFilter(ev, nameOnlyFilter)
    end
end

-- 2) Emotes come through the chat system and don't always pass through Ambiguate. Strip
--    the sender's realm dash from both the author field and the formatted message text.
local JP_EMOTE_DEBUG = false  -- diagnostics only; left in (off) in case another emote path surfaces
local function emoteFilter(_, event, msg, author, ...)
    local cleanAuthor = StripRealm(author)
    local newMsg = msg
    -- Strip the realm dash that follows the performer's name in the message body, whether or
    -- not the author field itself carries it ("Goop- laughs." -> "Goop laughs.").
    if type(msg) == "string" and type(cleanAuthor) == "string" and cleanAuthor ~= "" then
        local namePat = cleanAuthor:gsub("(%W)", "%%%1")
        newMsg = msg:gsub(namePat .. "%-", cleanAuthor)
    end
    if JP_EMOTE_DEBUG
       and ((type(author) == "string" and author:find("-", 1, true))
            or (type(msg) == "string" and msg:find("-", 1, true))) then
        DEFAULT_CHAT_FRAME:AddMessage("|cFFFF8800[JP-EMOTE-DBG]|r " .. tostring(event)
            .. " author='" .. tostring(author) .. "' msg='" .. tostring(msg) .. "'")
    end
    if (cleanAuthor ~= author) or (newMsg ~= msg) then
        return false, newMsg, cleanAuthor, ...
    end
end

if type(ChatFrame_AddMessageEventFilter) == "function" then
    ChatFrame_AddMessageEventFilter("CHAT_MSG_TEXT_EMOTE", emoteFilter)
    ChatFrame_AddMessageEventFilter("CHAT_MSG_EMOTE", emoteFilter)
end

-- 3) Friends list: after each refresh, strip the dash from the visible name fontstrings.
--    Best-effort and fully guarded so a UI-layout difference can only no-op, never error.
local function cleanFriendsList()
    local scroll = FriendsFrameFriendsScrollFrame or FriendsListFrameScrollFrame
    local buttons = scroll and scroll.buttons
    if not buttons then return end
    for _, button in ipairs(buttons) do
        local fs = button.name
        if fs and fs.GetText and fs.SetText then
            local txt = fs:GetText()
            local clean = StripRealm(txt)
            if clean ~= nil and clean ~= txt then
                fs:SetText(clean)
            end
        end
    end
end
if type(FriendsFrame_UpdateFriends) == "function" then
    hooksecurefunc("FriendsFrame_UpdateFriends", cleanFriendsList)
end

-- 4) Mail inbox: strip the dash from each sender fontstring after the inbox refreshes.
--    Best-effort and guarded (frame names vary by client build).
local function cleanInbox()
    for i = 1, 7 do
        local fs = _G["MailItem" .. i .. "Sender"]
        if fs and fs.GetText and fs.SetText then
            local txt = fs:GetText()
            local clean = StripRealm(txt)
            if clean ~= nil and clean ~= txt then
                fs:SetText(clean)
            end
        end
    end
end
if type(InboxFrame_Update) == "function" then
    hooksecurefunc("InboxFrame_Update", cleanInbox)
end

-- 5) Mail recipient autofill INLINE GHOST. The dropdown is handled by MailNameFix, but the
--    inline auto-completion the client ghosts into the To: editbox still carries the realm
--    dash ("Nath-"). After the text changes (including the autocomplete's own SetText), strip
--    from the first dash and re-highlight the auto-added remainder, so the suggestion UX is
--    preserved -- just without the dash. Re-entrancy guarded so our own SetText can't loop.
local stripping = false
local function fixMailInline(editBox)
    if stripping then return end
    if not editBox or not editBox.GetText then return end
    local text = editBox:GetText()
    if not text then return end
    local dash = text:find("-", 1, true)
    if not dash then return end
    local userLen = editBox:GetCursorPosition()  -- autocomplete leaves the cursor at the typed length
    local clean = text:sub(1, dash - 1)
    stripping = true
    editBox:SetText(clean)
    if userLen and userLen > 0 and userLen < #clean then
        editBox:SetCursorPosition(userLen)
        editBox:HighlightText(userLen, #clean)
    else
        editBox:SetCursorPosition(#clean)
    end
    stripping = false
end

local mailHooked = false
local function hookMailNameEditBox()
    if mailHooked then return end
    local eb = SendMailNameEditBox
    if eb and eb.HookScript then
        eb:HookScript("OnTextChanged", fixMailInline)
        mailHooked = true
    end
end
-- The mail frame is base UI, but hook on first MAIL_SHOW to be safe about load order;
-- also try immediately in case it already exists.
local mailWatcher = CreateFrame("Frame")
mailWatcher:RegisterEvent("MAIL_SHOW")
mailWatcher:SetScript("OnEvent", hookMailNameEditBox)
hookMailNameEditBox()

-- 6) Auction House Browse "Seller" column shows the cross-realm indicator "(*)" on
--    other-account/other-realm sellers -- same root cause as the dash, different glyph.
--    Seller-cell frame names are version-specific, so rather than target them we scan the
--    AuctionFrame's font strings after each list refresh and strip " (*)" wherever it appears.
--    Only strings that actually contain "(*)" are touched, so it's safe; recursion is depth-capped.
local function jpStripStar(text)
    if type(text) == "string" and text:find("(*)", 1, true) then
        return (text:gsub("%s*%(%*%)", ""))
    end
    return text
end
local function jpScanStars(frame, depth)
    if not frame or depth > 6 then return end
    if frame.GetRegions then
        for _, r in ipairs({ frame:GetRegions() }) do
            if r.GetText and r.SetText then
                local t = r:GetText()
                local c = jpStripStar(t)
                if c ~= t then r:SetText(c) end
            end
        end
    end
    if frame.GetChildren then
        for _, child in ipairs({ frame:GetChildren() }) do
            jpScanStars(child, depth + 1)
        end
    end
end
local function jpCleanAHStars()
    if AuctionFrame then jpScanStars(AuctionFrame, 0) end
end
local jpAhHooked = false
local function jpHookBrowseUpdate()
    -- Blizzard_AuctionUI is load-on-demand: AuctionFrameBrowse_Update doesn't exist until the AH
    -- is first opened. Hooking it (once it exists) makes the strip re-run on EVERY browse redraw,
    -- including scrolling -- which is why the "(*)" came back on scroll before.
    if not jpAhHooked and type(AuctionFrameBrowse_Update) == "function" then
        hooksecurefunc("AuctionFrameBrowse_Update", jpCleanAHStars)
        jpAhHooked = true
    end
end
local ahWatcher = CreateFrame("Frame")
ahWatcher:RegisterEvent("AUCTION_ITEM_LIST_UPDATE")
ahWatcher:RegisterEvent("AUCTION_HOUSE_SHOW")
ahWatcher:RegisterEvent("ADDON_LOADED")
ahWatcher:SetScript("OnEvent", function(_, evt, name)
    if evt == "ADDON_LOADED" then
        if name == "Blizzard_AuctionUI" then jpHookBrowseUpdate() end
        return
    end
    jpHookBrowseUpdate()
    if C_Timer and C_Timer.After then C_Timer.After(0, jpCleanAHStars) else jpCleanAHStars() end
end)
jpHookBrowseUpdate()  -- in case Blizzard_AuctionUI is already loaded
