using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMtr.Desktop.Services;

/// <summary>
/// The stored shape of the accepted settings. Every value is optional so a
/// missing field takes its default and a file written by an earlier build
/// still loads; <c>SchemaVersion</c> is the one field that must agree, because
/// an unknown version is treated as damaged.
/// </summary>
internal sealed record SettingsDocument
{
    public int SchemaVersion { get; init; }

    public double? ProbeIntervalSeconds { get; init; }

    public int? PayloadBytes { get; init; }

    public int? HopLimit { get; init; }

    public double? ReplyTimeoutSeconds { get; init; }

    public bool? ResolveNames { get; init; }

    public int? HistorySize { get; init; }

    public string? Theme { get; init; }
}

/// <summary>
/// The stored shape of the target history, newest entry first.
/// </summary>
internal sealed record HistoryDocument
{
    public int SchemaVersion { get; init; }

    public List<string>? Entries { get; init; }
}

/// <summary>
/// Source-generated serialization: the AOT publish forbids the reflection
/// based serializer, so a new persisted value means a new member here as well
/// as on the document.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(HistoryDocument))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;
