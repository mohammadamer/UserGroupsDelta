namespace UserGroupsDelta.GroupsDelta;

public sealed record MemberDelta(
    string Id,
    string? Type,
    string? RemovedReason);

public sealed record GroupDelta(
    string Id,
    string? DisplayName,
    string? Description,
    string? Mail,
    bool? MailEnabled,
    bool? SecurityEnabled,
    IReadOnlyList<string> GroupTypes,
    string? RemovedReason,
    IReadOnlyList<MemberDelta> Members);

public sealed record GroupsDeltaPage(
    IReadOnlyList<GroupDelta> Groups,
    string? NextLink,
    string? DeltaLink);

public sealed record MemberChange(
    string Id,
    string? Type,
    string Action);

public sealed record GroupChange(
    string Id,
    string Action,
    string? RemovedReason,
    string? DisplayName,
    string? Description,
    string? Mail,
    bool? MailEnabled,
    bool? SecurityEnabled,
    IReadOnlyList<string> GroupTypes,
    IReadOnlyList<MemberChange> Members);

public sealed record GroupsSyncResult(
    string SyncType,
    int PageCount,
    int GroupCount,
    int MemberChangeCount,
    bool Truncated,
    DateTimeOffset CheckpointUpdatedAt,
    IReadOnlyList<GroupChange> Changes);

public sealed record GroupMemberAdditions(
    string Id,
    string? DisplayName,
    IReadOnlyList<MemberChange> Members);

public sealed record GroupsMemberAdditionsResult(
    string SyncType,
    int PageCount,
    int GroupCount,
    int MemberAdditionCount,
    bool Truncated,
    DateTimeOffset CheckpointUpdatedAt,
    IReadOnlyList<GroupMemberAdditions> Groups);

public sealed record GroupsDeltaCheckpoint(
    string DeltaLink,
    DateTimeOffset UpdatedAt);