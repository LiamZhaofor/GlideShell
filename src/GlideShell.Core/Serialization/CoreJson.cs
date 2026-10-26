using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GlideShell.Core.Configuration;
using GlideShell.Core.Tracing;

namespace GlideShell.Core.Serialization;

// System.Text.Json 源生成：不用运行时反射，NativeAOT / 裁剪安全，启动也更快。

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GlideShellConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TraceHeaderLine))]
[JsonSerializable(typeof(TraceEndLine))]
[JsonSerializable(typeof(TraceHeader))]
internal sealed partial class TraceJsonContext : JsonSerializerContext;

internal static class CoreJson
{
    // 中文说明 / 配置保持可读（不转义成 \uXXXX）
    private static readonly JsonSerializerOptions ConfigOptions = new(ConfigJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions TraceOptions = new(TraceJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonTypeInfo<GlideShellConfig> Config { get; } = (JsonTypeInfo<GlideShellConfig>)ConfigOptions.GetTypeInfo(typeof(GlideShellConfig));

    public static JsonTypeInfo<TraceHeader> TraceHeader { get; } = (JsonTypeInfo<TraceHeader>)TraceOptions.GetTypeInfo(typeof(TraceHeader));

    public static JsonTypeInfo<TraceHeaderLine> TraceHeaderLine { get; } = (JsonTypeInfo<TraceHeaderLine>)TraceOptions.GetTypeInfo(typeof(TraceHeaderLine));

    public static JsonTypeInfo<TraceEndLine> TraceEndLine { get; } = (JsonTypeInfo<TraceEndLine>)TraceOptions.GetTypeInfo(typeof(TraceEndLine));
}
