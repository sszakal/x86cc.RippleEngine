using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.EntityFrameworkCore;

internal sealed class EfWaveGenerator(RippleDataSource dataSource) : IEfWaveGenerator
{
    // Match the options the engine deserializes payloads with (web casing + string enums), so a payload
    // written here round-trips cleanly through the handler registry on the worker.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public IWaveBuilder Create(DbContext context, string name)
        => new EfWaveBuilder(context, dataSource, name, waveType: null, wavePayloadJson: null,
            Guid.NewGuid(), parentRippleId: null, continueExisting: false);

    public IWaveBuilder Create<TWave>(DbContext context, string name, TWave wavePayload)
        where TWave : notnull
        => new EfWaveBuilder(context, dataSource, name, typeof(TWave).Name,
            JsonSerializer.Serialize(wavePayload, typeof(TWave), Json),
            Guid.NewGuid(), parentRippleId: null, continueExisting: false);

    public IWaveBuilder Continue(DbContext context, IRippleContext rippleContext)
        => new EfWaveBuilder(context, dataSource, name: null, waveType: null, wavePayloadJson: null,
            rippleContext.WaveId, rippleContext.RippleId, continueExisting: true);
}

public static class RippleEfGenerationExtensions
{
    /// <summary>
    /// Registers <see cref="IEfWaveGenerator"/>, the EF-Core-source fan-out generator. Requires
    /// <c>AddRippleStorage</c> (it depends on the Ripple connection). The caller passes its own
    /// <see cref="DbContext"/> per fan-out call.
    /// </summary>
    public static IServiceCollection AddRippleEfGeneration(this IServiceCollection services)
    {
        services.AddSingleton<IEfWaveGenerator, EfWaveGenerator>();
        return services;
    }
}
