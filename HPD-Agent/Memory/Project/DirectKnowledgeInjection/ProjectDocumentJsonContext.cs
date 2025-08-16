using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace ProjectCagMemory
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(List<ProjectDocument>))]
    internal partial class ProjectDocumentJsonContext : JsonSerializerContext
    {
    }
}
