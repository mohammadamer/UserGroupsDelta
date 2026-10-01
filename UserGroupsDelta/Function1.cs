using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using UserGroupsDelta.GroupsDelta;

namespace UserGroupsDelta;

public sealed class GroupsDeltaFunctions(
    IGroupsDeltaSyncService syncService)
{
    [Function("SyncGroupsDeltaHttp")]
    public async Task<IActionResult> SynchronizeHttpAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "groups/delta")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;

        try
        {
            var result = await syncService.SynchronizeAsync(cancellationToken);
            return new OkObjectResult(result);
        }
        catch (GroupsDeltaSyncInProgressException exception)
        {
            return new ConflictObjectResult(new { error = exception.Message });
        }
    }

    [Function("SyncGroupMemberAdditions")]
    public async Task<IActionResult> SynchronizeMemberAdditionsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "groups/delta/member-additions")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;

        try
        {
            var result = await syncService.SynchronizeMemberAdditionsAsync(cancellationToken);
            return new OkObjectResult(result);
        }
        catch (GroupsDeltaSyncInProgressException exception)
        {
            return new ConflictObjectResult(new { error = exception.Message });
        }
    }

    //[Function("SyncGroupsDeltaTimer")]
    //public async Task SynchronizeTimerAsync(
    //    [TimerTrigger("%GroupsDeltaSchedule%")] TimerInfo timer,
    //    CancellationToken cancellationToken)
    //{
    //    _ = timer;

    //    try
    //    {
    //        var result = await syncService.SynchronizeAsync(cancellationToken);
    //        logger.LogInformation(
    //            "Groups delta {SyncType} synchronization completed: {GroupCount} groups across {PageCount} pages.",
    //            result.SyncType,
    //            result.GroupCount,
    //            result.PageCount);
    //    }
    //    catch (GroupsDeltaSyncInProgressException exception)
    //    {
    //        logger.LogWarning(
    //            exception,
    //            "Skipped the scheduled groups delta synchronization because another run holds the lease.");
    //    }
    //}

    [Function("ResetGroupsDelta")]
    public async Task<IActionResult> ResetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "groups/delta/reset")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;

        try
        {
            var deleted = await syncService.ResetAsync(cancellationToken);
            return new OkObjectResult(new
            {
                reset = true,
                checkpointExisted = deleted,
                message = "The next synchronization will perform an initial groups query."
            });
        }
        catch (GroupsDeltaSyncInProgressException exception)
        {
            return new ConflictObjectResult(new { error = exception.Message });
        }
    }
}