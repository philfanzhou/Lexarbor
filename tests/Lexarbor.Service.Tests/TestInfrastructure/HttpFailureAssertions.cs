using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Lexarbor.Service.Tests.TestInfrastructure;

/// <summary>
/// Failure assertions that accept both failure shapes this service produces:
/// the ServiceMantle Problem Details written by the exception pipeline, and the
/// legacy envelope written by endpoint-explicit failures (including the batch
/// import's per-entry <c>errors</c>). Whichever shape a response has, the status
/// must match and the human-readable field must be present and non-empty.
/// </summary>
public static class HttpFailureAssertions
{
    public static async Task<JsonElement> AssertFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement.Clone();
        if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(title.GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("errorCode").GetString()));
            Assert.True(root.TryGetProperty("type", out var type)
                && type.GetString()!.StartsWith("urn:servicemantle:error:", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        }
        else
        {
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        }

        return root;
    }
}
