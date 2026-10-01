using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UserGroupsDelta.GroupsDelta;

namespace UserGroupsDelta.Tests;

public sealed class GroupsDeltaSyncServiceTests
{
    [Fact]
    public async Task SynchronizeAsync_MergesRepeatedGroupPagesAndSavesFinalCheckpoint()
    {
        // Arrange
        var firstPage = new GroupsDeltaPage(
            [CreateGroup("group-1", new MemberDelta("user-1", "#microsoft.graph.user", null))],
            "https://graph.microsoft.com/page-2",
            null);
        var secondPage = new GroupsDeltaPage(
            [CreateGroup("group-1", new MemberDelta("user-2", "#microsoft.graph.user", null))],
            null,
            "https://graph.microsoft.com/final-delta");
        var client = new FakeDeltaClient(firstPage, secondPage);
        var lease = new FakeLease();
        var service = CreateService(client, new FakeCheckpointStore(lease));

        // Act
        var result = await service.SynchronizeAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Initial", result.SyncType);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, result.MemberChangeCount);
        Assert.Equal(2, Assert.Single(result.Changes).Members.Count);
        Assert.Equal("https://graph.microsoft.com/final-delta", lease.SavedCheckpoint?.DeltaLink);
        Assert.Equal([client.InitialUrl, "https://graph.microsoft.com/page-2"], client.RequestedUrls);
    }

    [Fact]
    public async Task SynchronizeAsync_UsesCheckpointMapsRemovalsAndTruncatesResponse()
    {
        // Arrange
        var removedMember = new MemberDelta("user-1", "#microsoft.graph.user", "deleted");
        var client = new FakeDeltaClient(new GroupsDeltaPage(
            [
                CreateGroup("group-1", removedMember),
                CreateGroup("group-2") with { RemovedReason = "deleted" }
            ],
            null,
            "https://graph.microsoft.com/new-delta"));
        var lease = new FakeLease
        {
            Checkpoint = new GroupsDeltaCheckpoint("https://graph.microsoft.com/old-delta", DateTimeOffset.UtcNow)
        };
        var service = CreateService(client, new FakeCheckpointStore(lease), responseLimit: 1);

        // Act
        var result = await service.SynchronizeAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Incremental", result.SyncType);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.GroupCount);
        Assert.Single(result.Changes);
        Assert.Equal("Removed", Assert.Single(result.Changes).Members.Single().Action);
        Assert.Equal("https://graph.microsoft.com/old-delta", Assert.Single(client.RequestedUrls));
    }

    [Fact]
    public async Task SynchronizeAsync_DoesNotSaveCheckpointWhenFinalPageHasNoDeltaLink()
    {
        // Arrange
        var client = new FakeDeltaClient(new GroupsDeltaPage([], null, null));
        var lease = new FakeLease();
        var service = CreateService(client, new FakeCheckpointStore(lease));

        // Act
        var action = () => service.SynchronizeAsync(CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Null(lease.SavedCheckpoint);
    }

    [Fact]
    public async Task SynchronizeMemberAdditionsAsync_ReturnsOnlyGroupsAndMembersThatWereAdded()
    {
        // Arrange
        var client = new FakeDeltaClient(new GroupsDeltaPage(
            [
                CreateGroup(
                    "group-1",
                    new MemberDelta("user-added", "#microsoft.graph.user", null),
                    new MemberDelta("user-removed", "#microsoft.graph.user", "deleted")),
                CreateGroup(
                    "group-2",
                    new MemberDelta("user-removed-only", "#microsoft.graph.user", "deleted"))
            ],
            null,
            "https://graph.microsoft.com/new-delta"));
        var lease = new FakeLease
        {
            Checkpoint = new GroupsDeltaCheckpoint("https://graph.microsoft.com/old-delta", DateTimeOffset.UtcNow)
        };
        var service = CreateService(client, new FakeCheckpointStore(lease));

        // Act
        var result = await service.SynchronizeMemberAdditionsAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Incremental", result.SyncType);
        Assert.Equal(1, result.GroupCount);
        Assert.Equal(1, result.MemberAdditionCount);
        var group = Assert.Single(result.Groups);
        Assert.Equal("group-1", group.Id);
        Assert.Equal("user-added", Assert.Single(group.Members).Id);
    }

    [Fact]
    public async Task SynchronizeAsync_ThrowsWhenAnotherRunOwnsLease()
    {
        // Arrange
        var service = CreateService(new FakeDeltaClient(), new FakeCheckpointStore(null));

        // Act
        var action = () => service.SynchronizeAsync(CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<GroupsDeltaSyncInProgressException>(action);
    }

    [Fact]
    public async Task ResetAsync_DeletesCheckpointWhileHoldingLease()
    {
        // Arrange
        var lease = new FakeLease { DeleteResult = true };
        var service = CreateService(new FakeDeltaClient(), new FakeCheckpointStore(lease));

        // Act
        var deleted = await service.ResetAsync(CancellationToken.None);

        // Assert
        Assert.True(deleted);
        Assert.True(lease.DeleteCalled);
        Assert.True(lease.Disposed);
    }

    private static GroupsDeltaSyncService CreateService(
        IGroupsDeltaClient client,
        IGroupsDeltaCheckpointStore store,
        int responseLimit = 100)
    {
        return new GroupsDeltaSyncService(
            client,
            store,
            Options.Create(new GroupsDeltaOptions { ResponseLimit = responseLimit }),
            NullLogger<GroupsDeltaSyncService>.Instance);
    }

    private static GroupDelta CreateGroup(string id, params MemberDelta[] members)
    {
        return new GroupDelta(
            id,
            "Group",
            "Description",
            null,
            false,
            true,
            [],
            null,
            members);
    }

    private sealed class FakeDeltaClient(params GroupsDeltaPage[] pages) : IGroupsDeltaClient
    {
        private readonly Queue<GroupsDeltaPage> _pages = new(pages);

        public string InitialUrl { get; } = "https://graph.microsoft.com/initial";

        public List<string> RequestedUrls { get; } = [];

        public string GetInitialUrl() => InitialUrl;

        public Task<GroupsDeltaPage> GetPageAsync(string url, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed class FakeCheckpointStore(IGroupsDeltaLease? lease) : IGroupsDeltaCheckpointStore
    {
        public Task<IGroupsDeltaLease?> TryAcquireLeaseAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(lease);
        }
    }

    private sealed class FakeLease : IGroupsDeltaLease
    {
        public GroupsDeltaCheckpoint? Checkpoint { get; init; }

        public GroupsDeltaCheckpoint? SavedCheckpoint { get; private set; }

        public bool DeleteResult { get; init; }

        public bool DeleteCalled { get; private set; }

        public bool Disposed { get; private set; }

        public Task<GroupsDeltaCheckpoint?> ReadCheckpointAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Checkpoint);
        }

        public Task SaveCheckpointAsync(
            GroupsDeltaCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            SavedCheckpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteCheckpointAsync(CancellationToken cancellationToken)
        {
            DeleteCalled = true;
            return Task.FromResult(DeleteResult);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}