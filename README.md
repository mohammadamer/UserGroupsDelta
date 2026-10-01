# Microsoft Graph groups delta showcase

This .NET isolated Azure Functions app demonstrates initial and incremental synchronization of Microsoft Entra groups and group memberships with the Microsoft Graph delta API.

It follows every `@odata.nextLink`, merges repeated pages for large group memberships, saves only the final `@odata.deltaLink`, and recognizes group and membership removals. A renewable Blob lease prevents the timer and HTTP endpoint from advancing the same checkpoint concurrently.

## Prerequisites

- .NET 10 SDK
- Azure Functions Core Tools v4
- Azurite for local checkpoint storage
- A Microsoft Entra app registration with a client secret and the Microsoft Graph application permission `GroupMember.Read.All`

`GroupMember.Read.All` requires administrator consent. The application role ID is `98830695-27a2-44f7-8c18-0c3ebc9698f6`. Add the `Member.Read.Hidden` application permission only when hidden memberships must be included.

## Run locally

1. Start Azurite. The checked-in local configuration uses `UseDevelopmentStorage=true`.
2. Set `AzureTenantId`, `AzureClientId`, and `AzureClientSecret` in `UserGroupsDelta/local.settings.json`. Use the secret **value**, not its secret ID. This file is excluded by `.gitignore`.
3. Start the function host from the function project:

   ```powershell
   Set-Location .\UserGroupsDelta
   func start --port 7004
   ```

4. Trigger synchronization:

   ```powershell
   Invoke-RestMethod -Method Post -Uri http://localhost:7004/api/groups/delta
   ```

   To return only newly added memberships from an incremental delta:

   ```powershell
   Invoke-RestMethod -Method Post -Uri http://localhost:7004/api/groups/delta/member-additions
   ```

The first request returns the current state as `Present`. Later requests use the stored checkpoint and return group changes as `Upserted` or `Removed`; membership changes are `Added` or `Removed`.

Reset the checkpoint to force another initial synchronization:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:7004/api/groups/delta/reset
```

Reset deletes only the delta checkpoint. This showcase intentionally does not maintain a downstream group database.

## Azure configuration

Create a Microsoft Entra app registration for this daemon application, create a client secret, and grant its service principal:

- Microsoft Graph application permission `GroupMember.Read.All`, with administrator consent.
- `Storage Blob Data Contributor` on the checkpoint container or storage account when `GroupsDeltaStorageServiceUri` is used.

Client secrets are suitable for this requested showcase. For production, store the secret in a protected Function App setting or Key Vault reference, rotate it before expiration, and prefer a certificate or managed identity where possible.

Configure these Function App settings:

| Setting | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AzureTenantId` | Yes | None | Microsoft Entra tenant/directory ID. |
| `AzureClientId` | Yes | None | App registration application/client ID. |
| `AzureClientSecret` | Yes | None | App registration client secret value. |
| `GroupsDeltaSchedule` | Yes | Local: `0 */5 * * * *` | Six-field NCRONTAB timer schedule. |
| `GroupsDeltaStorageServiceUri` | Recommended in Azure | None | Blob service URI, for example `https://account.blob.core.windows.net`; uses the configured service principal. |
| `GroupsDeltaStorage` | Alternative | None | Blob connection string. |
| `GroupsDelta__ContainerName` | No | `groups-delta` | Checkpoint and lease container. |
| `GroupsDelta__ResponseLimit` | No | `100` | Maximum group changes returned by HTTP. Totals still describe the complete run. |
| `GroupsDelta__MaxRetries` | No | `3` | Retries for Graph `429`, `503`, and `504` responses. |
| `GroupsDelta__GraphBaseUrl` | No | `https://graph.microsoft.com/v1.0/` | Microsoft Graph base URL. |
| `GroupsDelta__Select` | No | See source default | Properties tracked by the delta token. Do not change this during an active synchronization cycle; reset first. |

If neither `GroupsDeltaStorageServiceUri` nor `GroupsDeltaStorage` is set, the app uses `AzureWebJobsStorage`.

The HTTP functions use `AuthorizationLevel.Function`. In Azure, include a valid function key when calling:

- `POST /api/groups/delta`
- `POST /api/groups/delta/member-additions`
- `POST /api/groups/delta/reset`

The general and member-additions endpoints share one delta checkpoint. Calling either endpoint consumes and advances the current delta. An initial sync reports existing memberships as `Present`, so the member-additions endpoint returns an empty `groups` collection until a later incremental run observes additions.

The timer runs every five minutes by default. A concurrent HTTP call receives `409 Conflict` while another run owns the Blob lease.

## Reliability behavior

- Graph continuation URLs are treated as opaque and restricted to the configured Graph host.
- `Retry-After` is honored for throttling and transient service failures.
- The checkpoint advances only after every page succeeds.
- Failures before checkpoint persistence replay the previous delta, providing at-least-once delivery.
- Raw delta links are stored in Blob Storage and are not returned or logged.
- A final response with no changes still replaces the old checkpoint with the new delta link.

Microsoft Entra changes can be eventually consistent, so recently changed objects might appear on a later run.

## Tests

```powershell
dotnet test .\UserGroupsDelta.slnx
```

## References

- [Get incremental changes for groups](https://learn.microsoft.com/graph/delta-query-groups)
- [Microsoft Graph permissions reference](https://learn.microsoft.com/graph/permissions-reference#groupmemberreadall)
- [OAuth 2.0 client credentials flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-client-creds-grant-flow)
- [Assign an Azure role for Blob data access](https://learn.microsoft.com/azure/storage/blobs/assign-azure-role-data-access)