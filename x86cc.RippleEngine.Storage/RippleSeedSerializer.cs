using System.Text.Json;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The single place in-memory work items are turned into <see cref="RippleSeed"/>s. Serialises with
/// <see cref="RippleJson.Options"/> — the options the handler registry deserialises with on the worker — and
/// stamps <see cref="RippleTypeName"/>'s name for <c>TMessage</c> as each seed's payload type.
/// </summary>
internal static class RippleSeedSerializer
{
    public static List<RippleSeed> Serialize<TMessage>(IEnumerable<TMessage> messages)
        where TMessage : notnull
    {
        var type = RippleTypeName.Of<TMessage>();
        var seeds = new List<RippleSeed>();
        foreach (var message in messages)
        {
            seeds.Add(new RippleSeed(JsonSerializer.Serialize(message, typeof(TMessage), RippleJson.Options), type));
        }

        return seeds;
    }
}
