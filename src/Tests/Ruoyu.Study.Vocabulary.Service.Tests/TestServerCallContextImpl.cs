using System;
using System.Threading;
using Grpc.Core;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

/// <summary>
/// Minimal test implementation of ServerCallContext for gRPC 2.62.0.
/// Adapted from ruoyu.homework/src/Tests/Ruoyu.Study.Homework.Service.Tests/TestServerCallContextImpl.cs.
/// </summary>
public class TestServerCallContextImpl : ServerCallContext
{
    private readonly Metadata _requestHeaders;
    private readonly CancellationToken _cancellationToken;
    private Status _status = Status.DefaultSuccess;
    private WriteOptions? _writeOptions;
    private readonly Metadata _responseTrailers = new Metadata();
    private readonly AuthContext _authContext;
    private readonly ContextPropagationToken? _contextPropagationToken;
    private readonly Action<Metadata>? _writeHeadersFunc;

    private TestServerCallContextImpl(
        Metadata? requestHeaders,
        CancellationToken cancellationToken,
        string? peer,
        DateTime? deadline,
        Metadata? responseHeaders,
        AuthContext? authContext,
        ContextPropagationToken? contextPropagationToken,
        Action<Metadata>? writeHeadersFunc)
    {
        _requestHeaders = requestHeaders ?? new Metadata();
        _cancellationToken = cancellationToken;
        PeerString = peer ?? "127.0.0.1:12345";
        DeadlineValue = deadline ?? DateTime.MaxValue;
        ResponseHeadersAsyncVal = responseHeaders != null
            ? System.Threading.Tasks.Task.FromResult(responseHeaders)
            : null;
        _authContext = authContext ?? new AuthContext(string.Empty, new Dictionary<string, System.Collections.Generic.List<AuthProperty>>());
        _contextPropagationToken = contextPropagationToken;
        _writeHeadersFunc = writeHeadersFunc;
    }

    public string PeerString { get; }
    public DateTime DeadlineValue { get; }
    public System.Threading.Tasks.Task<Metadata>? ResponseHeadersAsyncVal { get; }

    protected override string MethodCore => "/test/TestMethod";
    protected override string HostCore => "test.host";
    protected override string PeerCore => PeerString;
    protected override DateTime DeadlineCore => DeadlineValue;
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override Metadata ResponseTrailersCore => _responseTrailers;

    protected override Status StatusCore { get => _status; set => _status = value; }
    protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }

    protected override AuthContext AuthContextCore => _authContext;

    protected override ContextPropagationToken? CreatePropagationTokenCore(ContextPropagationOptions? options)
        => _contextPropagationToken;

    protected override System.Threading.Tasks.Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        _writeHeadersFunc?.Invoke(responseHeaders);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public static ServerCallContext Create(
        string? peer = null,
        DateTime? deadline = null,
        Metadata? requestHeaders = null,
        CancellationToken cancellationToken = default,
        Action<Metadata>? writeHeadersFunc = null)
    {
        return new TestServerCallContextImpl(
            requestHeaders: requestHeaders,
            cancellationToken: cancellationToken,
            peer: peer,
            deadline: deadline,
            responseHeaders: null,
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: writeHeadersFunc);
    }
}
