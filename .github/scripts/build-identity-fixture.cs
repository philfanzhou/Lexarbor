// Synthetic loopback-only OIDC fixture for test-build-identity.py. No persisted keys.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

using var key = RSA.Create(2048);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
string issuer = "";
var parameters = key.ExportParameters(false);
app.MapGet("/.well-known/openid-configuration", () => new
{
    issuer, jwks_uri = issuer + "/keys", id_token_signing_alg_values_supported = new[] { "RS256" }
});
app.MapGet("/keys", () => new { keys = new[] { new
{
    kty = "RSA", use = "sig", kid = "synthetic", alg = "RS256",
    n = Encode(parameters.Modulus!), e = Encode(parameters.Exponent!)
} } });
await app.StartAsync();
issuer = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = "synthetic" }));
var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
{
    iss = issuer, aud = "lexarbor", sub = "synthetic-admin", role = "admin",
    exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
}));
var unsigned = header + "." + payload;
var token = unsigned + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
// Parent reads this pipe in memory; never redirect it to an artifact/log.
Console.WriteLine(JsonSerializer.Serialize(new { issuer, token }));
await app.WaitForShutdownAsync();
static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
