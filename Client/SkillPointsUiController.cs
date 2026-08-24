using System;
using System.Collections.Generic;
using EFT;
using SPT.Common.Http;
using SPT.Common.Utils;

namespace SkillPointsMod.Client;

// Shared state/HTTP logic used by both the F9 floating overlay and the
// embedded skills-screen patches, so there's one source of truth for the
// balance and one place that talks to the server. Both UIs poll this
// class's state from their own per-frame Update()/OnGUI() calls rather than
// an event -- the embedded UI's GameObjects come and go with the screen
// (including via AsyncViewList pooling/reuse of row instances), so polling
// avoids leaking subscriptions tied to destroyed/reused Unity objects.
internal static class SkillPointsUiController
{
    public static double UnspentPoints { get; private set; }
    public static string? LastError { get; private set; }

    // Populated from status, used to build a real reset-confirmation prompt.
    public static double ResetCostAmount { get; private set; }
    public static string ResetCostCurrency { get; private set; } = "";
    public static bool ResetIsFree { get; private set; }

    // Raw per-skill progress from the last status fetch, keyed by SkillTypes
    // member name -- used by the standalone overlay for cap-checking (it may
    // be opened before the embedded skills screen ever has, so it can't rely
    // on the live-skill registry _liveSkills/IsSkillCapped below).
    public static IReadOnlyDictionary<string, double> SkillProgress { get; private set; } = new Dictionary<string, double>();

    // The client's own live Skill objects for currently-visible rows, so a
    // successful spend/reset can immediately nudge the vanilla row's own
    // progress bar/level text via Skill.SetCurrent (which raises
    // SkillExperienceChanged, already bound by SkillPanel.OnSkillLevelChanged)
    // instead of waiting for the game's normal server-sync packet, which our
    // custom routes don't go through.
    private static readonly Dictionary<ESkillId, Skill> _liveSkills = new();

    public static void RegisterLiveSkill(Skill skill)
    {
        _liveSkills[skill.Id] = skill;
    }

    public static bool IsSkillCapped(ESkillId id)
    {
        return _liveSkills.TryGetValue(id, out var skill) && skill.IsEliteLevel;
    }

    // Deliberately does NOT touch any live Skill object -- only updates this
    // class's own UI state (balance, cap-check dict, reset cost). Used for
    // plain balance checks (opening the overlay, opening the skills screen)
    // which can happen at any time, including mid-raid. A blanket sync of
    // every registered skill used to run here on every call, which snapped
    // each skill's *live in-raid* Progress back down to its last-saved
    // server value the moment you so much as opened the overlay to check
    // your balance -- silently erasing the on-screen display of real,
    // not-yet-server-synced in-raid skill gains (confirmed: a user reported
    // getting search/loot XP notifications all raid but seeing zero skill
    // progress on checking, with no spend/reset involved at all -- this was
    // the only mechanism in this codebase that could cause that). Now the
    // only thing allowed to call Skill.SetCurrent is SyncAffectedSkills,
    // and only for the skill(s) a spend/reset we ourselves just made
    // actually changed.
    public static void RefreshStatus()
    {
        try
        {
            var response = Json.Deserialize<StatusResponse>(RequestHandler.PostJson("/skillpointsmod/status", "{}"));
            UnspentPoints = response.UnspentPoints;
            SkillProgress = response.SkillProgress ?? new Dictionary<string, double>();
            ResetCostAmount = response.ResetCostAmount;
            ResetCostCurrency = response.ResetCostCurrency ?? "";
            ResetIsFree = response.ResetIsFree;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"Status request failed: {ex.Message}";
        }
    }

    public static void TrySpend(ESkillId skill)
    {
        string? failureReason = null;
        Dictionary<string, double>? affectedSkills = null;
        try
        {
            var body = Json.Serialize(new SpendRequest { Skill = skill.ToString() });
            var response = Json.Deserialize<ActionResponse>(RequestHandler.PostJson("/skillpointsmod/spend", body));
            if (response.Success)
            {
                affectedSkills = response.AffectedSkills;
            }
            else
            {
                failureReason = response.Reason ?? $"Could not spend a point on {skill}.";
            }
        }
        catch (Exception ex)
        {
            failureReason = $"Spend request failed: {ex.Message}";
        }

        // RefreshStatus() clears LastError on its own success path, so any
        // failure reason from this action has to be applied AFTER it, or it
        // gets immediately overwritten and the player never sees it.
        RefreshStatus();
        SyncAffectedSkills(affectedSkills);
        if (failureReason is not null)
        {
            LastError = failureReason;
        }
    }

    public static void TryReset()
    {
        string? failureReason = null;
        Dictionary<string, double>? affectedSkills = null;
        try
        {
            var response = Json.Deserialize<ActionResponse>(RequestHandler.PostJson("/skillpointsmod/reset", "{}"));
            if (response.Success)
            {
                affectedSkills = response.AffectedSkills;
            }
            else
            {
                failureReason = response.Reason ?? "Nothing to reset.";
            }
        }
        catch (Exception ex)
        {
            failureReason = $"Reset request failed: {ex.Message}";
        }

        RefreshStatus();
        SyncAffectedSkills(affectedSkills);
        if (failureReason is not null)
        {
            LastError = failureReason;
        }
    }

    // Only ever called with the specific skill(s) a spend/reset WE just
    // performed actually changed (server-reported, not a blanket dump of
    // every skill) -- see the long comment on RefreshStatus for why that
    // distinction matters.
    private static void SyncAffectedSkills(Dictionary<string, double>? affectedSkills)
    {
        if (affectedSkills is null)
        {
            return;
        }

        foreach (var (key, value) in affectedSkills)
        {
            if (Enum.TryParse<ESkillId>(key, out var id) && _liveSkills.TryGetValue(id, out var liveSkill))
            {
                // silent: true -- without it, BaseSkill.SetCurrent unconditionally
                // calls LevelChanged() (which fires Skill.LevelChanged ->
                // SkillManager.AnySkillUp.Complete(this), the game's own
                // level-up notification trigger) even when the value didn't
                // actually change. silent:true makes it a no-op when nothing
                // changed, and still correctly notifies when it did.
                liveSkill.SetCurrent((float)value, silent: true);
            }
        }
    }

    private class StatusResponse
    {
        public double UnspentPoints { get; set; }
        public Dictionary<string, double>? SkillProgress { get; set; }
        public double ResetCostAmount { get; set; }
        public string? ResetCostCurrency { get; set; }
        public bool ResetIsFree { get; set; }
    }

    private class SpendRequest
    {
        public string Skill { get; set; } = "";
    }

    private class ActionResponse
    {
        public bool Success { get; set; }
        public string? Reason { get; set; }
        public Dictionary<string, double>? AffectedSkills { get; set; }
    }
}
