using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UserGroupsDelta.GroupsDelta;

public interface IGroupsDeltaLease : IAsyncDisposable
{
    Task<GroupsDeltaCheckpoint?> ReadCheckpointAsync(CancellationToken cancellationToken);

    Task SaveCheckpointAsync(GroupsDeltaCheckpoint checkpoint, CancellationToken cancellationToken);

    Task<bool> DeleteCheckpointAsync(CancellationToken cancellationToken);
}

public interface IGroupsDeltaCheckpointStore
{
    Task<IGroupsDeltaLease?> TryAcquireLeaseAsync(CancellationToken cancellationToken);
}

public sealed class BlobGroupsDeltaCheckpointStore(
    BlobServiceClient blobServiceClient,
    IOptions<GroupsDeltaOptions> options,
    ILogger<BlobGroupsDeltaCheckpointStore> logger) : IGroupsDeltaCheckpointStore
{
    private const string CheckpointBlobName = "checkpoint.json";
    private const string LockBlobName = "sync.lock";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient(options.Value.ContainerName);

    public async Task<IGroupsDeltaLease?> TryAcquireLeaseAsync(CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var lockBlob = _container.GetBlobClient(LockBlobName);

        try
        {
            await lockBlob.UploadAsync(
                BinaryData.FromString("groups-delta-lock"),
                overwrite: false,
                cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.Conflict)
        {
            // The lock blob is intentionally permanent; only its lease controls ownership.
        }

        var leaseClient = lockBlob.GetBlobLeaseClient();

        try
        {
            await leaseClient.AcquireAsync(LeaseDuration, cancellationToken: cancellationToken);
            return new BlobGroupsDeltaLease(
                _container.GetBlobClient(CheckpointBlobName),
                leaseClient,
                logger);
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    private sealed class BlobGroupsDeltaLease : IGroupsDeltaLease
    {
        private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(40);
        private readonly BlobClient _checkpointBlob;
        private readonly BlobLeaseClient _leaseClient;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _renewalCancellation = new();
        private readonly Task _renewalTask;

        public BlobGroupsDeltaLease(
            BlobClient checkpointBlob,
            BlobLeaseClient leaseClient,
            ILogger logger)
        {
            _checkpointBlob = checkpointBlob;
            _leaseClient = leaseClient;
            _logger = logger;
            _renewalTask = RenewLeaseAsync(_renewalCancellation.Token);
        }

        public async Task<GroupsDeltaCheckpoint?> ReadCheckpointAsync(CancellationToken cancellationToken)
        {
            try
            {
                var download = await _checkpointBlob.DownloadContentAsync(cancellationToken);
                return download.Value.Content.ToObjectFromJson<GroupsDeltaCheckpoint>();
            }
            catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("The groups delta checkpoint is not valid JSON.", exception);
            }
        }

        public async Task SaveCheckpointAsync(
            GroupsDeltaCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            await _checkpointBlob.UploadAsync(
                BinaryData.FromObjectAsJson(checkpoint),
                overwrite: true,
                cancellationToken: cancellationToken);
        }

        public async Task<bool> DeleteCheckpointAsync(CancellationToken cancellationToken)
        {
            var response = await _checkpointBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return response.Value;
        }

        public async ValueTask DisposeAsync()
        {
            await _renewalCancellation.CancelAsync();

            try
            {
                await _renewalTask;
            }
            catch (OperationCanceledException exception)
            {
                _logger.LogDebug(exception, "Stopped renewing the groups delta lease.");
            }

            try
            {
                await _leaseClient.ReleaseAsync(cancellationToken: CancellationToken.None);
            }
            catch (RequestFailedException exception) when (exception.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.NotFound)
            {
                _logger.LogWarning(exception, "The groups delta lease had already expired or been released.");
            }

            _renewalCancellation.Dispose();
        }

        private async Task RenewLeaseAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(RenewalInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _leaseClient.RenewAsync(cancellationToken: cancellationToken);
            }
        }
    }
}