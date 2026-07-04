using System.Text.Json;
using System.Text.Json.Serialization;
using Dotsy.Core.Loop.Data;

namespace Dotsy.Cli;

/// <summary>
/// Serializes loop events into the one-line envelope used by the headless
/// <c>--output-format stream-json</c> mode: <c>{"type":"&lt;EventName&gt;","data":{…}}</c>.
/// </summary>
/// <remarks>
/// The payload must be serialized against the event's <em>runtime</em> type.
/// <see cref="LoopEvent"/> is an abstract record with no members, so serializing
/// an event through its static base type produces an empty <c>data</c> object -
/// the derived properties (Text, Reason, …) never appear. Serializing the
/// concrete type explicitly restores them.
/// </remarks>
internal static class HeadlessStreamJson
{
    // Enums (e.g. EndReason, StopReason) are emitted as their names rather than numeric
    // values so the stream is self-describing for consumers reading it.
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Format(LoopEvent ev) =>
        JsonSerializer.Serialize(new
        {
            type = ev.GetType().Name,
            data = JsonSerializer.SerializeToElement(ev, ev.GetType(), Options)
        });
}
