using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>A registered type's config (batch/gap + optional retry ceiling), seeded into <c>type_schedule</c> at startup.</summary>
public readonly record struct TypeScheduleConfig(string TypeKey, int BatchSize, double GapSeconds, int? MaxAttempts);

/// <summary>
/// A ripple deserialized and ready to run: its <see cref="TargetIds"/> (known up front, so the pipeline can
/// attribute an all-failed outcome even if <see cref="Run"/> throws) and the deferred handler call.
/// </summary>
internal readonly record struct RippleInvocation(string[] TargetIds, Func<Task<SplashReport?>> Run);

/// <summary>
/// Maps a ripple's composite <c>type_key</c> — the <c>"{waveType}|{rippleType}"</c> pair — to the code that
/// deserializes both payloads and invokes the developer's <see cref="IRippleHandler{TWave,TRipple}"/>.
/// Populated at startup by <c>AddHandler</c>; the key is
/// <c>RippleTypeKey.Compose(typeof(TWave).Name, typeof(TRipple).Name)</c>, which must match the
/// <c>type_key</c> stamped on the ripple at fan-out time or the ripple has no handler. Each registration also
/// records the type's optional batch/gap for seeding into <c>type_schedule</c>.
/// </summary>
public sealed class RippleHandlerRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<string, Func<IServiceProvider, JsonDocument?, JsonDocument, IRippleContext, RippleInvocation>>
        _invokers = new();

    private readonly List<TypeScheduleConfig> _schedules = new();

    internal void Register<THandler, TWave, TRipple>(int? batchSize, double gapSeconds, int? maxAttempts)
        where THandler : class, IRippleHandler<TWave, TRipple>
        where TRipple : IRippleTarget
    {
        var key = RippleTypeKey.Compose(typeof(TWave).Name, typeof(TRipple).Name);
        _invokers[key] = (sp, wavePayload, ripplePayload, context) =>
        {
            var handler = sp.GetRequiredService<THandler>();
            var wave = wavePayload is null ? default : wavePayload.Deserialize<TWave>(JsonOptions);
            var ripple = ripplePayload.Deserialize<TRipple>(JsonOptions)!;
            // TargetIds are read now (before the handler runs) so the pipeline can attribute an all-failed
            // report if Execute throws.
            var targetIds = ripple.TargetIds?.ToArray() ?? [];
            return new RippleInvocation(targetIds, () => handler.Execute(wave!, ripple, context));
        };

        // Only types given explicit config get a type_schedule row; the rest fall back to the engine defaults
        // via COALESCE (batch/gap at fan-out, max_attempts at claim). A configured type may still leave
        // max_attempts null to keep the default retry ceiling.
        if (batchSize is { } bs)
        {
            // Fail at the AddHandler call site, not at the first fan-out. These feed the schedule_order
            // arithmetic, where e.g. batchSize 0 is a division by zero raised by every insert for the type,
            // forever, with a clean startup and nothing to point at (see TypeScheduleGuard).
            TypeScheduleGuard.Validate(bs, gapSeconds, maxAttempts);

            // Replace, don't append: _invokers already does last-wins for a re-registered pair, and appending
            // here would leave two configs with the same key — enough to make any consumer that keys by TypeKey
            // (the dashboard's /settings/types does) throw on a duplicate key.
            _schedules.RemoveAll(s => s.TypeKey == key);
            _schedules.Add(new TypeScheduleConfig(key, bs, gapSeconds, maxAttempts));
        }
        else
        {
            // Re-registered without config ⇒ the type is now unconfigured; drop any stale seed.
            _schedules.RemoveAll(s => s.TypeKey == key);
        }
    }

    internal bool TryGet(string? typeKey,
        out Func<IServiceProvider, JsonDocument?, JsonDocument, IRippleContext, RippleInvocation> invoker)
    {
        if (typeKey is not null && _invokers.TryGetValue(typeKey, out var found))
        {
            invoker = found;
            return true;
        }

        invoker = default!;
        return false;
    }

    /// <summary>The registered composite type keys (for diagnostics/startup validation).</summary>
    public IReadOnlyCollection<string> RegisteredTypes => _invokers.Keys;

    /// <summary>The per-type batch/gap configs to seed into <c>type_schedule</c> at startup.</summary>
    public IReadOnlyList<TypeScheduleConfig> Schedules => _schedules;
}
