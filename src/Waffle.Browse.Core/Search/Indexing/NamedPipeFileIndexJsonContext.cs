using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waffle.Browse.Core.Search.Indexing;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PipeChunkFrame))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipeRequestHeader))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipePathMessage))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipeBaselineHeader))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipeCheckpointMessage))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipeEntryMessage))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipeEntryBatchMessage))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipeWarningMessage))]
[JsonSerializable(typeof(NamedPipeFileIndexProtocol.PipeResultHeader))]
internal sealed partial class NamedPipeFileIndexJsonContext : JsonSerializerContext;
