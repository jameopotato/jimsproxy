local ADDON_NAME, namespace = ...

-- Moonkin Form custom sound (QoL feature).
--
-- Background: in vanilla 1.12 (and still in modern 1.14 Classic) the Druid
-- Moonkin Form spell (24858) plays the BearAttacks SoundKit (5735) — the
-- same shared sound as Bear Form, because vanilla never gave Moonkin its
-- own form-shift audio. This addon is a small QoL feature that gives the
-- balance druid form a distinct sound from Bear Form.
--
-- Triggered only when the local player casts spell 24858: temporarily mute
-- the four BearAttack FileDataIDs (so the engine's SVK chain can't play
-- them), play one of the three FOrceOfNatureAttack FileDataIDs as the
-- distinct Moonkin sound, then unmute after a short window so real bears,
-- hunter bear pets, and druid Bear Form cast by anyone else still play
-- normal sounds.

local MOONKIN_FORM_SPELL_ID = 24858

-- FileDataIDs of the four mBearAttack variants in SoundKit 5735 "BearAttacks".
-- Source: wowhead.com/classic/sound=5735/bearattacks.
local BEAR_ATTACK_FILE_IDS = {
    544953,  -- mBearAttackA
    544951,  -- mBearAttackB
    544955,  -- mBearAttackCriticalA
    544954,  -- mBearAttackD
}

-- FileDataIDs of the FOrceOfNature attack variants. Replacement screech
-- pool — one is picked at random per Moonkin Form cast so the sound has
-- some variety like vanilla's per-form variants did.
local MOONKIN_REPLACEMENT_FILE_IDS = {
    549228,  -- FOrceOfNatureAttack
    549237,  -- FOrceOfNatureAttackB
    549232,  -- FOrceOfNatureAttackC
}

-- How long to keep the bear FileDataIDs muted after the cast. Has to cover
-- the time from UNIT_SPELLCAST_SENT through whenever the engine actually
-- pulls the sound from disk. 0.6s is comfortably longer than any observed
-- delay between cast send and SVK trigger; shorter is safer for letting
-- real bears resume making sound quickly.
local MUTE_WINDOW_SECONDS = 0.6

local muteDepth = 0  -- guards against overlapping casts double-muting

local function MuteBears()
    if muteDepth == 0 then
        for _, fileId in ipairs(BEAR_ATTACK_FILE_IDS) do
            MuteSoundFile(fileId)
        end
    end
    muteDepth = muteDepth + 1
end

local function UnmuteBears()
    muteDepth = muteDepth - 1
    if muteDepth <= 0 then
        muteDepth = 0
        for _, fileId in ipairs(BEAR_ATTACK_FILE_IDS) do
            UnmuteSoundFile(fileId)
        end
    end
end

local function PlayReplacementSound()
    local pick = MOONKIN_REPLACEMENT_FILE_IDS[math.random(#MOONKIN_REPLACEMENT_FILE_IDS)]
    PlaySoundFile(pick, "SFX")
end

local frame = CreateFrame("Frame")
frame:RegisterEvent("UNIT_SPELLCAST_SENT")
frame:SetScript("OnEvent", function(_, event, unit, _, _, spellID)
    if unit ~= "player" then return end
    if spellID ~= MOONKIN_FORM_SPELL_ID then return end
    MuteBears()
    PlayReplacementSound()
    C_Timer.After(MUTE_WINDOW_SECONDS, UnmuteBears)
end)
