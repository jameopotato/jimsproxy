local ADDON_NAME, namespace = ...

-- JimsPlus TooltipFix: recolor armor / weapon type lines on tooltips when the
-- player's class can't use the item.
--
-- Why this is an addon: the 1.14 Classic Era client determines tooltip armor /
-- weapon "you can't wear this" coloring from a hardcoded class-proficiency
-- table baked into the client. Three wire paths from the proxy (legacy
-- SMSG_SET_PROFICIENCY, post-login SMSG_LEARNED_SPELLS, augmented initial
-- SMSG_SEND_KNOWN_SPELLS) all confirmed delivered to the modern client and
-- ALL ignored for tooltip color. The signal lives somewhere we can't reach
-- from the wire — but we CAN do the recolor ourselves in Lua.
--
-- Approach:
--   1. Static class-proficiency table (matches ProficiencyData.cs in proxy).
--   2. On tooltip-set-item, read itemClassID + itemSubClassID via GetItemInfo.
--   3. If player can't use that class+subclass, walk tooltip lines, find the
--      one carrying the subclass name (e.g. "Mail" / "Plate" / "Polearms"),
--      recolor it red.

local TooltipFix = {}
namespace.TooltipFix = TooltipFix

-- Item.ItemClass enum as seen by GetItemInfo on the modern 1.14 Classic
-- Era client: 2 = Weapon, 4 = Armor. (This is the reverse of the legacy
-- vanilla DBC ordering — the modern client renumbered these.)
local ITEM_CLASS_WEAPON = 2
local ITEM_CLASS_ARMOR  = 4

-- ItemSubClassArmor values.
local ARMOR_SUB = {
    CLOTH = 1, LEATHER = 2, MAIL = 3, PLATE = 4,
    SHIELD = 6, LIBRAM = 7, IDOL = 8, TOTEM = 9,
}

-- ItemSubClassWeapon values.
local WPN_SUB = {
    AXE_1H = 0, AXE_2H = 1, BOW = 2, GUN = 3,
    MACE_1H = 4, MACE_2H = 5, POLEARM = 6, SWORD_1H = 7, SWORD_2H = 8,
    STAFF = 10, FIST = 13, DAGGER = 15, THROWN = 16,
    CROSSBOW = 18, WAND = 19,
}

-- Vanilla-style weapon skills are unified per type — one "Swords" entry
-- in the Skills tab covers both 1H and 2H swords. So we map a skill book
-- entry → the list of subclasses it covers, then filter by what the class
-- can actually equip (rogue with "Swords" → 1H only, warrior → both).
--
-- Skill names are English; this is a 1.14 Classic Era English client. If
-- locale support is ever needed we can derive these via GetSpellInfo on
-- the underlying skill spell IDs, but the English path covers the user
-- base today.
local SKILL_NAME_TO_WEAPON_SUBS = {
    -- English
    ["Axes"]         = { WPN_SUB.AXE_1H, WPN_SUB.AXE_2H },
    ["Maces"]        = { WPN_SUB.MACE_1H, WPN_SUB.MACE_2H },
    ["Swords"]       = { WPN_SUB.SWORD_1H, WPN_SUB.SWORD_2H },
    ["Polearms"]     = { WPN_SUB.POLEARM },
    ["Staves"]       = { WPN_SUB.STAFF },
    ["Bows"]         = { WPN_SUB.BOW },
    ["Guns"]         = { WPN_SUB.GUN },
    ["Crossbows"]    = { WPN_SUB.CROSSBOW },
    ["Daggers"]      = { WPN_SUB.DAGGER },
    ["Thrown"]       = { WPN_SUB.THROWN },
    ["Wands"]        = { WPN_SUB.WAND },
    ["Fist Weapons"] = { WPN_SUB.FIST },
    -- German (deDE)
    ["Äxte"]              = { WPN_SUB.AXE_1H, WPN_SUB.AXE_2H },
    ["Streitkolben"]      = { WPN_SUB.MACE_1H, WPN_SUB.MACE_2H },
    ["Schwerter"]         = { WPN_SUB.SWORD_1H, WPN_SUB.SWORD_2H },
    ["Stangenwaffen"]     = { WPN_SUB.POLEARM },
    ["Stäbe"]             = { WPN_SUB.STAFF },
    ["Bögen"]             = { WPN_SUB.BOW },
    ["Schusswaffen"]      = { WPN_SUB.GUN },
    ["Armbrüste"]         = { WPN_SUB.CROSSBOW },
    ["Dolche"]            = { WPN_SUB.DAGGER },
    ["Wurfwaffen"]        = { WPN_SUB.THROWN },
    ["Zauberstäbe"]       = { WPN_SUB.WAND },
    ["Faustwaffen"]       = { WPN_SUB.FIST },
    -- French (frFR)
    ["Haches"]            = { WPN_SUB.AXE_1H, WPN_SUB.AXE_2H },
    ["Masses"]            = { WPN_SUB.MACE_1H, WPN_SUB.MACE_2H },
    ["Épées"]             = { WPN_SUB.SWORD_1H, WPN_SUB.SWORD_2H },
    ["Armes d'hast"]      = { WPN_SUB.POLEARM },
    ["Bâtons"]            = { WPN_SUB.STAFF },
    ["Arcs"]              = { WPN_SUB.BOW },
    ["Armes à feu"]       = { WPN_SUB.GUN },
    ["Arbalètes"]         = { WPN_SUB.CROSSBOW },
    ["Dagues"]            = { WPN_SUB.DAGGER },
    ["Armes de jet"]      = { WPN_SUB.THROWN },
    ["Baguettes"]         = { WPN_SUB.WAND },
    ["Armes de pugilat"]  = { WPN_SUB.FIST },
    -- Spanish (esES/esMX)
    ["Hachas"]            = { WPN_SUB.AXE_1H, WPN_SUB.AXE_2H },
    ["Mazas"]             = { WPN_SUB.MACE_1H, WPN_SUB.MACE_2H },
    ["Espadas"]           = { WPN_SUB.SWORD_1H, WPN_SUB.SWORD_2H },
    ["Armas de asta"]     = { WPN_SUB.POLEARM },
    ["Bastones"]          = { WPN_SUB.STAFF },
    ["Arcos"]             = { WPN_SUB.BOW },
    ["Armas de fuego"]    = { WPN_SUB.GUN },
    ["Ballestas"]         = { WPN_SUB.CROSSBOW },
    ["Dagas"]             = { WPN_SUB.DAGGER },
    ["Armas arrojadizas"] = { WPN_SUB.THROWN },
    ["Varitas"]           = { WPN_SUB.WAND },
    ["Armas de puño"]     = { WPN_SUB.FIST },
    -- Russian (ruRU)
    ["Топоры"]            = { WPN_SUB.AXE_1H, WPN_SUB.AXE_2H },
    ["Дробящее"]          = { WPN_SUB.MACE_1H, WPN_SUB.MACE_2H },
    ["Мечи"]              = { WPN_SUB.SWORD_1H, WPN_SUB.SWORD_2H },
    ["Древковое"]         = { WPN_SUB.POLEARM },
    ["Посохи"]            = { WPN_SUB.STAFF },
    ["Луки"]              = { WPN_SUB.BOW },
    ["Огнестрельное"]     = { WPN_SUB.GUN },
    ["Арбалеты"]          = { WPN_SUB.CROSSBOW },
    ["Кинжалы"]           = { WPN_SUB.DAGGER },
    ["Метательное"]       = { WPN_SUB.THROWN },
    ["Жезлы"]             = { WPN_SUB.WAND },
    ["Кистевое"]          = { WPN_SUB.FIST },
}

local SKILL_NAME_TO_ARMOR_SUBS = {
    -- English
    ["Cloth"]      = { ARMOR_SUB.CLOTH },
    ["Leather"]    = { ARMOR_SUB.LEATHER },
    ["Mail"]       = { ARMOR_SUB.MAIL },
    ["Plate Mail"] = { ARMOR_SUB.PLATE },
    ["Plate"]      = { ARMOR_SUB.PLATE },
    ["Shield"]     = { ARMOR_SUB.SHIELD },
    ["Shields"]    = { ARMOR_SUB.SHIELD },
    -- German (deDE)
    ["Stoff"]      = { ARMOR_SUB.CLOTH },
    ["Leder"]      = { ARMOR_SUB.LEATHER },
    ["Schwere Rüstung"] = { ARMOR_SUB.MAIL },
    ["Kette"]      = { ARMOR_SUB.MAIL },
    ["Platte"]     = { ARMOR_SUB.PLATE },
    ["Plattenrüstung"] = { ARMOR_SUB.PLATE },
    ["Schild"]     = { ARMOR_SUB.SHIELD },
    ["Schilde"]    = { ARMOR_SUB.SHIELD },
    -- French (frFR)
    ["Tissu"]      = { ARMOR_SUB.CLOTH },
    ["Cuir"]       = { ARMOR_SUB.LEATHER },
    ["Mailles"]    = { ARMOR_SUB.MAIL },
    ["Plaques"]    = { ARMOR_SUB.PLATE },
    ["Bouclier"]   = { ARMOR_SUB.SHIELD },
    ["Boucliers"]  = { ARMOR_SUB.SHIELD },
    -- Spanish (esES/esMX)
    ["Tela"]       = { ARMOR_SUB.CLOTH },
    ["Cuero"]      = { ARMOR_SUB.LEATHER },
    ["Malla"]      = { ARMOR_SUB.MAIL },
    ["Placas"]     = { ARMOR_SUB.PLATE },
    ["Escudo"]     = { ARMOR_SUB.SHIELD },
    ["Escudos"]    = { ARMOR_SUB.SHIELD },
    -- Russian (ruRU)
    ["Ткань"]      = { ARMOR_SUB.CLOTH },
    ["Кожа"]       = { ARMOR_SUB.LEATHER },
    ["Кольчуга"]   = { ARMOR_SUB.MAIL },
    ["Латы"]       = { ARMOR_SUB.PLATE },
    ["Щит"]        = { ARMOR_SUB.SHIELD },
    ["Щиты"]       = { ARMOR_SUB.SHIELD },
}

-- Class capability — which subclasses each class can EVER equip. Used to
-- disambiguate shared skills (rogue's "Swords" → 1H only).
local CLASS_CAN_USE = {
    WARRIOR = {
        armor  = { [ARMOR_SUB.CLOTH]=true, [ARMOR_SUB.LEATHER]=true, [ARMOR_SUB.MAIL]=true, [ARMOR_SUB.PLATE]=true, [ARMOR_SUB.SHIELD]=true },
        weapon = { [WPN_SUB.AXE_1H]=true, [WPN_SUB.AXE_2H]=true, [WPN_SUB.BOW]=true, [WPN_SUB.GUN]=true,
                   [WPN_SUB.MACE_1H]=true, [WPN_SUB.MACE_2H]=true, [WPN_SUB.POLEARM]=true,
                   [WPN_SUB.SWORD_1H]=true, [WPN_SUB.SWORD_2H]=true, [WPN_SUB.STAFF]=true,
                   [WPN_SUB.FIST]=true, [WPN_SUB.DAGGER]=true, [WPN_SUB.THROWN]=true, [WPN_SUB.CROSSBOW]=true },
    },
    PALADIN = {
        armor  = { [ARMOR_SUB.CLOTH]=true, [ARMOR_SUB.LEATHER]=true, [ARMOR_SUB.MAIL]=true, [ARMOR_SUB.PLATE]=true, [ARMOR_SUB.SHIELD]=true, [ARMOR_SUB.LIBRAM]=true },
        weapon = { [WPN_SUB.AXE_1H]=true, [WPN_SUB.AXE_2H]=true, [WPN_SUB.MACE_1H]=true, [WPN_SUB.MACE_2H]=true,
                   [WPN_SUB.POLEARM]=true, [WPN_SUB.SWORD_1H]=true, [WPN_SUB.SWORD_2H]=true },
    },
    HUNTER = {
        armor  = { [ARMOR_SUB.CLOTH]=true, [ARMOR_SUB.LEATHER]=true, [ARMOR_SUB.MAIL]=true },
        weapon = { [WPN_SUB.AXE_1H]=true, [WPN_SUB.AXE_2H]=true, [WPN_SUB.BOW]=true, [WPN_SUB.GUN]=true,
                   [WPN_SUB.POLEARM]=true, [WPN_SUB.SWORD_1H]=true, [WPN_SUB.SWORD_2H]=true,
                   [WPN_SUB.STAFF]=true, [WPN_SUB.FIST]=true, [WPN_SUB.DAGGER]=true,
                   [WPN_SUB.THROWN]=true, [WPN_SUB.CROSSBOW]=true },
    },
    ROGUE = {
        armor  = { [ARMOR_SUB.CLOTH]=true, [ARMOR_SUB.LEATHER]=true },
        weapon = { [WPN_SUB.BOW]=true, [WPN_SUB.GUN]=true, [WPN_SUB.MACE_1H]=true, [WPN_SUB.SWORD_1H]=true,
                   [WPN_SUB.FIST]=true, [WPN_SUB.DAGGER]=true, [WPN_SUB.THROWN]=true, [WPN_SUB.CROSSBOW]=true },
    },
    PRIEST = {
        armor  = { [ARMOR_SUB.CLOTH]=true },
        weapon = { [WPN_SUB.MACE_1H]=true, [WPN_SUB.STAFF]=true, [WPN_SUB.DAGGER]=true, [WPN_SUB.WAND]=true },
    },
    SHAMAN = {
        armor  = { [ARMOR_SUB.CLOTH]=true, [ARMOR_SUB.LEATHER]=true, [ARMOR_SUB.MAIL]=true, [ARMOR_SUB.SHIELD]=true, [ARMOR_SUB.TOTEM]=true },
        weapon = { [WPN_SUB.AXE_1H]=true, [WPN_SUB.AXE_2H]=true, [WPN_SUB.MACE_1H]=true, [WPN_SUB.MACE_2H]=true,
                   [WPN_SUB.STAFF]=true, [WPN_SUB.FIST]=true, [WPN_SUB.DAGGER]=true },
    },
    MAGE = {
        armor  = { [ARMOR_SUB.CLOTH]=true },
        weapon = { [WPN_SUB.SWORD_1H]=true, [WPN_SUB.STAFF]=true, [WPN_SUB.DAGGER]=true, [WPN_SUB.WAND]=true },
    },
    WARLOCK = {
        armor  = { [ARMOR_SUB.CLOTH]=true },
        weapon = { [WPN_SUB.SWORD_1H]=true, [WPN_SUB.STAFF]=true, [WPN_SUB.DAGGER]=true, [WPN_SUB.WAND]=true },
    },
    DRUID = {
        armor  = { [ARMOR_SUB.CLOTH]=true, [ARMOR_SUB.LEATHER]=true, [ARMOR_SUB.IDOL]=true },
        weapon = { [WPN_SUB.MACE_1H]=true, [WPN_SUB.MACE_2H]=true, [WPN_SUB.POLEARM]=true,
                   [WPN_SUB.STAFF]=true, [WPN_SUB.FIST]=true, [WPN_SUB.DAGGER]=true },
    },
}

-- Sets of subclass IDs the player has actually trained, refreshed from
-- the Skills panel on PLAYER_LOGIN and SKILL_LINES_CHANGED.
local trainedArmorSubs  = {}
local trainedWeaponSubs = {}
local proficienciesReady = false

local function RefreshTrainedProficiencies()
    wipe(trainedArmorSubs)
    wipe(trainedWeaponSubs)

    local _, classFile = UnitClass("player")
    local canUse = CLASS_CAN_USE[classFile]

    local n = GetNumSkillLines and GetNumSkillLines() or 0
    if not canUse then
        proficienciesReady = (n > 0)
        return
    end

    for i = 1, n do
        local skillName, isHeader = GetSkillLineInfo(i)
        if not isHeader and skillName then
            local wsubs = SKILL_NAME_TO_WEAPON_SUBS[skillName]
            if wsubs then
                for _, s in ipairs(wsubs) do
                    if canUse.weapon[s] then trainedWeaponSubs[s] = true end
                end
            end
            local asubs = SKILL_NAME_TO_ARMOR_SUBS[skillName]
            if asubs then
                for _, s in ipairs(asubs) do
                    if canUse.armor[s] then trainedArmorSubs[s] = true end
                end
            end
        end
    end

    proficienciesReady = (n > 0)

    if namespace.JPTT_DEBUG then
        local wlist, alist = {}, {}
        for sub in pairs(trainedWeaponSubs) do table.insert(wlist, tostring(sub)) end
        for sub in pairs(trainedArmorSubs)  do table.insert(alist, tostring(sub)) end
        table.sort(wlist) table.sort(alist)
        print(string.format("|cFFFFAA00[JimsPlus TT]|r proficiencies refreshed — weapon subs=[%s] armor subs=[%s] (skills=%d)",
            table.concat(wlist, ","), table.concat(alist, ","), n))
    end
end

-- The 1.14 Classic Era client renders abbreviated subclass labels on
-- tooltips ("Sword" / "Mace" / "Plate") rather than the full subclass
-- names returned by GetItemSubClassInfo ("One-Handed Swords" / "Maces" /
-- "Plate Mail"). We list every plausible candidate so EnforceTypeLineColor
-- can match whichever the client decided to display.
local SUBCLASS_TOOLTIP_NAMES = {
    weapon = {
        [WPN_SUB.AXE_1H]   = { "Axe", "Axes", "One-Handed Axes",
            "Axt", "Äxte", "Einhandäxte",                                          -- deDE
            "Hache", "Haches", "Haches à une main",                                -- frFR
            "Hacha", "Hachas", "Hachas de una mano",                                -- esES
            "Топор", "Топоры", "Одноручные топоры" },                               -- ruRU
        [WPN_SUB.AXE_2H]   = { "Axe", "Axes", "Two-Handed Axes",
            "Axt", "Äxte", "Zweihandäxte",
            "Hache", "Haches", "Haches à deux mains",
            "Hacha", "Hachas", "Двуручные топоры",
            "Топор", "Топоры" },
        [WPN_SUB.MACE_1H]  = { "Mace", "Maces", "One-Handed Maces",
            "Streitkolben", "Einhandstreitkolben",
            "Masse", "Masses", "Masses à une main",
            "Maza", "Mazas", "Mazas de una mano",
            "Булава", "Дробящее", "Одноручное дробящее" },
        [WPN_SUB.MACE_2H]  = { "Mace", "Maces", "Two-Handed Maces",
            "Streitkolben", "Zweihandstreitkolben",
            "Masse", "Masses", "Masses à deux mains",
            "Maza", "Mazas", "Mazas de dos manos",
            "Булава", "Дробящее", "Двуручное дробящее" },
        [WPN_SUB.SWORD_1H] = { "Sword", "Swords", "One-Handed Swords",
            "Schwert", "Schwerter", "Einhandschwerter",
            "Épée", "Épées", "Épées à une main",
            "Espada", "Espadas", "Espadas de una mano",
            "Меч", "Мечи", "Одноручные мечи" },
        [WPN_SUB.SWORD_2H] = { "Sword", "Swords", "Two-Handed Swords",
            "Schwert", "Schwerter", "Zweihandschwerter",
            "Épée", "Épées", "Épées à deux mains",
            "Espada", "Espadas", "Espadas de dos manos",
            "Меч", "Мечи", "Двуручные мечи" },
        [WPN_SUB.POLEARM]  = { "Polearm", "Polearms",
            "Stangenwaffe", "Stangenwaffen",
            "Arme d'hast", "Armes d'hast",
            "Arma de asta", "Armas de asta",
            "Древковое" },
        [WPN_SUB.STAFF]    = { "Staff", "Staves",
            "Stab", "Stäbe",
            "Bâton", "Bâtons",
            "Bastón", "Bastones",
            "Посох", "Посохи" },
        [WPN_SUB.BOW]      = { "Bow", "Bows",
            "Bogen", "Bögen",
            "Arc", "Arcs",
            "Arco", "Arcos",
            "Лук", "Луки" },
        [WPN_SUB.GUN]      = { "Gun", "Guns",
            "Schusswaffe", "Schusswaffen",
            "Arme à feu", "Armes à feu",
            "Arma de fuego", "Armas de fuego",
            "Огнестрельное" },
        [WPN_SUB.CROSSBOW] = { "Crossbow", "Crossbows",
            "Armbrust", "Armbrüste",
            "Arbalète", "Arbalètes",
            "Ballesta", "Ballestas",
            "Арбалет", "Арбалеты" },
        [WPN_SUB.DAGGER]   = { "Dagger", "Daggers",
            "Dolch", "Dolche",
            "Dague", "Dagues",
            "Daga", "Dagas",
            "Кинжал", "Кинжалы" },
        [WPN_SUB.THROWN]   = { "Thrown",
            "Wurfwaffe", "Wurfwaffen",
            "Arme de jet", "Armes de jet",
            "Arma arrojadiza", "Armas arrojadizas",
            "Метательное" },
        [WPN_SUB.WAND]     = { "Wand", "Wands",
            "Zauberstab", "Zauberstäbe",
            "Baguette", "Baguettes",
            "Varita", "Varitas",
            "Жезл", "Жезлы" },
        [WPN_SUB.FIST]     = { "Fist Weapon", "Fist Weapons",
            "Faustwaffe", "Faustwaffen",
            "Arme de pugilat", "Armes de pugilat",
            "Arma de puño", "Armas de puño",
            "Кистевое" },
    },
    armor = {
        [ARMOR_SUB.CLOTH]   = { "Cloth",
            "Stoff", "Tissu", "Tela", "Ткань" },
        [ARMOR_SUB.LEATHER] = { "Leather",
            "Leder", "Cuir", "Cuero", "Кожа" },
        [ARMOR_SUB.MAIL]    = { "Mail",
            "Kette", "Schwere Rüstung", "Mailles", "Malla", "Кольчуга" },
        [ARMOR_SUB.PLATE]   = { "Plate", "Plate Mail",
            "Platte", "Plattenrüstung", "Plaques", "Placas", "Латы" },
        [ARMOR_SUB.SHIELD]  = { "Shield", "Shields",
            "Schild", "Schilde", "Bouclier", "Boucliers",
            "Escudo", "Escudos", "Щит", "Щиты" },
        [ARMOR_SUB.LIBRAM]  = { "Libram", "Librams", "Relic",
            "Buchband", "Reliquie", "Libram", "Relique", "Escrito", "Reliquia", "Манускрипт", "Реликвия" },
        [ARMOR_SUB.IDOL]    = { "Idol", "Idols", "Relic",
            "Götze", "Götzen", "Reliquie", "Idole", "Relique", "Ídolo", "Reliquia", "Идол", "Реликвия" },
        [ARMOR_SUB.TOTEM]   = { "Totem", "Totems", "Relic",
            "Reliquie", "Relique", "Tótem", "Reliquia", "Тотем", "Реликвия" },
    },
}

-- Class-proficiency check ONLY — does NOT consider level requirement.
-- The tooltip type-line and equip-loc recolor uses just this, since
-- Blizzard already paints "Requires Level X" red natively. The merchant
-- icon tinting layers on a separate level check (see FixMerchantUsability).
local function PlayerCanUse(itemClassId, itemSubClassId)
    -- Don't recolor anything until we've successfully read the skill book
    -- — otherwise a slow-loading skill list would mean every item flashes
    -- red on the first tooltip.
    if not proficienciesReady then return true end

    local _, classFile = UnitClass("player")

    if itemClassId == ITEM_CLASS_ARMOR then
        -- Relic types: paladin librams, druid idols, shaman totems. These
        -- aren't trained — they're hard class-locked.
        if itemSubClassId == ARMOR_SUB.LIBRAM then return classFile == "PALADIN" end
        if itemSubClassId == ARMOR_SUB.IDOL   then return classFile == "DRUID"   end
        if itemSubClassId == ARMOR_SUB.TOTEM  then return classFile == "SHAMAN"  end
        local trained = trainedArmorSubs[itemSubClassId] == true
        if namespace.JPTT_DEBUG then
            print(string.format("|cFFFFAA00[JimsPlus TT]|r PlayerCanUse=%s (armor) sub=%s",
                tostring(trained), tostring(itemSubClassId)))
        end
        return trained
    elseif itemClassId == ITEM_CLASS_WEAPON then
        if itemSubClassId == 14 or itemSubClassId == 20 then
            return true
        end
        local trained = trainedWeaponSubs[itemSubClassId] == true
        if namespace.JPTT_DEBUG then
            print(string.format("|cFFFFAA00[JimsPlus TT]|r PlayerCanUse=%s (weapon) sub=%s",
                tostring(trained), tostring(itemSubClassId)))
        end
        return trained
    end

    return true -- non-armor/weapon items: don't recolor
end

local function PlayerMeetsLevel(itemMinLevel)
    if not itemMinLevel or itemMinLevel <= 0 then return true end
    local lvl = UnitLevel("player") or 1
    return lvl >= itemMinLevel
end

-- Walks tooltip lines looking for any FontString whose text matches one
-- of the candidate subclass display names, and explicitly sets its color.
-- We always set a color (red when off-class, white when usable) so the
-- modern client's own — and broken-on-vanilla-data — recolor doesn't
-- leak through and so previous-hover red doesn't persist on reused
-- FontStrings.
local function EnforceTypeLineColor(tooltip, candidateNames, r, g, b)
    if not candidateNames or #candidateNames == 0 then return end

    local tooltipName = tooltip:GetName()
    if not tooltipName then return end

    local nameSet = {}
    for _, n in ipairs(candidateNames) do nameSet[n] = true end

    local numLines = tooltip:NumLines()
    for i = 1, numLines do
        local rightFs = _G[tooltipName .. "TextRight" .. i]
        if rightFs then
            local text = rightFs:GetText()
            if text and nameSet[text] then
                rightFs:SetTextColor(r, g, b)
            end
        end
        local leftFs = _G[tooltipName .. "TextLeft" .. i]
        if leftFs then
            local text = leftFs:GetText()
            if text and nameSet[text] then
                leftFs:SetTextColor(r, g, b)
            end
        end
    end
end

-- Items the player has NEVER touched aren't in the client's item cache,
-- so GetItemInfo returns nil for everything past the link itself. We track
-- pending items per-tooltip so GET_ITEM_INFO_RECEIVED can refresh them.
local pendingTooltips = {}

local function ApplyTooltipFeatures(tooltip, link)
    if not namespace.db then return end
    if not link then return end

    local itemName, _, _, _, itemMinLevel, itemType, itemSubType, _, equipLoc, _, _, classId, subClassId = GetItemInfo(link)

    if namespace.JPTT_DEBUG then
        print(string.format("|cFFFFAA00[JimsPlus TT]|r apply name=%s classId=%s subClassId=%s equipLoc=%s itemType=%s itemSubType=%s minLvl=%s tooltipFix=%s",
            tostring(itemName), tostring(classId), tostring(subClassId), tostring(equipLoc),
            tostring(itemType), tostring(itemSubType), tostring(itemMinLevel),
            tostring(namespace.db.tooltipFix)))
    end

    -- If the item isn't cached yet, register it for retry once
    -- GET_ITEM_INFO_RECEIVED fires.
    if not classId or not subClassId then
        local itemId = tonumber(string.match(link, "item:(%d+)"))
        if itemId then
            pendingTooltips[itemId] = pendingTooltips[itemId] or {}
            pendingTooltips[itemId][tooltip] = link
        end
        return
    end

    -- Always set the color of the type line — white when usable, red when
    -- not. This both recolors off-class items AND undoes any incorrect
    -- red the modern client (or a previous hover) painted on the line.
    if namespace.db.tooltipFix and (classId == ITEM_CLASS_ARMOR or classId == ITEM_CLASS_WEAPON) then
        local subclassNames
        if classId == ITEM_CLASS_ARMOR then
            subclassNames = SUBCLASS_TOOLTIP_NAMES.armor[subClassId]
        else
            subclassNames = SUBCLASS_TOOLTIP_NAMES.weapon[subClassId]
        end
        -- Build candidate list: subclass display names ONLY. We deliberately
        -- skip the equip-loc string (e.g. "Two-Hand", "Finger", "Waist",
        -- "Main Hand") — slot/hand lines have no proficiency relationship:
        -- everyone can wear a ring, everyone can equip an offhand slot, and
        -- one-hand weapons are the same hand-type regardless of weapon
        -- subclass. The "you can't use this" signal lives entirely on the
        -- subclass line (Sword/Mace/Leather/Plate/etc.), so that's the only
        -- line we recolor.
        --
        -- Bonus side-effect: items whose subclass has no SUBCLASS_TOOLTIP_NAMES
        -- entry (Fishing Poles subClass 20, Miscellaneous subClass 14 for
        -- Mining Picks / Blacksmith Hammers / similar profession tools) now
        -- get an empty candidate list and no recolor — which is the desired
        -- behavior since those items have no proficiency requirement.
        local candidateNames = {}
        if subclassNames then
            for _, n in ipairs(subclassNames) do table.insert(candidateNames, n) end
        end
        if #candidateNames > 0 then
            -- Tooltip type-line color reflects ONLY proficiency, not level.
            -- Blizzard already shows "Requires Level X" in red natively
            -- when under-level — recoloring "Waist" / "Leather" red on a
            -- belt the rogue can wear (just not yet) would be misleading.
            local usable = PlayerCanUse(classId, subClassId)
            if namespace.JPTT_DEBUG then
                print(string.format("|cFFFFAA00[JimsPlus TT]|r enforce color (%s) sub=%s names=[%s]",
                    usable and "white/usable" or "red/off-class",
                    tostring(subClassId),
                    table.concat(candidateNames, ",")))
            end
            if usable then
                EnforceTypeLineColor(tooltip, candidateNames, 1.0, 1.0, 1.0)
            else
                EnforceTypeLineColor(tooltip, candidateNames, 1.0, 0.1, 0.1)
            end
        end
    end
end

local function OnTooltipItem(tooltip)
    if not namespace.db then return end

    local _, link = tooltip:GetItem()
    if not link then return end

    if namespace.JPTT_DEBUG then
        print("|cFFFFAA00[JimsPlus TT]|r hook fired link=" .. tostring(link))
    end

    ApplyTooltipFeatures(tooltip, link)
end

-- The merchant window tints item icons red for "you can't use this" using
-- the same broken proficiency check as the tooltip — so a rogue sees
-- 1H Swords/Daggers/Crossbows tinted red (bad) while a 2H Axe sits white
-- (also bad). Re-tint each visible button after the default UI is done.
local function FixMerchantUsability()
    if namespace.JPTT_DEBUG then
        print(string.format("|cFFFFAA00[JimsPlus TT]|r FixMerchantUsability entered (db=%s tooltipFix=%s mfShown=%s tab=%s)",
            tostring(namespace.db ~= nil),
            tostring(namespace.db and namespace.db.tooltipFix),
            tostring(MerchantFrame and MerchantFrame:IsShown()),
            tostring(MerchantFrame and MerchantFrame.selectedTab)))
    end
    if not namespace.db or not namespace.db.tooltipFix then return end
    if not MerchantFrame then return end
    if MerchantFrame.selectedTab and MerchantFrame.selectedTab ~= 1 then return end

    local perPage = MERCHANT_ITEMS_PER_PAGE or 12
    local page = MerchantFrame.page or 1
    local total = GetMerchantNumItems and GetMerchantNumItems() or 0

    for i = 1, perPage do
        local index = (page - 1) * perPage + i
        local button = _G["MerchantItem" .. i .. "ItemButton"]
        if button and index <= total then
            local link = GetMerchantItemLink(index)
            if link then
                local _, _, quality, _, itemMinLevel, _, _, _, _, _, _, classId, subClassId = GetItemInfo(link)
                if classId then
                    -- Merchant icon tint layers proficiency + level
                    -- together. Non-armor/weapon items (projectiles,
                    -- consumables, reagents) skip the proficiency check
                    -- so we override whatever incorrect red the modern
                    -- client painted on.
                    local proficient = true
                    if classId == ITEM_CLASS_ARMOR or classId == ITEM_CLASS_WEAPON then
                        proficient = PlayerCanUse(classId, subClassId)
                    end
                    local usable = proficient and PlayerMeetsLevel(itemMinLevel)
                    local r, g, b
                    if usable then r, g, b = 1.0, 1.0, 1.0
                    else            r, g, b = 0.9, 0.0, 0.0 end

                    -- Icon + border tint.
                    if SetItemButtonTextureVertexColor       then SetItemButtonTextureVertexColor(button, r, g, b)       end
                    if SetItemButtonNormalTextureVertexColor then SetItemButtonNormalTextureVertexColor(button, r, g, b) end

                    -- Slot background — wrap in pcall: not all 1.14 builds
                    -- have the underlying texture.
                    if SetItemButtonSlotVertexColor then
                        pcall(SetItemButtonSlotVertexColor, button, r, g, b)
                    end

                    -- Newer-template IconBorder/IconOverlay (the colored
                    -- frame around the icon).
                    if button.IconBorder and button.IconBorder.SetVertexColor then
                        button.IconBorder:SetVertexColor(r, g, b)
                    end
                    if button.IconOverlay and button.IconOverlay.SetVertexColor then
                        button.IconOverlay:SetVertexColor(r, g, b)
                    end

                    -- Brute-force sweep: walk every Texture child of the
                    -- button and force vertex color, skipping the icon
                    -- image itself (we want the actual item art untouched
                    -- — its color is set via SetItemButtonTextureVertexColor
                    -- above). Catches anonymous border/overlay textures
                    -- the named helpers don't reach.
                    local iconName = "MerchantItem" .. i .. "ItemButtonIconTexture"
                    for _, region in ipairs({ button:GetRegions() }) do
                        if region.IsObjectType and region:IsObjectType("Texture") and region:GetName() ~= iconName then
                            if region.SetVertexColor then
                                region:SetVertexColor(r, g, b)
                            end
                            if namespace.JPTT_DEBUG and i == 1 then
                                print(string.format("|cFFFFAA00[JimsPlus TT]|r merchant tex name=%s", tostring(region:GetName())))
                            end
                        end
                    end

                    -- Row-level textures (live on MerchantItem%d, not on
                    -- ItemButton). NameFrame is the backdrop behind the
                    -- name + price; SlotTexture is the dark inset behind
                    -- the icon button.
                    local rowNameFrame = _G["MerchantItem" .. i .. "NameFrame"]
                    if rowNameFrame and rowNameFrame.SetVertexColor then
                        rowNameFrame:SetVertexColor(r, g, b)
                    end
                    local rowSlot = _G["MerchantItem" .. i .. "SlotTexture"]
                    if rowSlot and rowSlot.SetVertexColor then
                        rowSlot:SetVertexColor(r, g, b)
                    end

                    -- Leave item name text at the client's default yellow.
                    -- Proficiency is communicated via icon/border tinting;
                    -- vanilla never recolors the name text for proficiency.
                end
            end
        end
    end
end

-- Quest-reward window mirrors the merchant icon-tint logic. Same broken
-- 1.14 client proficiency check paints "you can't use this" red on the
-- wrong rewards (rogue sees 1H swords red, 2H axes white, etc.) — and
-- worse for quest rewards, since picking the wrong choice is one-shot.
-- Re-tint each visible reward button after QuestInfo_Display populates them.
local function FixQuestRewardUsability()
    if not namespace.db or not namespace.db.tooltipFix then return end
    local rewardsFrame = _G.QuestInfoRewardsFrame
    if not rewardsFrame then return end
    local buttons = rewardsFrame.RewardButtons
    if not buttons then return end

    if namespace.JPTT_DEBUG then
        print(string.format("|cFFFFAA00[JimsPlus TT]|r FixQuestRewardUsability entered (buttons=%d)",
            #buttons))
    end

    for _, button in ipairs(buttons) do
        if button and button:IsShown() and button.GetID and button:GetID() > 0 then
            -- button.type set by QuestInfo_ShowRewards: "choice" or "reward".
            -- Default to "reward" for non-choice fixed rewards if unset.
            local rtype = button.type or "reward"
            local link = GetQuestItemLink and GetQuestItemLink(rtype, button:GetID())
            if link then
                local _, _, quality, _, itemMinLevel, _, _, _, _, _, _, classId, subClassId = GetItemInfo(link)
                if classId then
                    local proficient = true
                    if classId == ITEM_CLASS_ARMOR or classId == ITEM_CLASS_WEAPON then
                        proficient = PlayerCanUse(classId, subClassId)
                    end
                    local usable = proficient and PlayerMeetsLevel(itemMinLevel)
                    local r, g, b
                    if usable then r, g, b = 1.0, 1.0, 1.0
                    else            r, g, b = 0.9, 0.0, 0.0 end

                    if SetItemButtonTextureVertexColor       then SetItemButtonTextureVertexColor(button, r, g, b)       end
                    if SetItemButtonNormalTextureVertexColor then SetItemButtonNormalTextureVertexColor(button, r, g, b) end
                    if SetItemButtonSlotVertexColor then
                        pcall(SetItemButtonSlotVertexColor, button, r, g, b)
                    end
                    if button.IconBorder and button.IconBorder.SetVertexColor then
                        button.IconBorder:SetVertexColor(r, g, b)
                    end
                    if button.IconOverlay and button.IconOverlay.SetVertexColor then
                        button.IconOverlay:SetVertexColor(r, g, b)
                    end

                    -- Brute-force sweep on button regions, skip the icon
                    -- image itself (its color is set via the helper above).
                    local btnName = button.GetName and button:GetName() or ""
                    local iconName = btnName ~= "" and (btnName .. "IconTexture") or nil
                    if button.GetRegions then
                        for _, region in ipairs({ button:GetRegions() }) do
                            if region.IsObjectType and region:IsObjectType("Texture")
                               and (not iconName or region:GetName() ~= iconName) then
                                if region.SetVertexColor then
                                    region:SetVertexColor(r, g, b)
                                end
                            end
                        end
                    end

                    -- Leave reward name text at the client's default color.
                    -- Proficiency is communicated via icon/border tinting;
                    -- vanilla never recolors the name text for proficiency.
                end
            end
        end
    end
end

local questHooked = false
local function HookQuestRewards()
    if questHooked then return end
    local hookedAny = false
    -- QuestInfo_Display is the central populate-the-quest-frame function
    -- used by accept / progress / complete panels. Hooking it covers every
    -- code path that lays out reward buttons.
    if _G.QuestInfo_Display then
        hooksecurefunc("QuestInfo_Display", FixQuestRewardUsability)
        hookedAny = true
    end
    -- Belt-and-suspenders: hook the dedicated reward-show function too in
    -- case some flow updates rewards without re-running QuestInfo_Display.
    if _G.QuestInfo_ShowRewards then
        hooksecurefunc("QuestInfo_ShowRewards", FixQuestRewardUsability)
        hookedAny = true
    end
    questHooked = hookedAny
    if namespace.JPTT_DEBUG then
        print(string.format("|cFFFFAA00[JimsPlus TT]|r HookQuestRewards attempted (Display=%s ShowRewards=%s)",
            tostring(_G.QuestInfo_Display ~= nil),
            tostring(_G.QuestInfo_ShowRewards ~= nil)))
    end
end

local merchantHooked = false
local function HookMerchantFrame()
    if merchantHooked then return end

    local hookedAny = false
    if _G.MerchantFrame_UpdateMerchantInfo then
        hooksecurefunc("MerchantFrame_UpdateMerchantInfo", FixMerchantUsability)
        hookedAny = true
    end
    if _G.MerchantFrame_Update then
        hooksecurefunc("MerchantFrame_Update", FixMerchantUsability)
        hookedAny = true
    end
    if MerchantFrame and MerchantFrame.HookScript then
        MerchantFrame:HookScript("OnShow", FixMerchantUsability)
        hookedAny = true
    end

    -- Fallback: throttled OnUpdate poller. Runs every 0.25s while the
    -- merchant is open so we can't miss whatever update path Blizzard uses.
    if MerchantFrame and MerchantFrame.HookScript then
        local accum = 0
        MerchantFrame:HookScript("OnUpdate", function(_, elapsed)
            accum = accum + (elapsed or 0)
            if accum >= 0.25 then
                accum = 0
                FixMerchantUsability()
            end
        end)
    end

    merchantHooked = hookedAny
    if namespace.JPTT_DEBUG then
        print(string.format("|cFFFFAA00[JimsPlus TT]|r HookMerchantFrame attempted (UpdateMerchantInfo=%s Update=%s OnShow=%s)",
            tostring(_G.MerchantFrame_UpdateMerchantInfo ~= nil),
            tostring(_G.MerchantFrame_Update ~= nil),
            tostring(MerchantFrame ~= nil)))
    end
end

local function HookTooltips()
    -- Cover both the main GameTooltip and the comparison shopping tooltips.
    -- The 1.14 Classic Era client supports OnTooltipSetItem on these.
    local tooltips = {
        GameTooltip,
        ItemRefTooltip,
        ShoppingTooltip1,
        ShoppingTooltip2,
    }
    for _, tip in ipairs(tooltips) do
        if tip and tip.HookScript then
            tip:HookScript("OnTooltipSetItem", OnTooltipItem)
        end
    end
end

function TooltipFix:Init()
    HookTooltips()
end

namespace:RegisterModule("TooltipFix", function() TooltipFix:Init() end)

-- Hook tooltips at PLAYER_LOGIN once GameTooltip and friends exist.
-- Hook unconditionally — the OnTooltipItem callback gates each feature
-- (tooltipFix / tooltipCompare) independently against the saved-vars
-- so a /reload after toggling either checkbox immediately takes effect.
local f = CreateFrame("Frame")
f:RegisterEvent("PLAYER_LOGIN")
f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:RegisterEvent("SKILL_LINES_CHANGED")
f:RegisterEvent("LEARNED_SPELL_IN_TAB")
f:RegisterEvent("GET_ITEM_INFO_RECEIVED")
f:RegisterEvent("MERCHANT_SHOW")
f:RegisterEvent("MERCHANT_UPDATE")
f:RegisterEvent("QUEST_DETAIL")
f:RegisterEvent("QUEST_PROGRESS")
f:RegisterEvent("QUEST_COMPLETE")
f:SetScript("OnEvent", function(_, event, arg1)
    if event == "PLAYER_LOGIN" then
        JimsPlusDB = JimsPlusDB or {}
        namespace.db = JimsPlusDB
        if JimsPlusDB.tooltipFix == nil then JimsPlusDB.tooltipFix = true end
        HookTooltips()
        HookMerchantFrame()
        HookQuestRewards()
        RefreshTrainedProficiencies()
    elseif event == "PLAYER_ENTERING_WORLD"
        or event == "SKILL_LINES_CHANGED"
        or event == "LEARNED_SPELL_IN_TAB" then
        RefreshTrainedProficiencies()
    elseif event == "MERCHANT_SHOW" or event == "MERCHANT_UPDATE" then
        HookMerchantFrame() -- in case Blizzard's merchant code loaded late
        FixMerchantUsability()
    elseif event == "QUEST_DETAIL" or event == "QUEST_PROGRESS" or event == "QUEST_COMPLETE" then
        HookQuestRewards() -- in case Blizzard's QuestInfo code loaded late
        FixQuestRewardUsability()
    elseif event == "GET_ITEM_INFO_RECEIVED" then
        local itemId = arg1
        local pendings = pendingTooltips[itemId]
        if pendings then
            pendingTooltips[itemId] = nil
            for tooltip, link in pairs(pendings) do
                -- Only re-apply if the tooltip is still showing this same item.
                if tooltip:IsShown() then
                    local _, currentLink = tooltip:GetItem()
                    if currentLink == link then
                        ApplyTooltipFeatures(tooltip, link)
                    end
                end
            end
        end
        -- Late-cached merchant items: refresh button tints when an item arrives.
        if MerchantFrame and MerchantFrame:IsShown() then
            FixMerchantUsability()
        end
        -- Late-cached quest reward items: same treatment.
        if QuestFrame and QuestFrame:IsShown() then
            FixQuestRewardUsability()
        end
    end
end)

-- Slash command for diagnosing whether the OnTooltipSetItem hook is firing.
-- "/jptt debug on" → prints a chat line every time the hook runs.
SLASH_JIMSPLUSTOOLTIP1 = "/jptt"
SlashCmdList["JIMSPLUSTOOLTIP"] = function(msg)
    msg = (msg or ""):lower():gsub("^%s+", ""):gsub("%s+$", "")
    if msg == "debug on" then
        namespace.JPTT_DEBUG = true
        print("|cFF00FF00[JimsPlus]|r TooltipFix debug ON. Hover an item.")
    elseif msg == "debug off" then
        namespace.JPTT_DEBUG = false
        print("|cFF00FF00[JimsPlus]|r TooltipFix debug OFF.")
    else
        print("|cFF00FF00[JimsPlus]|r /jptt debug on  — turn on hook-fired prints")
        print("|cFF00FF00[JimsPlus]|r /jptt debug off — turn off")
        local has = (GameTooltip and GameTooltip.GetScript and GameTooltip:GetScript("OnTooltipSetItem")) and "yes" or "no"
        print("|cFF00FF00[JimsPlus]|r OnTooltipSetItem script installed: " .. has)
    end
end
