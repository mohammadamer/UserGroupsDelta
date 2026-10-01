using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UserGroupsDelta.GroupsDelta;

public interface IGroupsDeltaClient
{
    string GetInitialUrl();

    Task<GroupsDeltaPage> GetPageAsync(string url, CancellationToken cancellationToken);
}

public sealed class GroupsDeltaClient(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<GroupsDeltaOptions> options,
    ILogger<GroupsDeltaClient> logger) : IGroupsDeltaClient
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private readonly GroupsDeltaOptions _options = options.Value;
    private readonly Uri _graphBaseUri = new(options.Value.GraphBaseUrl, UriKind.Absolute);

    public string GetInitialUrl()
    {
        return new Uri(_graphBaseUri, $"groups/delta?$select={Uri.EscapeDataString(_options.Select)}").AbsoluteUri;
    }

    public async Task<GroupsDeltaPage> GetPageAsync(string url, CancellationToken cancellationToken)
    {
        var requestUri = ValidateGraphUrl(url);
        var accessToken = await credential.GetTokenAsync(
            new TokenRequestContext(GraphScopes),
            cancellationToken);

        var attempt = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await ParsePageAsync(content, cancellationToken);
            }

            if (!IsTransient(response.StatusCode) || attempt >= _options.MaxRetries)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Microsoft Graph returned {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}",
                    null,
                    response.StatusCode);
            }

            var delay = GetRetryDelay(response, attempt);
            logger.LogWarning(
                "Microsoft Graph returned {StatusCode}. Retrying after {Delay} (attempt {Attempt}).",
                (int)response.StatusCode,
                delay,
                attempt + 1);
            await Task.Delay(delay, cancellationToken);
            attempt++;
        }
    }

    private Uri ValidateGraphUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, _graphBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Microsoft Graph returned an invalid continuation URL.");
        }

        return uri;
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
            return retryAfter;

        if (response.Headers.RetryAfter?.Date is { } retryDate)
            return retryDate > DateTimeOffset.UtcNow
                ? retryDate - DateTimeOffset.UtcNow
                : TimeSpan.Zero;

        return TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
    }

    private static async Task<GroupsDeltaPage> ParsePageAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var groups = root.GetProperty("value")
            .EnumerateArray()
            .Select(ParseGroup)
            .ToArray();

        return new GroupsDeltaPage(
            groups,
            GetOptionalString(root, "@odata.nextLink"),
            GetOptionalString(root, "@odata.deltaLink"));
    }

    private static GroupDelta ParseGroup(JsonElement group)
    {
        var id = group.GetProperty("id").GetString()
            ?? throw new JsonException("A group delta item did not contain an id.");
        var groupTypes = group.TryGetProperty("groupTypes", out var groupTypesElement)
            ? groupTypesElement.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray()
            : [];
        var members = group.TryGetProperty("members@delta", out var membersElement)
            ? membersElement.EnumerateArray().Select(ParseMember).ToArray()
            : [];

        return new GroupDelta(
            id,
            GetOptionalString(group, "displayName"),
            GetOptionalString(group, "description"),
            GetOptionalString(group, "mail"),
            GetOptionalBoolean(group, "mailEnabled"),
            GetOptionalBoolean(group, "securityEnabled"),
            groupTypes,
            GetRemovedReason(group),
            members);
    }

    private static MemberDelta ParseMember(JsonElement member)
    {
        var id = member.GetProperty("id").GetString()
            ?? throw new JsonException("A member delta item did not contain an id.");

        return new MemberDelta(
            id,
            GetOptionalString(member, "@odata.type"),
            GetRemovedReason(member));
    }

    private static string? GetRemovedReason(JsonElement element)
    {
        return element.TryGetProperty("@removed", out var removed)
            ? GetOptionalString(removed, "reason")
            : null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static bool? GetOptionalBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
    }
}