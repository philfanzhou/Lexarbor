using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Ruoyu.Study.Vocabulary.Test;

public interface IGrpcClientFactory
{
    T Get<T>(string url) where T : class;
}

public class GrpcClientFactory : IGrpcClientFactory
{
    public T Get<T>(string url) where T : class
    {
        var channel = GrpcChannel.ForAddress(url);
        return (T)Activator.CreateInstance(typeof(T), channel)!;
    }
}

public static class GrpcClientExtensions
{
    public static void SetupGrpcClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IGrpcClientFactory, GrpcClientFactory>();
    }
}