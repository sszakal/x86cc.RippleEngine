using System.Text.Json;
using System.Text.Json.Serialization;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The engine's one JSON contract — every payload and report the engine writes or reads goes through
/// <see cref="Options"/>: wave/ripple payloads serialised at fan-out (collection, Marten and EF generators), the
/// same payloads deserialised for the handler (<c>RippleHandlerRegistry</c>), and the per-attempt splash reports
/// written by the pipeline and read back for export. Fan-out and worker are different processes, often different
/// deploys, so these MUST agree; this used to be six identical copies kept in sync only by comments.
/// </summary>
/// <remarks>
/// <see cref="JsonSerializerDefaults.Web"/> + <see cref="JsonStringEnumConverter"/>. Two of its properties are
/// load-bearing for the query-source providers, whose payloads are built by SQL, not by this serializer:
/// <list type="bullet">
/// <item><b>Case-insensitive property names</b> (from <c>Web</c>). The collection path writes camelCase
/// (<c>companyId</c>), but Marten's <c>jsonb_build_object</c> and EF's <c>to_jsonb</c> key the payload by the
/// projected member name (<c>CompanyId</c>). Both must bind to the same property.</item>
/// <item><b>Enums read from strings OR integers</b> (the converter's default <c>AllowIntegerValues</c>). The
/// collection path and Marten (<c>EnumStorage.AsString</c>) write names; EF maps enums to integer columns by
/// default, so <c>to_jsonb</c> emits numbers.</item>
/// </list>
/// Read-only: a consumer can serialise with it (e.g. to build <see cref="RippleSeed"/>s by hand) but can't
/// change the contract out from under the engine.
/// </remarks>
public static class RippleJson
{
    /// <summary>The shared, read-only serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
