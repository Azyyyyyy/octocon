using System.Text.Json.Serialization;

namespace Interfold.SPDump.Http;

[JsonSerializable(typeof(RequestMeta))]
[JsonSerializable(typeof(ResponseMeta))]
internal partial class HttpLoggingJsonContext : JsonSerializerContext
{
}

