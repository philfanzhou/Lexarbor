using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Lexarbor.Domain.Exceptions;

namespace Lexarbor.Service.Tests;

public class VocabularyExceptionMiddlewareTests
{
    public static TheoryData<Exception, int, string> KnownExceptions =>
        new()
        {
            { new DomainValidationException("Invalid request."), StatusCodes.Status400BadRequest, "Invalid request." },
            { new BadHttpRequestException("Sensitive JSON parser details."), StatusCodes.Status400BadRequest, "The request is invalid." },
            { new ResourceNotFoundException("Vocabulary not found."), StatusCodes.Status404NotFound, "Vocabulary not found." },
            { new ConflictException("Vocabulary already exists."), StatusCodes.Status409Conflict, "Vocabulary already exists." },
            { new BusinessRuleException("Vocabulary cannot be deleted."), StatusCodes.Status422UnprocessableEntity, "Vocabulary cannot be deleted." }
        };

    [Theory]
    [MemberData(nameof(KnownExceptions))]
    public async Task InvokeAsync_KnownException_ReturnsMappedFailureEnvelope(
        Exception exception,
        int expectedStatusCode,
        string expectedMessage)
    {
        var response = await InvokeMiddlewareAsync(exception);

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.StartsWith("application/json", response.ContentType);
        Assert.False(response.Success);
        Assert.Equal(expectedMessage, response.Message);
    }

    [Fact]
    public async Task InvokeAsync_UnexpectedException_ReturnsGenericFailureWithoutLeakingDetails()
    {
        const string secret = "postgresql://internal-user:internal-password@database/vocabulary";

        var response = await InvokeMiddlewareAsync(new InvalidOperationException(secret));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.False(response.Success);
        Assert.Equal("An unexpected error occurred.", response.Message);
        Assert.DoesNotContain(secret, response.RawBody);
        Assert.DoesNotContain(nameof(InvalidOperationException), response.RawBody);
    }

    private static async Task<MiddlewareResponse> InvokeMiddlewareAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var middleware = new VocabularyExceptionMiddleware(
            _ => throw exception,
            NullLogger<VocabularyExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        var rawBody = await reader.ReadToEndAsync();
        using var document = JsonDocument.Parse(rawBody);

        return new MiddlewareResponse(
            context.Response.StatusCode,
            context.Response.ContentType,
            document.RootElement.GetProperty("success").GetBoolean(),
            document.RootElement.GetProperty("message").GetString(),
            rawBody);
    }

    private sealed record MiddlewareResponse(
        int StatusCode,
        string? ContentType,
        bool Success,
        string? Message,
        string RawBody);
}
