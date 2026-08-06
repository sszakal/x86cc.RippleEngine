using System.Text.Json;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The in-memory (source-less) <see cref="ICollectionWaveGenerator"/> — a thin adapter over
/// <see cref="IEngineStore"/>. <see cref="Create(string)"/> starts a new wave; <see cref="Continue"/> expands
/// the wave a running ripple belongs to. Both hand off to <see cref="CollectionWaveBuilder"/>, which reuses the
/// same <c>schedule_order</c> stamping (via <see cref="IEngineStore.AddRipplesAsync"/>) as every other fan-out.
/// </summary>
internal sealed class CollectionWaveGenerator(IEngineStore store) : ICollectionWaveGenerator
{
    public ICollectionWaveBuilder Create(string name)
        => new CollectionWaveBuilder(store, name, waveType: null, wavePayload: null);

    public ICollectionWaveBuilder Create<TWave>(string name, TWave wavePayload)
        where TWave : notnull
        => new CollectionWaveBuilder(store, name, typeof(TWave).Name,
            JsonSerializer.SerializeToDocument(wavePayload, typeof(TWave), RippleSeedSerializer.Options));

    public ICollectionWaveBuilder Continue(IRippleContext context)
        => new CollectionWaveBuilder(store, context.WaveId, context.RippleId);
}

/// <summary>
/// Accumulates serialized ripple seeds across <c>AddRipples</c> calls, then on <see cref="DispatchAsync"/>
/// materialises them via <see cref="IEngineStore"/> — reusing the same <c>schedule_order</c> stamping
/// (base-clamp, batch/gap) as the query-source fan-out. In <b>create</b> mode it first inserts the wave row;
/// in <b>continue</b> mode it appends to an existing wave, stamping <see cref="_parentRippleId"/> on each new
/// ripple for the audit lineage.
/// </summary>
internal sealed class CollectionWaveBuilder : ICollectionWaveBuilder
{
    private readonly IEngineStore _store;
    private readonly List<RippleSeed> _seeds = new();

    // Create-mode fields (the wave to insert); null/ignored in continue mode.
    private readonly string? _name;
    private readonly string? _waveType;
    private readonly JsonDocument? _wavePayload;

    // Continue-mode fields; _continueExisting toggles which DispatchAsync path runs.
    private readonly bool _continueExisting;
    private readonly Guid _waveId;
    private readonly Guid? _parentRippleId;

    /// <summary>Create mode: a brand-new wave is inserted on dispatch.</summary>
    public CollectionWaveBuilder(IEngineStore store, string name, string? waveType, JsonDocument? wavePayload)
    {
        _store = store;
        _name = name;
        _waveType = waveType;
        _wavePayload = wavePayload;
    }

    /// <summary>Continue mode: ripples are appended to an existing wave, parented to <paramref name="parentRippleId"/>.</summary>
    public CollectionWaveBuilder(IEngineStore store, Guid waveId, Guid parentRippleId)
    {
        _store = store;
        _continueExisting = true;
        _waveId = waveId;
        _parentRippleId = parentRippleId;
    }

    public ICollectionWaveBuilder AddRipples<TMessage>(IEnumerable<TMessage> messages)
        where TMessage : notnull
    {
        _seeds.AddRange(RippleSeedSerializer.Serialize(messages));
        return this;
    }

    public Task<Wave> DispatchAsync(CancellationToken ct = default)
        => _continueExisting ? ContinueAsync(ct) : CreateAsync(ct);

    private async Task<Wave> CreateAsync(CancellationToken ct)
    {
        try
        {
            // One atomic call: the wave row + its ripples in a single transaction (no zero-ripple window), born
            // Completed when there are no seeds. Sets RippleCount/Pending/Status on the returned wave.
            var wave = await _store.CreateWaveWithRipplesAsync(new Wave
            {
                Name = _name ?? "",
                Type = _waveType ?? "default",
                PayloadType = _waveType,
                Payload = _wavePayload
            }, _seeds, ct);

            // DETACH the payload before the finally disposes it: the store hands back the same Wave instance it
            // was given, so leaving it attached would return a document whose ArrayPool buffer is about to be
            // returned — a caller reading wave.Payload gets ObjectDisposedException, or silently corrupt JSON
            // once another thread rents that array. The caller already holds the payload it passed in, and the
            // query-source builders likewise return a wave with no Payload (WaveBuilderBase.CreateWaveAsync),
            // so null here is also what keeps the two Create paths consistent.
            wave.Payload = null;
            return wave;
        }
        finally
        {
            // SerializeToDocument rents its buffer from ArrayPool. The document is only read once, by
            // JsonDocumentTypeHandler.SetValue while the insert runs, so this builder can return it here —
            // the same discipline PreparedRipple applies on the claim path.
            _wavePayload?.Dispose();
        }
    }

    private async Task<Wave> ContinueAsync(CancellationToken ct)
    {
        await _store.AddRipplesAsync(_waveId, _seeds, parentRippleId: _parentRippleId, ct: ct);

        // The wave already exists; return its current row (its live numbers are periodically recomputed onto
        // the wave row by refresh_wave_stats(), exactly like the query-source Continue).
        return await _store.GetWaveAsync(_waveId, ct)
            ?? throw new InvalidOperationException($"Wave {_waveId} was not found");
    }
}
