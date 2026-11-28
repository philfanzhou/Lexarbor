using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

public sealed partial class CatalogClient
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Envelope<CatalogEntryPage>))]
    private partial class EntryPageJsonContext : JsonSerializerContext;
}
