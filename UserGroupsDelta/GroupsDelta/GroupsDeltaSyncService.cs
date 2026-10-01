using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UserGroupsDelta.GroupsDelta;

public interface IGroupsDeltaSyncService
{
    Task<GroupsSyncResult> SynchronizeAsync(CancellationToken cancellationToken);

    Task<GroupsMemberAdditionsResult> SynchronizeMemberAdditionsAsync(CancellationToken cancellationToken);

    Task<bool> ResetAsync(CancellationToken cancellationToken);
}

public sealed class GroupsDeltaSyncService(
    IGroupsDeltaClient deltaClient,
    IGroupsDeltaCheckpointStore checkpointStore,
    IOptions<GroupsDeltaOptions> options,
    ILogger<GroupsDeltaSyncService> logger) : IGroupsDeltaSyncService
{
    private readonly int _responseLimit = Math.Max(0, options.Value.ResponseLimit);

    public async Task<GroupsSyncResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var execution = await SynchronizeCoreAsync(cancellationToken);

        return new GroupsSyncResult(
            execution.SyncType,
            execution.PageCount,
            execution.Changes.Count,
            execution.Changes.Sum(change => change.Members.Count),
            execution.Changes.Count > _responseLimit,
            execution.CheckpointUpdatedAt,
            execution.Changes.Take(_responseLimit).ToArray());
    }

    public async Task<GroupsMemberAdditionsResult> SynchronizeMemberAdditionsAsync(
        CancellationToken cancellationToken)
    {
        var execution = await SynchronizeCoreAsync(cancellationToken);
        var groups = execution.Changes
            .Select(change => new GroupMemberAdditions(
                change.Id,
                change.DisplayName,
                change.Members.Where(member => member.Action == "Added").ToArray()))
            .Where(group => group.Members.Count > 0)
            .ToArray();

        return new GroupsMemberAdditionsResult(
            execution.SyncType,
            execution.PageCount,
            groups.Length,
            groups.Sum(group => group.Members.Count),
            groups.Length > _responseLimit,
            execution.CheckpointUpdatedAt,
            groups.Take(_responseLimit).ToArray());
    }

    public async Task<bool> ResetAsync(CancellationToken cancellationToken)
    {
        await using var lease = await checkpointStore.TryAcquireLeaseAsync(cancellationToken)
            ?? throw new GroupsDeltaSyncInProgressException();
        return await lease.DeleteCheckpointAsync(cancellationToken);
    }

    private async Task<SyncExecution> SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        await using var lease = await checkpointStore.TryAcquireLeaseAsync(cancellationToken)
            ?? throw new GroupsDeltaSyncInProgressException();
        var checkpoint = await lease.ReadCheckpointAsync(cancellationToken);
        var isInitialSync = checkpoint is null;
        var url = checkpoint?.DeltaLink ?? deltaClient.GetInitialUrl();
        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        var groupOrder = new List<string>();
        var pageCount = 0;
        string? finalDeltaLink = null;

        while (url is not null)
        {
            var page = await deltaClient.GetPageAsync(url, cancellationToken);
            pageCount++;

            foreach (var group in page.Groups)
            {
                if (!groups.TryGetValue(group.Id, out var accumulator))
                {
                    accumulator = new GroupAccumulator(group);
                    groups.Add(group.Id, accumulator);
                    groupOrder.Add(group.Id);
                }
                else
                {
                    accumulator.Merge(group);
                }
            }

            if (page.NextLink is not null)
            {
                url = page.NextLink;
                continue;
            }

            finalDeltaLink = page.DeltaLink
                ?? throw new InvalidOperationException("The final Microsoft Graph page did not contain an @odata.deltaLink.");
            url = null;
        }

        var allChanges = groupOrder
            .Select(groupId => groups[groupId].ToChange(isInitialSync))
            .ToArray();
        var checkpointUpdatedAt = DateTimeOffset.UtcNow;

        foreach (var change in allChanges)
        {
            logger.LogInformation(
                "Group {GroupId} action {Action}; {MemberCount} membership changes.",
                change.Id,
                change.Action,
                change.Members.Count);
        }

        await lease.SaveCheckpointAsync(
            new GroupsDeltaCheckpoint(finalDeltaLink!, checkpointUpdatedAt),
            cancellationToken);

        return new SyncExecution(
            isInitialSync ? "Initial" : "Incremental",
            pageCount,
            checkpointUpdatedAt,
            allChanges);
    }

    private sealed record SyncExecution(
        string SyncType,
        int PageCount,
        DateTimeOffset CheckpointUpdatedAt,
        IReadOnlyList<GroupChange> Changes);

    private sealed class GroupAccumulator
    {
        private readonly List<MemberDelta> _members = [];
        private readonly HashSet<string> _memberKeys = new(StringComparer.OrdinalIgnoreCase);
        private GroupDelta _group;

        public GroupAccumulator(GroupDelta group)
        {
            _group = group with { Members = [] };
            AddMembers(group.Members);
        }

        public void Merge(GroupDelta group)
        {
            _group = group with { Members = [] };
            AddMembers(group.Members);
        }

        public GroupChange ToChange(bool isInitialSync)
        {
            var groupAction = _group.RemovedReason is not null
                ? "Removed"
                : GetCurrentAction(isInitialSync);
            var memberChanges = _members
                .Select(member => new MemberChange(
                    member.Id,
                    member.Type,
                    member.RemovedReason is not null
                        ? "Removed"
                        : GetMemberAction(isInitialSync)))
                .ToArray();

            return new GroupChange(
                _group.Id,
                groupAction,
                _group.RemovedReason,
                _group.DisplayName,
                _group.Description,
                _group.Mail,
                _group.MailEnabled,
                _group.SecurityEnabled,
                _group.GroupTypes,
                memberChanges);
        }

        private static string GetCurrentAction(bool isInitialSync)
        {
            return isInitialSync ? "Present" : "Upserted";
        }

        private static string GetMemberAction(bool isInitialSync)
        {
            return isInitialSync ? "Present" : "Added";
        }

        private void AddMembers(IEnumerable<MemberDelta> members)
        {
            foreach (var member in members)
            {
                var key = $"{member.Id}\u001f{member.Type}\u001f{member.RemovedReason}";

                if (_memberKeys.Add(key))
                    _members.Add(member);
            }
        }
    }
}

public sealed class GroupsDeltaSyncInProgressException()
    : InvalidOperationException("A groups delta synchronization is already running.");