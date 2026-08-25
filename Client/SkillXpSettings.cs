using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;

namespace SkillPointsMod.Client;

// Client-only XP scaling system, replacing SVM's globals.json-based
// SkillProgressRate/WeaponSkillProgressRate/fatigue settings -- those are
// static server-side DB config cached in memory at server boot, so editing
// them (e.g. via Greed) needs a server restart to take effect. Everything
// here is editable live via BepInEx's Configuration Manager (F12) instead:
//   - Fatigue settings are applied by writing straight into
//     Singleton<GlobalConfiguration>.Instance's own plain public fields.
//     No Harmony patch needed for these -- confirmed by decompiling
//     GetEffectiveness/UseEffectiveness, which read them fresh on every
//     single call, no caching -- so a live field write takes effect on the
//     very next skill-XP event, even mid-raid.
//   - The multiplier (global + per-skill) has no equivalent live field on
//     GlobalConfiguration to write (SkillProgressRate/WeaponSkillProgressRate
//     only give a global weapon/non-weapon split, and are baked into each
//     skill's SkillAction.FactorValue once at construction, not read live),
//     so it's applied via a Harmony prefix on Skill.OnTrigger instead --
//     see SkillXpMultiplierPatch.
internal static class SkillXpSettings
{
    // ESkillId includes BotReload/BotSound -- present in a real profile's
    // Skills.Common (verified against the server's profiles.json template
    // earlier this session) but meaningless to the player, so they're left
    // out of the per-skill override list.
    public static readonly ESkillId[] AllSkills = Enum.GetValues(typeof(ESkillId))
        .Cast<ESkillId>()
        .Where(skill => skill != ESkillId.BotReload && skill != ESkillId.BotSound)
        .ToArray();

    // The skills the user actually plays around with day to day, in a
    // sensible display order (physical/mental, then weapon categories, then
    // practical). Confirmed against the user's own game knowledge, cross-
    // checked with the server's SkillTypes.cs comment ("Also called Weapon
    // Maintenance" on WeaponTreatment). Everything else -- hidden skills
    // that feed into one of these (e.g. Lockpicking -> Intellect), and
    // faction-exclusive/legacy-trading/vestigial entries -- goes in
    // AdvancedSkills instead, shown only behind the overlay's "advanced"
    // toggle.
    public static readonly ESkillId[] MainSkills =
    {
        ESkillId.Endurance, ESkillId.Strength, ESkillId.Vitality, ESkillId.Health, ESkillId.StressResistance,
        ESkillId.Metabolism, ESkillId.Immunity, ESkillId.Perception, ESkillId.Intellect, ESkillId.Attention,
        ESkillId.Charisma,
        ESkillId.Pistol, ESkillId.Revolver, ESkillId.SMG, ESkillId.Assault, ESkillId.Shotgun, ESkillId.Sniper,
        ESkillId.LMG, ESkillId.HMG, ESkillId.Launcher, ESkillId.AttachedLauncher, ESkillId.Throwing,
        ESkillId.Melee, ESkillId.DMR,
        ESkillId.AimDrills, ESkillId.TroubleShooting, ESkillId.Surgery, ESkillId.CovertMovement, ESkillId.Search,
        ESkillId.MagDrills, ESkillId.LightVests, ESkillId.HeavyVests, ESkillId.WeaponTreatment, ESkillId.Crafting,
        ESkillId.HideoutManagement,
    };

    public static readonly ESkillId[] AdvancedSkills = AllSkills.Except(MainSkills).ToArray();

    // MainSkills first, then AdvancedSkills -- used only to derive a display
    // Order below, so Configuration Manager's list matches this grouping
    // instead of falling back to its own default (alphabetical by key).
    private static readonly ESkillId[] DisplayOrder = MainSkills.Concat(AdvancedSkills).ToArray();

    // Configuration Manager reads tag objects off ConfigDescription.Tags via
    // reflection matched purely by type *name* ("ConfigurationManagerAttributes")
    // and field name -- confirmed by decompiling the installed
    // ConfigurationManager.dll (SettingEntryBase.SetFromAttributes). So this
    // local duck-typed class works without adding a real assembly reference,
    // same principle as the client's existing no-BepInDependency approach
    // for spt-common. IsAdvanced hooks into Configuration Manager's own
    // built-in "Show advanced" toggle -- no custom overlay checkbox needed.
    private sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
        public int? Order;
    }

    public static ConfigEntry<float> GlobalMultiplier { get; private set; } = null!;
    public static Dictionary<ESkillId, ConfigEntry<float>> PerSkillOverride { get; private set; } = null!;

    // Defaults below mirror EFT.GlobalConfiguration's own vanilla defaults
    // (confirmed via decompiling), so leaving these untouched matches
    // vanilla behavior, not whatever SVM last wrote to globals.json.
    public static ConfigEntry<int> FreshPoints { get; private set; } = null!;
    public static ConfigEntry<int> PointsBeforeFatigue { get; private set; } = null!;
    public static ConfigEntry<float> FreshEffectiveness { get; private set; } = null!;
    public static ConfigEntry<float> MinEffectiveness { get; private set; } = null!;
    public static ConfigEntry<float> FatiguePerPoint { get; private set; } = null!;
    public static ConfigEntry<int> FatigueReset { get; private set; } = null!;

    public static void Initialize(ConfigFile config)
    {
        GlobalMultiplier = config.Bind(
            "Global",
            "Multiplier",
            1f,
            "XP multiplier applied to any skill that doesn't have its own override below (an override of " +
            "0 means \"use this instead\")."
        );

        FreshPoints = config.Bind(
            "Fatigue",
            "FreshPoints",
            1,
            "SkillFreshPoints -- points earned this session (on a given skill) before the fresh-XP bonus ends."
        );
        PointsBeforeFatigue = config.Bind(
            "Fatigue",
            "PointsBeforeFatigue",
            2,
            "SkillPointsBeforeFatigue -- additional points earned after FreshPoints, at normal (1x) " +
            "effectiveness, before fatigue starts reducing it."
        );
        FreshEffectiveness = config.Bind(
            "Fatigue",
            "FreshEffectiveness",
            1.2f,
            "SkillFreshEffectiveness -- XP effectiveness multiplier while within FreshPoints."
        );
        MinEffectiveness = config.Bind(
            "Fatigue",
            "MinEffectiveness",
            0.01f,
            "SkillMinEffectiveness -- floor on how low fatigue can reduce effectiveness."
        );
        FatiguePerPoint = config.Bind(
            "Fatigue",
            "FatiguePerPoint",
            0.5f,
            "SkillFatiguePerPoint -- effectiveness is raised to this power per point earned once fatigued " +
            "(lower = falls off faster)."
        );
        FatigueReset = config.Bind(
            "Fatigue",
            "FatigueResetSeconds",
            300,
            "SkillFatigueReset -- seconds with no gain on a skill before its fatigue clears."
        );

        PerSkillOverride = new Dictionary<ESkillId, ConfigEntry<float>>();
        var advancedLookup = new HashSet<ESkillId>(AdvancedSkills);
        for (var i = 0; i < DisplayOrder.Length; i++)
        {
            var skill = DisplayOrder[i];
            var isAdvanced = advancedLookup.Contains(skill);
            var tags = new ConfigurationManagerAttributes
            {
                // Higher Order sorts first (ConfigurationManager orders
                // "descending" within a category), so counting down from the
                // array length preserves this array's grouping.
                Order = DisplayOrder.Length - i,
                IsAdvanced = isAdvanced ? true : null,
            };
            var description = isAdvanced
                ? $"XP multiplier for {skill} specifically. 0 = use the global multiplier instead. " +
                  "Hidden/vestigial or faction-exclusive -- feeds another skill or rarely applies."
                : $"XP multiplier for {skill} specifically. 0 = use the global multiplier instead.";

            // Section name kept as "PerSkillOverride" (not renamed to
            // something friendlier) so this binds to the user's existing
            // saved values in com.imperator.skillpointsmod.client.cfg --
            // real non-default overrides already there (Health, Metabolism,
            // Charisma, etc.) -- rather than orphaning them under a
            // section BepInEx would silently rebuild with defaults.
            PerSkillOverride[skill] = config.Bind(
                "PerSkillOverride",
                skill.ToString(),
                0f,
                new ConfigDescription(description, null, tags)
            );
        }
    }

    public static float GetMultiplier(ESkillId skill)
    {
        var overrideValue = PerSkillOverride.TryGetValue(skill, out var entry) ? entry.Value : 0f;
        return overrideValue > 0f ? overrideValue : GlobalMultiplier.Value;
    }

    // Called every frame from the plugin's Update() -- cheap (six field
    // writes), and guarantees our values are in effect regardless of when
    // GlobalConfiguration.Instance gets (re)constructed by the game, whose
    // exact lifecycle (per-raid? per-launch?) wasn't pinned down, so
    // re-applying continuously sidesteps needing to know.
    public static void ApplyFatigueSettings()
    {
        if (!Singleton<GlobalConfiguration>.Instantiated)
        {
            return;
        }

        var gc = Singleton<GlobalConfiguration>.Instance;
        gc.SkillFreshPoints = FreshPoints.Value;
        gc.SkillPointsBeforeFatigue = PointsBeforeFatigue.Value;
        gc.SkillFreshEffectiveness = FreshEffectiveness.Value;
        gc.SkillMinEffectiveness = MinEffectiveness.Value;
        gc.SkillFatiguePerPoint = FatiguePerPoint.Value;
        gc.SkillFatigueReset = FatigueReset.Value;
    }
}
