using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UserGroupsDelta.GroupsDelta;

namespace UserGroupsDelta.Tests;

public sealed class GroupsDeltaClientTests
{
    [Fact]
    public async Task GetPageAsync_ParsesGroupAndMembershipRemovalAnnotations()
    {
        // Arrange
        const string responseJson = """
            {
              "@odata.deltaLink": "https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=next",
              "value": [
                {
                  "id": "group-1",
                  "displayName": "Engineering",
                  "mailEnabled": true,
                  "securityEnabled": false,
                  "groupTypes": ["Unified"],
                  "members@delta": [
                    {
                      "id": "user-1",
                      "@odata.type": "#microsoft.graph.user",
                      "@removed": { "reason": "deleted" }
                    }
                  ]
                }
              ]
            }
            """;
        var client = CreateClient(new QueueHttpMessageHandler(CreateResponse(responseJson)));

        // Act
        var page = await client.GetPageAsync(
            "https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=current",
            CancellationToken.None);

        // Assert
        var group = Assert.Single(page.Groups);
        Assert.Equal("Engineering", group.DisplayName);
        Assert.True(group.MailEnabled);
        Assert.False(group.SecurityEnabled);
        Assert.Equal("Unified", Assert.Single(group.GroupTypes));
        Assert.Equal("deleted", Assert.Single(group.Members).RemovedReason);
        Assert.NotNull(page.DeltaLink);
    }

    [Fact]
    public async Task GetPageAsync_RetriesThrottledRequestAndHonorsRetryAfter()
    {
        // Arrange
        var throttled = CreateResponse("{}", HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new QueueHttpMessageHandler(
            throttled,
            CreateResponse("{\"@odata.deltaLink\":\"https://graph.microsoft.com/final\",\"value\":[]}"));
        var client = CreateClient(handler);

        // Act
        var page = await client.GetPageAsync(
            "https://graph.microsoft.com/v1.0/groups/delta",
            CancellationToken.None);

        // Assert
        Assert.Empty(page.Groups);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetPageAsync_RejectsContinuationUrlForAnotherHost()
    {
        // Arrange
        var handler = new QueueHttpMessageHandler(CreateResponse("{}"));
        var client = CreateClient(handler);

        // Act
        var action = () => client.GetPageAsync(
            "https://example.com/stolen-token",
            CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void GetInitialUrl_IncludesConfiguredSelection()
    {
        // Arrange
        var options = Options.Create(new GroupsDeltaOptions { Select = "id,members" });
        var client = new GroupsDeltaClient(
            new HttpClient(new QueueHttpMessageHandler()),
            new FakeTokenCredential(),
            options,
            NullLogger<GroupsDeltaClient>.Instance);

        // Act
        var url = client.GetInitialUrl();

        // Assert
        Assert.Contains("groups/delta", url, StringComparison.Ordinal);
        Assert.Contains("%2Cid", url.Replace("id%2Cmembers", "%2Cid", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("members", url, StringComparison.Ordinal);
    }

    private static GroupsDeltaClient CreateClient(QueueHttpMessageHandler handler)
    {
        return new GroupsDeltaClient(
            new HttpClient(handler),
            new FakeTokenCredential(),
            Options.Create(new GroupsDeltaOptions { MaxRetries = 2 }),
            NullLogger<GroupsDeltaClient>.Instance);
    }

    private static HttpResponseMessage CreateResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token = new("test-token", DateTimeOffset.MaxValue);

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            return Token;
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Token);
        }
    }
}