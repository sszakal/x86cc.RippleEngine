using System.Text.Json;
using System.Text.Json.Serialization;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The single place in-memory work items are turned into <see cref="RippleSeed"/>s. Uses the engine's payload
/// JSON options (web casing + string enums) so a payload written here round-trips cleanly through the handler
/// registry on the worker, and stamps <see cref="RippleTypeName"/>'s name for <c>TMessage</c> as each seed's payload type.
/// </summary>
internal static class RippleSeedSerializer
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<RippleSeed> Serialize<TMessage>(IEnumerable<TMessage> messages)
        where TMessage : notnull
    {
        var type = RippleTypeName.Of<TMessage>();
        var seeds = new List<RippleSeed>();
        foreach (var message in messages)
        {
            seeds.Add(new RippleSeed(JsonSerializer.Serialize(message, typeof(TMessage), Options), type));
        }

        return seeds;
    }
}
