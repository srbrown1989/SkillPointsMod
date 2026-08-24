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

        var response = new SkillPointsStatusResponse
        {
            UnspentPoints = skillPointsService.GetUnspentPoints(sessionId)
        };

        return new ValueTask<string>(httpResponseUtil.NoBody(response));
    }

    public ValueTask<string> HandleSpend(SpendPointRequest info, MongoId sessionId)
    {
        var success = skillPointsService.SpendPointOnSkill(sessionId, info.Skill);
        return new ValueTask<string>(
            httpResponseUtil.NoBody(new SkillPointsActionResponse { Success = success })
        );
    }

    public ValueTask<string> HandleReset(MongoId sessionId)
    {
        var success = skillPointsService.ResetAllSkills(sessionId);
        return new ValueTask<string>(
            httpResponseUtil.NoBody(new SkillPointsActionResponse { Success = success })
        );
    }
}

public record SpendPointRequest : IRequestData
{
    public SkillTypes Skill { get; init; }
}

public record SkillPointsStatusResponse
{
    public int UnspentPoints { get; init; }
}

public record SkillPointsActionResponse
{
    public bool Success { get; init; }
}
