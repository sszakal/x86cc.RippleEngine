using System.Text.Json;
using System.Text.Json.Serialization;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.MartenDb;

internal sealed class MartenWaveGenerator(RippleDataSource dataSource) : IMartenWaveGenerator
{
    // Match the options the engine deserializes wave payloads with (web casing + string enums), so a
    // payload written here round-trips cleanly through RippleHandlerRegistry on the worker.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public IWaveBuilder Create(IQuerySession session, string name)
        => new MartenWaveBuilder(session, dataSource, name, waveType: null, wavePayloadJson: null,
            Guid.NewGuid(), parentRippleId: null, continueExisting: false);

    public IWaveBuilder Create<TWave>(IQuerySession session, string name, TWave wavePayload)
        where TWave : notnull
        => new MartenWaveBuilder(session, dataSource, name, waveType: typeof(TWave).Name,
            wavePayloadJson: JsonSerializer.Serialize(wavePayload, typeof(TWave), Json),
            Guid.NewGuid(), parentRippleId: null, continueExisting: false);

    public IWaveBuilder Continue(IQuerySession session, IRippleContext context)
        => new MartenWaveBuilder(session, dataSource, name: null, waveType: null, wavePayloadJson: null,
            context.WaveId, context.RippleId, continueExisting: true);
}

public static class RippleMartenGenerationExtensions
{
    /// <summary>
    /// Registers <see cref="IMartenWaveGenerator"/>, the Marten-source fan-out generator, so a wave can be
    /// created from a Marten LINQ query (the fan-out runs as one server-side <c>INSERT … SELECT</c>). Call it
    /// inside the <c>AddRippleEngine</c> configuration lambda:
    /// <code>builder.AddRippleEngine(o => o.UseMartenFanOut());</code>
    /// </summary>
    public static IRippleFeatures UseMartenFanOut(this IRippleFeatures features)
    {
        features.AddFeature(services => services.AddSingleton<IMartenWaveGenerator, MartenWaveGenerator>());
        return features;
    }
}
