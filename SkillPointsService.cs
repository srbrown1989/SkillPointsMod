using SPTarkov.DI.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SkillPointsMod;

// TODO: load these from a config.json instead of hardcoding, once the
// basic flow is confirmed working end to end.
public static class SkillPointsConfig
{
    public const int PointsPerLevel = 1;
    public const int ProgressPerPoint = 100; // 100 Progress == 1 in-game skill level
    public const int ResetCostRubles = 50000;
}

// Tracks, per profile, how many points we've awarded vs the player's
// actual level, and how many points are currently unspent.
// Kept separate from the vanilla profile on purpose -- we never want
// this mod to be able to corrupt BSG/SPT-owned profile data.
public record SkillPointsData
{
    public int LastAwardedAtLevel { get; set; } = 1;
    public int UnspentPoints { get; set; } = 0;

    // How many points we've put into each skill, so a reset knows
    // exactly how much Progress to remove and how many points to refund.
    public Dictionary<SkillTypes, int> SpentPerSkill { get; set; } = new();
}

[Injectable]
public class SkillPointsService(
    ISptLogger<SkillPointsService> logger,
    ProfileHelper profileHelper
)
{
    // TODO: replace with real per-profile persistence (a small JSON file
    // under this mod's folder, keyed by profile id) -- an in-memory
    // dictionary won't survive a server restart. Flagging this rather
    // than pretending it's done.
    private readonly Dictionary<MongoId, SkillPointsData> _data = new();

    private SkillPointsData GetData(MongoId sessionId)
    {
        if (!_data.TryGetValue(sessionId, out var data))
        {
            data = new SkillPointsData();
            _data[sessionId] = data;
        }
        return data;
    }

    // Call this periodically (e.g. from an IOnUpdate hook) or whenever
    // the profile is touched. Awards points for any levels gained since
    // we last checked -- avoids needing a single dedicated "on level up"
    // event, which SPT doesn't cleanly expose.
    public void CheckForLevelUp(MongoId sessionId)
    {
        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Info?.Level is null)
        {
            return;
        }

        var data = GetData(sessionId);
        var currentLevel = pmc.Info.Level.Value;

        if (currentLevel > data.LastAwardedAtLevel)
        {
            var levelsGained = currentLevel - data.LastAwardedAtLevel;
            var pointsAwarded = levelsGained * SkillPointsConfig.PointsPerLevel;

            data.UnspentPoints += pointsAwarded;
            data.LastAwardedAtLevel = currentLevel;

            logger.Info(
                $"SkillPointsMod: awarded {pointsAwarded} point(s) for reaching level {currentLevel}. " +
                $"Unspent balance: {data.UnspentPoints}"
            );
        }
    }

    public int GetUnspentPoints(MongoId sessionId)
    {
        return GetData(sessionId).UnspentPoints;
    }

    // Returns false if the player doesn't have a point to spend, or the
    // skill is already at max (5100 Progress / level 51).
    public bool SpendPointOnSkill(MongoId sessionId, SkillTypes skill)
    {
        var data = GetData(sessionId);
        if (data.UnspentPoints <= 0)
        {
            return false;
        }

        var pmc = profileHelper.GetPmcProfile(sessionId);
        var skillEntry = pmc?.Skills?.Common?.FirstOrDefault(s => s.Id == skill);
        if (skillEntry is null)
        {
            logger.Warning($"SkillPointsMod: could not find skill {skill} on profile.");
            return false;
        }

        if (skillEntry.Progress >= CommonSkill.MaxSkillProgress)
        {
            return false;
        }

        skillEntry.Progress = Math.Min(
            skillEntry.Progress + SkillPointsConfig.ProgressPerPoint,
            CommonSkill.MaxSkillProgress
        );

        data.UnspentPoints -= 1;
        data.SpentPerSkill[skill] = data.SpentPerSkill.GetValueOrDefault(skill) + 1;

        logger.Info($"SkillPointsMod: spent 1 point on {skill}. Remaining: {data.UnspentPoints}");
        return true;
    }

    // TODO: actually deduct SkillPointsConfig.ResetCostRubles from the
    // player's inventory before applying this -- needs the item/money
    // helper, which we haven't looked at yet. For now this only does the
    // skill + point bookkeeping.
    public bool ResetAllSkills(MongoId sessionId)
    {
        var data = GetData(sessionId);
        if (data.SpentPerSkill.Count == 0)
        {
            return false;
        }

        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Skills?.Common is null)
        {
            return false;
        }

        foreach (var (skillType, pointsSpent) in data.SpentPerSkill)
        {
            var skillEntry = pmc.Skills.Common.FirstOrDefault(s => s.Id == skillType);
            if (skillEntry is not null)
            {
                var progressToRemove = pointsSpent * SkillPointsConfig.ProgressPerPoint;
                skillEntry.Progress = Math.Max(0, skillEntry.Progress - progressToRemove);
            }

            data.UnspentPoints += pointsSpent;
        }

        data.SpentPerSkill.Clear();
        logger.Info($"SkillPointsMod: reset all skills, refunded points. New balance: {data.UnspentPoints}");
        return true;
    }
}
