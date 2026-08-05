using System;
using System.Collections.Generic;
using System.Text;

namespace Lexarbor.Domain;

public readonly record struct Result<T>(bool Success, T? Value, string? Error)
{
    public bool Success { get; } = Success;
    public T? Value { get; } = Value;
    public string? Error { get; } = Error;

    public static Result<T> Ok(T value) => new(true, value, null);

    public static Result<T> Fail(string error) => new(false, default, error);
}
