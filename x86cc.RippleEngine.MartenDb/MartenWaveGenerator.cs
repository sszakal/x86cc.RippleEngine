using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.MartenDb;

internal sealed class MartenWaveGenerator(RippleDataSource dataSource, TimeProvider time) : IMartenWaveGenerator
{
    public IWaveBuilder Create(IQuerySession session, string name)
        => new MartenWaveBuilder(session, dataSource, time, name, waveType: null, wavePayloadJson: null,
            Guid.NewGuid(), expansion: null);

    public IWaveBuilder Create<TWave>(IQuerySession session, string name, TWave wavePayload)
        where TWave : notnull
        => new MartenWaveBuilder(session, dataSource, time, name, waveType: RippleTypeName.Of<TWave>(),
            wavePayloadJson: JsonSerializer.Serialize(wavePayload, typeof(TWave), RippleJson.Options),
            Guid.NewGuid(), expansion: null);

    public IWaveBuilder Continue(IQuerySession session, IRippleContext context, string? expansionKey = null)
        => new MartenWaveBuilder(session, dataSource, time, name: null, waveType: null, wavePayloadJson: null,
            context.WaveId, RippleExpansion.For(context, expansionKey));
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
