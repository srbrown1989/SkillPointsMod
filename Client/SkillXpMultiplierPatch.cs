using EFT;
using HarmonyLib;

namespace SkillPointsMod.Client;

// Multiplies raw skill XP by our own global/per-skill setting before the
// game's own OnTrigger logic runs (fatigue scaling via UseEffectiveness,
// cross-skill dependency broadcasts like Lockpicking -> Intellect via
// SkillManager.SkillProgress.Complete, the early-level bonus curve via
// CalculateExpOnFirstLevels). Deliberately a `ref val` prefix that lets the
// original method continue running -- not a full replacement -- so all of
// that keeps working exactly as vanilla, just fed a scaled value first.
[HarmonyPatch(typeof(Skill), nameof(Skill.OnTrigger))]
internal static class SkillXpMultiplierPatch
{
    [HarmonyPrefix]
    private static void Prefix(Skill __instance, ref float val)
    {
        val *= SkillXpSettings.GetMultiplier(__instance.Id);
    }
}
