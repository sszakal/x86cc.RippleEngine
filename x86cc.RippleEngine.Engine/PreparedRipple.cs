using System.Text.Json;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// A ripple claimed from the DB and ready to run: the row's execution essentials plus its wave's shared
/// payload (loaded alongside the claim). No lane/affinity — every prepared ripple flows through the same
/// bounded execute block.
/// </summary>
/// <remarks>
/// <b>Owns its two <see cref="JsonDocument"/>s and must be disposed.</b> They come from
/// <c>JsonDocumentTypeHandler.Parse</c> → <c>JsonDocument.Parse(string)</c>, which rents its backing buffer from
/// <see cref="System.Buffers.ArrayPool{T}"/>; dropping them without disposing returns nothing to the pool, so at
/// this engine's scale (200k–10M ripples, multi-KB payloads) pooling is defeated on the hottest path and every
/// claim becomes continuous allocation/GC churn. Disposal is safe as soon as execution finishes because the
/// handler registry deserializes BOTH payloads into POCOs up front — the deferred <c>Run</c> closure captures
/// those, never the documents.
/// </remarks>
internal sealed record PreparedRipple(
    Guid RippleId,
    Guid WaveId,
    int Attempt,
    int MaxAttempts,
    string TypeKey,
    string? PayloadType,
    JsonDocument Payload,
    JsonDocument? WavePayload) : IDisposable
{
    public void Dispose()
    {
        Payload.Dispose();
        WavePayload?.Dispose();
    }
}
