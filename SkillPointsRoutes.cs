using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SkillPointsMod;

[Injectable(TypePriority = OnLoadOrder.Routers)]
public class SkillPointsStaticRouter(JsonUtil jsonUtil, SkillPointsRouteCallback callback)
    : StaticRouter(jsonUtil, [
        new RouteAction<EmptyRequestData>(
            "/skillpointsmod/status",
            async (url, info, sessionId, output, cancellationToken) =>
                await callback.HandleStatus(sessionId)
        ),
        new RouteAction<SpendPointRequest>(
            "/skillpointsmod/spend",
            async (url, info, sessionId, output, cancellationToken) =>
                await callback.HandleSpend(info, sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/skillpointsmod/reset",
            async (url, info, sessionId, output, cancellationToken) =>
                await callback.HandleReset(sessionId)
        )
    ])
{ }

[Injectable]
public class SkillPointsRouteCallback(
    HttpResponseUtil httpResponseUtil,
    JsonUtil jsonUtil,
    SkillPointsService skillPointsService
)
{
    public ValueTask<string> HandleStatus(MongoId sessionId)
    {
        // Catch up on any levels gained since we last checked before
        // reporting the balance back.
        skillPointsService.CheckForLevelUp(sessionId);

        var resetCost = skillPointsService.GetResetCostInfo();
        var response = new SkillPointsStatusResponse
        {
            UnspentPoints = skillPointsService.GetUnspentPoints(sessionId),
            SkillProgress = skillPointsService.GetSkillProgress(sessionId),
            ResetCostAmount = resetCost.Amount,
            ResetCostCurrency = resetCost.Currency,
            ResetIsFree = resetCost.DebugFree
        };

        return new ValueTask<string>(httpResponseUtil.NoBody(response));
    }

    public ValueTask<string> HandleSpend(SpendPointRequest info, MongoId sessionId)
    {
        var result = skillPointsService.SpendPointOnSkill(sessionId, info.Skill);
        return new ValueTask<string>(
            httpResponseUtil.NoBody(new SkillPointsActionResponse
            {
                Success = result.Success,
                Reason = result.Reason,
                AffectedSkills = result.AffectedSkills
            })
        );
    }

    public ValueTask<string> HandleReset(MongoId sessionId)
    {
        var result = skillPointsService.ResetAllSkills(sessionId);
        return new ValueTask<string>(
            httpResponseUtil.NoBody(new SkillPointsActionResponse
            {
                Success = result.Success,
                Reason = result.Reason,
                AffectedSkills = result.AffectedSkills
            })
        );
    }
}

public record SpendPointRequest : IRequestData
{
    public SkillTypes Skill { get; init; }
}

public record SkillPointsStatusResponse
{
    public double UnspentPoints { get; init; }

    // Keyed by SkillTypes member name (e.g. "Endurance") rather than the
    // enum itself, to sidestep any ambiguity around whether System.Text.Json
    // honours the enum's JsonStringEnumConverter for dictionary keys.
    public Dictionary<string, double> SkillProgress { get; init; } = new();

    // So the client can show a real reset-confirmation prompt with the
    // actual configured cost instead of guessing/hardcoding it.
    public double ResetCostAmount { get; init; }
    public string ResetCostCurrency { get; init; } = "";
    public bool ResetIsFree { get; init; }
}

public record SkillPointsActionResponse
{
    public bool Success { get; init; }
    public string? Reason { get; init; }

    // New Progress for exactly the skill(s) this action changed, so the
    // client only ever nudges the live display for skills it caused itself
    // -- never a blanket sync of everything, which risks overwriting
    // unrelated real-time in-raid progress the server doesn't know about.
    public Dictionary<string, double>? AffectedSkills { get; init; }
}
