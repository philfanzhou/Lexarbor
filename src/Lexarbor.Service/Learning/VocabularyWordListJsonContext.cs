using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VocabularyWordListResponse))]
internal partial class VocabularyWordListJsonContext : JsonSerializerContext;
