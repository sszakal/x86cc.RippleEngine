using Microsoft.Extensions.DependencyInjection;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The extension point the optional Ripple packages hang off, implemented by the setup options passed to
/// <c>AddRippleEngine</c> — so an add-on reads as one more line inside that single configuration lambda
/// (<c>o.UseMartenFanOut()</c>) instead of a separate <c>services.Add…</c> call the caller has to remember.
/// </summary>
/// <remarks>
/// It lives here, in the lowest layer every provider already references, on purpose: it lets the Marten and
/// EF Core packages extend the setup options <b>without referencing the hosting package</b> — which would drag
/// ASP.NET Core and OpenTelemetry into them, and force each provider's dependencies onto users of the other.
/// </remarks>
public interface IRippleFeatures
{
    /// <summary>Queues a registration to run when the engine's own services are registered. Delegates run in
    /// the order they were added, after the storage services and before the engine's hosted services.</summary>
    void AddFeature(Action<IServiceCollection> register);
}
