using System.Reflection;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;
using SPTarkov.DI.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace SkillPointsMod;

// Loaded from config/config.json (shipped next to the built assembly, see
// the .csproj). Missing/unparsable file falls back to these defaults rather
// than failing the whole mod load.
public record SkillPointsFileConfig
{
    [JsonPropertyName("skillPointsPerLevel")]
    public double SkillPointsPerLevel { get; init; } = 1;

    [JsonPropertyName("startingSkillPoints")]
    public int StartingSkillPoints { get; init; } = 5;

    [JsonPropertyName("resetCostAmount")]
    public double ResetCostAmount { get; init; } = 5_000_000;

    [JsonPropertyName("resetCostCurrency")]
    public string ResetCostCurrency { get; init; } = "rubles";

    // Skips the currency deduction in ResetAllSkills entirely when true, for
    // testing the reset flow without needing to grind/spawn currency. Flip
    // back to false before actually relying on the reset cost.
    [JsonPropertyName("debugFreeReset")]
    public bool DebugFreeReset { get; init; } = false;
}

// SpendPointOnSkill/ResetAllSkills return this instead of a bare bool so the
// client can show a specific reason (e.g. "insufficient rubles") instead of
// a generic failure message. AffectedSkills carries the new Progress for
// exactly the skill(s) this action changed (keyed by SkillTypes member
// name), so the client can nudge only those skills' live display -- never
// every visible skill, which risks stomping unrelated real-time in-raid
// progress the server doesn't know about yet (see SyncSkillProgress).
public readonly record struct SkillActionResult(bool Success, string? Reason, Dictionary<string, double>? AffectedSkills = null)
{
    public static SkillActionResult Ok(Dictionary<string, double>? affectedSkills = null) => new(true, null, affectedSkills);
    public static SkillActionResult Fail(string reason) => new(false, reason);
}

// Tracks, per profile, how many points we've awarded vs the player's
// actual level/prestige, and how many points are currently unspent.
// Persisted as one JSON file per profile under this mod's own "data"
// folder -- kept separate from the vanilla profile on purpose, so this
// mod can never corrupt BSG/SPT-owned profile data.
public record SkillPointsData
{
    public int LastAwardedAtLevel { get; set; } = 1;
    public int LastKnownPrestigeLevel { get; set; } = 0;

    // Info.RegistrationDate is set fresh by CreateProfileService whenever a
    // profile is created -- including a wipe, which recreates the character.
    // 0 means "never recorded yet" (old save predating this field, or a
    // brand-new profile going through GetData's own new-profile path) and is
    // deliberately never treated as a wipe on its own; only an actual change
    // from a previously-recorded non-zero value counts. Not the same signal
    // as ProfileInfo.IsWiped, which is only true transiently mid-wipe and
    // already false again by the time any of our routes could observe it.
    public int LastKnownRegistrationDate { get; set; } = 0;

    // Fractional because skillPointsPerLevel supports decimals (e.g. 1.5);
    // spending always costs exactly 1 whole point regardless.
    public double UnspentPoints { get; set; } = 0;

    // How many points we've put into each skill, so a reset knows
    // exactly how much Progress to remove and how many points to refund.
    public Dictionary<SkillTypes, int> SpentPerSkill { get; set; } = new();
}

[Injectable]
public class SkillPointsService(
    ISptLogger<SkillPointsService> logger,
    ProfileHelper profileHelper,
    ModHelper modHelper,
    FileUtil fileUtil,
    JsonUtil jsonUtil,
    PaymentService paymentService,
    EventOutputHolder eventOutputHolder
)
{
    private const int ProgressPerPoint = 100; // 100 Progress == 1 in-game skill level

    private readonly string _modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
    private readonly SkillPointsFileConfig _config = LoadConfig(modHelper, fileUtil, jsonUtil, logger);

    private readonly Dictionary<MongoId, SkillPointsData> _cache = new();

    private static SkillPointsFileConfig LoadConfig(
        ModHelper modHelper,
        FileUtil fileUtil,
        JsonUtil jsonUtil,
        ISptLogger<SkillPointsService> logger
    )
    {
        var configPath = IOPath.Combine(
            modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()),
            "config",
            "config.json"
        );

        if (!fileUtil.FileExists(configPath))
        {
            logger.Warning($"SkillPointsMod: no config.json found at {configPath}, using defaults.");
            return new SkillPointsFileConfig();
        }

        var config = jsonUtil.Deserialize<SkillPointsFileConfig>(fileUtil.ReadFile(configPath));
        if (config is null)
        {
            logger.Warning("SkillPointsMod: config.json failed to parse, using defaults.");
            return new SkillPointsFileConfig();
        }

        return config;
    }

    private string GetDataFilePath(MongoId sessionId) => IOPath.Combine(_modFolder, "data", $"{sessionId}.json");

    // Loads a profile's data from disk on first access per process run and
    // caches it; every mutating method below saves back to disk immediately
    // so nothing's lost on an unclean shutdown. A profile with no data file
    // yet is treated as brand new and granted its starting points once.
    private SkillPointsData GetData(MongoId sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var cached))
        {
            return cached;
        }

        var filePath = GetDataFilePath(sessionId);
        SkillPointsData data;

        if (fileUtil.FileExists(filePath))
        {
            data = jsonUtil.Deserialize<SkillPointsData>(fileUtil.ReadFile(filePath)) ?? new SkillPointsData();

            // A save file from before wipe-detection existed has
            // LastKnownRegistrationDate defaulting to 0, which
            // CheckForLevelUp treats as "no baseline yet, don't reset" (to
            // avoid false-positiving a wipe for a brand-new profile whose
            // baseline just hasn't been set within this same call yet).
            // Seed it here, on load, rather than waiting for
            // CheckForLevelUp to notice a mismatch -- otherwise an old save
            // whose owner wipes before ever loading under this version has
            // no recorded pre-wipe baseline to compare against, and the
            // wipe silently goes undetected (confirmed: this happened once
            // already, leaving stale SpentPerSkill data that a subsequent
            // Reset paid out against an already-wiped profile).
            if (data.LastKnownRegistrationDate == 0)
            {
                data.LastKnownRegistrationDate = profileHelper.GetPmcProfile(sessionId)?.Info?.RegistrationDate ?? 0;
                SaveData(sessionId, data);
            }
        }
        else
        {
            data = new SkillPointsData
            {
                UnspentPoints = _config.StartingSkillPoints,
                LastKnownRegistrationDate = profileHelper.GetPmcProfile(sessionId)?.Info?.RegistrationDate ?? 0,
            };
            SaveData(sessionId, data);
        }

        _cache[sessionId] = data;
        return data;
    }

    private void SaveData(MongoId sessionId, SkillPointsData data)
    {
        fileUtil.WriteFile(GetDataFilePath(sessionId), jsonUtil.Serialize(data, indented: true) ?? "{}");
    }

    // Call this periodically (e.g. from an IOnUpdate hook) or whenever the
    // profile is touched. Awards points for any levels gained since we last
    // checked -- avoids needing a single dedicated "on level up" event,
    // which SPT doesn't cleanly expose. Also detects prestige (player's
    // level resets on prestige, so we track prestige separately via
    // Info.PrestigeLevel) and re-grants starting points on each one, and
    // detects a full profile wipe (via Info.RegistrationDate changing) so a
    // wipe doesn't leave this mod's own save data stuck referencing a
    // character that no longer exists.
    public void CheckForLevelUp(MongoId sessionId)
    {
        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Info?.Level is null)
        {
            return;
        }

        var data = GetData(sessionId);
        var currentRegistrationDate = pmc.Info.RegistrationDate ?? 0;
        var changed = false;

        if (currentRegistrationDate != data.LastKnownRegistrationDate)
        {
            if (data.LastKnownRegistrationDate != 0)
            {
                data.LastAwardedAtLevel = 1;
                data.LastKnownPrestigeLevel = 0;
                data.UnspentPoints = _config.StartingSkillPoints;
                data.SpentPerSkill.Clear();

                logger.Info(
                    $"SkillPointsMod: detected a profile wipe for session {sessionId}, reset mod data and " +
                    $"granted {_config.StartingSkillPoints} starting point(s)."
                );
            }

            data.LastKnownRegistrationDate = currentRegistrationDate;
            changed = true;
        }

        var currentLevel = pmc.Info.Level.Value;
        var currentPrestige = pmc.Info.PrestigeLevel ?? 0;

        if (currentPrestige > data.LastKnownPrestigeLevel)
        {
            data.LastKnownPrestigeLevel = currentPrestige;
            data.LastAwardedAtLevel = currentLevel;
            data.UnspentPoints += _config.StartingSkillPoints;
            changed = true;

            logger.Info(
                $"SkillPointsMod: prestige {currentPrestige} reached, granted {_config.StartingSkillPoints} " +
                $"starting point(s). Unspent balance: {data.UnspentPoints}"
            );
        }

        if (currentLevel > data.LastAwardedAtLevel)
        {
            var levelsGained = currentLevel - data.LastAwardedAtLevel;
            var pointsAwarded = levelsGained * _config.SkillPointsPerLevel;

            data.UnspentPoints += pointsAwarded;
            data.LastAwardedAtLevel = currentLevel;
            changed = true;

            logger.Info(
                $"SkillPointsMod: awarded {pointsAwarded} point(s) for reaching level {currentLevel}. " +
                $"Unspent balance: {data.UnspentPoints}"
            );
        }

        if (changed)
        {
            SaveData(sessionId, data);
        }
    }

    public double GetUnspentPoints(MongoId sessionId)
    {
        return GetData(sessionId).UnspentPoints;
    }

    // So the client can show a real "this costs N rubles" confirmation
    // before resetting, instead of guessing/hardcoding the configured cost.
    public (double Amount, string Currency, bool DebugFree) GetResetCostInfo()
    {
        return (_config.ResetCostAmount, _config.ResetCostCurrency, _config.DebugFreeReset);
    }

    // Lets the client grey out a skill's spend button once it's capped, per
    // the spec, without hardcoding CommonSkill.MaxSkillProgress client-side.
    public Dictionary<string, double> GetSkillProgress(MongoId sessionId)
    {
        var pmc = profileHelper.GetPmcProfile(sessionId);
        return pmc?.Skills?.Common?.ToDictionary(s => s.Id.ToString(), s => s.Progress)
            ?? new Dictionary<string, double>();
    }

    // Fails if the player doesn't have a full point to spend, or the skill
    // is already at max (5100 Progress / level 51).
    public SkillActionResult SpendPointOnSkill(MongoId sessionId, SkillTypes skill)
    {
        var data = GetData(sessionId);
        if (data.UnspentPoints < 1)
        {
            return SkillActionResult.Fail("No unspent skill points available.");
        }

        var pmc = profileHelper.GetPmcProfile(sessionId);
        var skillEntry = pmc?.Skills?.Common?.FirstOrDefault(s => s.Id == skill);
        if (skillEntry is null)
        {
            logger.Warning($"SkillPointsMod: could not find skill {skill} on profile.");
            return SkillActionResult.Fail($"Could not find skill {skill} on profile.");
        }

        if (skillEntry.Progress >= CommonSkill.MaxSkillProgress)
        {
            return SkillActionResult.Fail($"{skill} is already at the maximum level.");
        }

        skillEntry.Progress = Math.Min(skillEntry.Progress + ProgressPerPoint, CommonSkill.MaxSkillProgress);

        data.UnspentPoints -= 1;
        data.SpentPerSkill[skill] = data.SpentPerSkill.GetValueOrDefault(skill) + 1;
        SaveData(sessionId, data);

        logger.Info($"SkillPointsMod: spent 1 point on {skill}. Remaining: {data.UnspentPoints}");
        return SkillActionResult.Ok(new Dictionary<string, double> { [skill.ToString()] = skillEntry.Progress });
    }

    // Deducts the configured reset cost from the player's inventory (unless
    // debugFreeReset is set) before refunding points and reverting skill
    // Progress. Fails (no changes made) if there's nothing to refund or the
    // player can't afford the cost.
    public SkillActionResult ResetAllSkills(MongoId sessionId)
    {
        var data = GetData(sessionId);
        if (data.SpentPerSkill.Count == 0)
        {
            return SkillActionResult.Fail("No spent points to refund.");
        }

        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Skills?.Common is null)
        {
            return SkillActionResult.Fail("Could not read skills from profile.");
        }

        if (!_config.DebugFreeReset)
        {
            var currencyTpl = _config.ResetCostCurrency.ToLowerInvariant() switch
            {
                "dollars" => Money.DOLLARS,
                "euros" => Money.EUROS,
                _ => Money.ROUBLES,
            };

            var output = eventOutputHolder.GetOutput(sessionId);
            // requestedStackIds: null -- no client-chosen stack preference, spend in default order.
            // (The old 5-arg overload is obsolete in 4.1.3; it forwarded to this one with null anyway.)
            paymentService.AddPaymentToOutput(pmc, currencyTpl, _config.ResetCostAmount, sessionId, output, null);

            if (output.Warnings is { Count: > 0 })
            {
                logger.Warning(
                    $"SkillPointsMod: reset failed for session {sessionId}, insufficient " +
                    $"{_config.ResetCostCurrency} ({_config.ResetCostAmount} required)."
                );
                return SkillActionResult.Fail(
                    $"Not enough {_config.ResetCostCurrency} ({_config.ResetCostAmount:N0} required)."
                );
            }
        }

        var affectedSkills = new Dictionary<string, double>();

        foreach (var (skillType, pointsSpent) in data.SpentPerSkill)
        {
            var skillEntry = pmc.Skills.Common.FirstOrDefault(s => s.Id == skillType);
            if (skillEntry is not null)
            {
                var progressToRemove = pointsSpent * ProgressPerPoint;
                skillEntry.Progress = Math.Max(0, skillEntry.Progress - progressToRemove);
                affectedSkills[skillType.ToString()] = skillEntry.Progress;
            }

            data.UnspentPoints += pointsSpent;
        }

        data.SpentPerSkill.Clear();
        SaveData(sessionId, data);

        logger.Info($"SkillPointsMod: reset all skills, refunded points. New balance: {data.UnspentPoints}");
        return SkillActionResult.Ok(affectedSkills);
    }
}
