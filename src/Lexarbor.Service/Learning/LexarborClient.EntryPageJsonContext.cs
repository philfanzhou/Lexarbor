using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

public sealed partial class LexarborClient
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(LexarborEnvelope<LexarborEntryPage>))]
    private partial class EntryPageJsonContext : JsonSerializerContext;
}
