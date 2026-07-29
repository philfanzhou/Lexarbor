using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Vocabulary.Domain.Exceptions;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

public class VocabularyExceptionMiddlewareTests
{
    public static TheoryData<Exception, int, string> KnownExceptions =>
        new()
        {
            { new DomainValidationException("Invalid request."), StatusCodes.Status400BadRequest, "Invalid request." },
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

        response.StatusCode.Should().Be(expectedStatusCode);
        response.ContentType.Should().StartWith("application/json");
        response.Success.Should().BeFalse();
        response.Message.Should().Be(expectedMessage);
    }

    [Fact]
    public async Task InvokeAsync_UnexpectedException_ReturnsGenericFailureWithoutLeakingDetails()
    {
        const string secret = "postgresql://internal-user:internal-password@database/vocabulary";

        var response = await InvokeMiddlewareAsync(new InvalidOperationException(secret));

        response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        response.Success.Should().BeFalse();
        response.Message.Should().Be("An unexpected error occurred.");
        response.RawBody.Should().NotContain(secret);
        response.RawBody.Should().NotContain(nameof(InvalidOperationException));
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
