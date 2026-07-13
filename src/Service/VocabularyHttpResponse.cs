using System.Net;
using Microsoft.AspNetCore.Http;

namespace Ruoyu.Study.Vocabulary.Service;

/// <summary>
/// Unified HTTP response envelope helper for all Vocabulary HTTP endpoints.
/// Response format:
///   Success: { "success": true, "data": ... } / { "success": true }
///   Failure: { "success": false, "message": "..." }
/// </summary>
public static class VocabularyHttpResponse
{
    /// <summary>
    /// Success response: { "success": true, "data": ... }
    /// </summary>
    public static IResult Ok<T>(T data) => Results.Ok(new { success = true, data });

    /// <summary>
    /// Success response (no data, just success=true).
    /// </summary>
    public static IResult Ok() => Results.Ok(new { success = true });

    /// <summary>
    /// 400 Bad Request: { "success": false, "message": "..." }
    /// </summary>
    public static IResult BadRequest(string message)
        => Results.BadRequest(new { success = false, message });

    /// <summary>
    /// 404 Not Found: { "success": false, "message": "..." }
    /// </summary>
    public static IResult NotFound(string message)
        => Results.NotFound(new { success = false, message });

    /// <summary>
    /// 500 Internal Server Error: { "success": false, "message": "..." }
    /// </summary>
    public static IResult Internal(string message)
        => Results.Json(new { success = false, message }, statusCode: (int)HttpStatusCode.InternalServerError);
}
