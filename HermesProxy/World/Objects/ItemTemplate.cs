using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HermesProxy.Enums;

namespace HermesProxy.World.Objects;

public class ItemTemplate
{
    public uint Entry;
    public int Class;
    public uint SubClass;
    public int SoundOverrideSubclass;
    public string[] Name = new string[4];
    public uint DisplayID;
    public int Quality;
    public uint Flags;
    public uint FlagsExtra;
    public uint BuyCount;
    public uint BuyPrice;
    public uint SellPrice;
    public int InventoryType;
    public int AllowedClasses;
    public int AllowedRaces;
    public uint ItemLevel;
    public uint RequiredLevel;
    public uint RequiredSkillId;
    public uint RequiredSkillLevel;
    public uint RequiredSpell;
    public uint RequiredHonorRank;
    public uint RequiredCityRank;
    public uint RequiredRepFaction;
    public uint RequiredRepValue;
    public int MaxCount;
    public int MaxStackSize;
    public uint ContainerSlots;
    public uint StatsCount;
    public int[] StatTypes = new int[10];
    public int[] StatValues = new int[10];
    public int ScalingStatDistribution;
    public uint ScalingStatValue;
    public float[] DamageMins = new float[5];
    public float[] DamageMaxs = new float[5];
    public int[] DamageTypes = new int[5];
    public uint Armor;
    public uint HolyResistance;
    public uint FireResistance;
    public uint NatureResistance;
    public uint FrostResistance;
    public uint ShadowResistance;
    public uint ArcaneResistance;
    public uint Delay;
    public int AmmoType;
    public float RangedMod;
    public int[] TriggeredSpellIds = new int[5];
    public int[] TriggeredSpellTypes = new int[5];
    public int[] TriggeredSpellCharges = new int[5];
    public int[] TriggeredSpellCooldowns = new int[5];
    public uint[] TriggeredSpellCategories = new uint[5];
    public int[] TriggeredSpellCategoryCooldowns = new int[5];
    public int Bonding;
    public string Description = string.Empty;
    public uint PageText;
    public int Language;
    public int PageMaterial;
    public uint StartQuestId;
    public uint LockId;
    public int Material;
    public int SheathType;
    public int RandomProperty;
    public uint RandomSuffix;
    public uint Block;
    public uint ItemSet;
    public uint MaxDurability;
    public uint AreaID;
    public int MapID;
    public uint BagFamily;
    public int TotemCategory;
    public int[] ItemSocketColors = new int[3];
    public uint[] SocketContent = new uint[3];
    public int SocketBonus;
    public int GemProperties;
    public int RequiredDisenchantSkill;
    public float ArmorDamageModifier;
    public uint Duration;
    public int ItemLimitCategory;
    public int HolidayID;

    public void ReadFromLegacyPacket(uint entry, WorldPacket packet)
    {
        Entry = entry;

        // Packet start
        Class = packet.ReadInt32();
        SubClass = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_3_6299))
            SoundOverrideSubclass = packet.ReadInt32();

        for (int i = 0; i < 4; i++)
            Name[i] = packet.ReadCString();

        DisplayID = packet.ReadUInt32();

        Quality = packet.ReadInt32();

        Flags = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            FlagsExtra = packet.ReadUInt32();

        BuyPrice = packet.ReadUInt32();

        SellPrice = packet.ReadUInt32();

        InventoryType = packet.ReadInt32();

        AllowedClasses = packet.ReadInt32();

        AllowedRaces = packet.ReadInt32();

        ItemLevel = packet.ReadUInt32();

        RequiredLevel = packet.ReadUInt32();

        RequiredSkillId = packet.ReadUInt32();

        RequiredSkillLevel = packet.ReadUInt32();

        RequiredSpell = packet.ReadUInt32();

        RequiredHonorRank = packet.ReadUInt32();

        RequiredCityRank = packet.ReadUInt32();

        RequiredRepFaction = packet.ReadUInt32();

        RequiredRepValue = packet.ReadUInt32();

        MaxCount = packet.ReadInt32();

        MaxStackSize = packet.ReadInt32();

        ContainerSlots = packet.ReadUInt32();

        StatsCount = LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056) ? packet.ReadUInt32() : 10;
        if (StatsCount > 10)
        {
            StatTypes = new int[StatsCount];
            StatValues = new int[StatsCount];
        }
        for (int i = 0; i < StatsCount; i++)
        {
            StatTypes[i] = packet.ReadInt32();
            StatValues[i] = packet.ReadInt32();
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            ScalingStatDistribution = packet.ReadInt32();
            ScalingStatValue = packet.ReadUInt32();
        }

        int dmgCount = LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767) ? 2 : 5;
        for (int i = 0; i < dmgCount; i++)
        {
            DamageMins[i] = packet.ReadFloat();
            DamageMaxs[i] = packet.ReadFloat();
            DamageTypes[i] = packet.ReadInt32();
        }

        Armor = packet.ReadUInt32();
        HolyResistance = packet.ReadUInt32();
        FireResistance = packet.ReadUInt32();
        NatureResistance = packet.ReadUInt32();
        FrostResistance = packet.ReadUInt32();
        ShadowResistance = packet.ReadUInt32();
        ArcaneResistance = packet.ReadUInt32();

        Delay = packet.ReadUInt32();

        AmmoType = packet.ReadInt32();

        RangedMod = packet.ReadFloat();

        for (byte i = 0; i < 5; i++)
        {
            TriggeredSpellIds[i] = packet.ReadInt32();
            TriggeredSpellTypes[i] = packet.ReadInt32();
            TriggeredSpellCharges[i] = packet.ReadInt32();
            TriggeredSpellCooldowns[i] = packet.ReadInt32();
            TriggeredSpellCategories[i] = packet.ReadUInt32();
            TriggeredSpellCategoryCooldowns[i] = packet.ReadInt32();

            if (TriggeredSpellIds[i] != 0)
                GameData.SaveItemEffectSlot(Entry, (uint)TriggeredSpellIds[i], i);
        }

        Bonding = packet.ReadInt32();

        Description = packet.ReadCString();

        PageText = packet.ReadUInt32();

        Language = packet.ReadInt32();

        PageMaterial = packet.ReadInt32();

        StartQuestId = packet.ReadUInt32();

        LockId = packet.ReadUInt32();

        Material = packet.ReadInt32();

        // in modern client files, there are no items with material -1 instead of 0
        // change it so we dont need to send hotfix for this
        if (Material < 0)
            Material = 0;

        SheathType = packet.ReadInt32();

        RandomProperty = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            RandomSuffix = packet.ReadUInt32();

        Block = packet.ReadUInt32();

        ItemSet = packet.ReadUInt32();

        MaxDurability = packet.ReadUInt32();

        AreaID = packet.ReadUInt32();

        // In this single (?) case, map 0 means no map
        MapID = packet.ReadInt32();

        //MIRASU: Translate Vanilla (1.12) BagFamily bitmask to the modern (1.14+) bitmask.
        //MIRASU: In Vanilla the ItemBagFamily.dbc row order was Quiver, Ammo, Soul, Keyring, Herb,
        //MIRASU: Enchanting, Engineering, Gems, Mining, Leatherworking (1..10 -> bits 0..9).
        //MIRASU: In modern clients the order is Arrows, Bullets, Soul, Leatherworking, Inscription,
        //MIRASU: Herbs, Enchanting, Engineering, Keyring, Gems, Mining (1..11 -> bits 0..10).
        //MIRASU: The Keyring bit is the critical one for this bug: Vanilla bit 3 (=8) must map to
        //MIRASU: modern bit 8 (=256) so the 1.14 client shows the Key Ring tab and routes keys into it.
        //MIRASU: We also remap the other families best-effort; unrecognised bits are passed through
        //MIRASU: unchanged so this is never worse than the prior behaviour.
        uint rawBagFamily = packet.ReadUInt32();
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            uint translated = 0;
            if ((rawBagFamily & 0x001) != 0) translated |= 0x001;   // Quiver        -> Arrows
            if ((rawBagFamily & 0x002) != 0) translated |= 0x002;   // Ammo Pouch    -> Bullets
            if ((rawBagFamily & 0x004) != 0) translated |= 0x004;   // Soul Bag      -> Soul Shards
            if ((rawBagFamily & 0x008) != 0) translated |= 0x100;   // Key Ring      -> Keyring (bit 8)
            if ((rawBagFamily & 0x010) != 0) translated |= 0x020;   // Herb Bag      -> Herbs
            if ((rawBagFamily & 0x020) != 0) translated |= 0x040;   // Enchanting    -> Enchanting
            if ((rawBagFamily & 0x040) != 0) translated |= 0x080;   // Engineering   -> Engineering
            if ((rawBagFamily & 0x080) != 0) translated |= 0x200;   // Gem Bag       -> Gems
            if ((rawBagFamily & 0x100) != 0) translated |= 0x400;   // Mining Bag    -> Mining
            if ((rawBagFamily & 0x200) != 0) translated |= 0x008;   // Leatherworking-> Leatherworking
            //MIRASU: Preserve any bits we didn't recognise (pass-through) so we never worsen behaviour
            uint recognisedMask = 0x3FF; // bits 0..9
            translated |= (rawBagFamily & ~recognisedMask);
            BagFamily = translated;
        }
        else
        {
            BagFamily = rawBagFamily;
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            TotemCategory = packet.ReadInt32();

            for (int i = 0; i < 3; i++)
            {
                ItemSocketColors[i] = packet.ReadInt32();
                SocketContent[i] = packet.ReadUInt32();
            }

            SocketBonus = packet.ReadInt32();

            GemProperties = packet.ReadInt32();

            RequiredDisenchantSkill = packet.ReadInt32();

            ArmorDamageModifier = packet.ReadFloat();
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_4_2_8209))
            Duration = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            ItemLimitCategory = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
            HolidayID = packet.ReadInt32();

        // JimsProxy: derive RequiredSkillId from item Class+SubClass when the
        // server sends 0 (no skill specified). Some 1.12 servers (Kronos in
        // particular) leave RequiredSkillId blank for items where the modern
        // 1.14 client expects a populated value to render the tooltip's
        // "Requires <Skill>" line in red for unmet requirements. The item is
        // still correctly flagged as unequippable (red border in inventory),
        // but the tooltip TEXT explaining WHY isn't colored. Other 1.12
        // servers (Ashen-wow) populate this field correctly, so deriving as
        // a fallback closes the Kronos gap without overriding good data.
        // Mappings sourced from TrinityCore ItemPrototype::GetSkill().
        if (RequiredSkillId == 0)
            RequiredSkillId = DeriveSkillFromItemType(Class, SubClass);
    }

    // JimsProxy: ItemClass+SubClass -> SkillLine.dbc id mapping. Returns 0
    // for unknown combinations (caller leaves RequiredSkillId at 0 unchanged).
    private static uint DeriveSkillFromItemType(int itemClass, uint subClass)
    {
        // Item Class 2 = Weapon
        if (itemClass == 2)
        {
            return subClass switch
            {
                0  => 44,   // 1H Axe       -> SKILL_AXES
                1  => 172,  // 2H Axe       -> SKILL_2H_AXES
                2  => 45,   // Bow          -> SKILL_BOWS
                3  => 46,   // Gun          -> SKILL_GUNS
                4  => 54,   // 1H Mace      -> SKILL_MACES
                5  => 160,  // 2H Mace      -> SKILL_2H_MACES
                6  => 229,  // Polearm      -> SKILL_POLEARMS
                7  => 43,   // 1H Sword     -> SKILL_SWORDS
                8  => 55,   // 2H Sword     -> SKILL_2H_SWORDS
                10 => 136,  // Staff        -> SKILL_STAVES
                13 => 473,  // Fist Weapon  -> SKILL_FIST_WEAPONS
                15 => 173,  // Dagger       -> SKILL_DAGGERS
                16 => 176,  // Thrown       -> SKILL_THROWN
                18 => 226,  // Crossbow     -> SKILL_CROSSBOWS
                19 => 228,  // Wand         -> SKILL_WANDS
                20 => 356,  // Fishing Pole -> SKILL_FISHING
                _  => 0,
            };
        }
        // Item Class 4 = Armor
        if (itemClass == 4)
        {
            return subClass switch
            {
                1 => 415,  // Cloth     -> SKILL_CLOTH
                2 => 414,  // Leather   -> SKILL_LEATHER
                3 => 413,  // Mail      -> SKILL_MAIL
                4 => 293,  // Plate     -> SKILL_PLATE_MAIL
                6 => 433,  // Shield    -> SKILL_SHIELD
                _ => 0,
            };
        }
        return 0;
    }
}
