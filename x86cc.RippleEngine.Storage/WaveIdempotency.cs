using Dapper;
using Npgsql;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// Makes wave creation <b>idempotent on a caller-supplied key</b> — the first step of both create paths (the
/// in-memory one in <see cref="EngineStore"/> and the <c>INSERT … SELECT</c> one in <see cref="WaveBuilderBase"/>)
/// when the builder was given <c>WithIdempotencyKey</c>.
/// </summary>
/// <remarks>
/// Why it exists. A wave is created on the engine's own connection, in its own transaction, so it cannot commit
/// together with the business change that triggers it. The caller commits its change and then creates the wave;
/// if that call times out or the process dies in between, the only safe move is to try again — and without a key
/// a retry of a create that DID land fans the whole job out a second time (every ripple twice, at 200k–10M of
/// them). With a key derived from the triggering event, retrying is always safe: the second create finds the
/// first one's wave and returns it.
/// <para>
/// The check is a transaction-scoped advisory lock on the key followed by a lookup, rather than an
/// <c>insert … on conflict</c> on the wave row, so that it can run FIRST: the query-source path inserts the wave
/// row only after its ripples (it needs their count), and a duplicate must be detected before a multi-million-row
/// fan-out, not after. The lock serialises concurrent creates of one key — the loser waits for the winner's
/// commit and then sees its wave. The unique index on <c>wave.idempotency_key</c> is the backstop.
/// </para>
/// <para>
/// A key is unique for as long as its wave row exists: once retention purges the wave, the key can be used again.
/// </para>
/// </remarks>
internal static class WaveIdempotency
{
    private const string S = M0001_Schema.SchemaName;

    // The two-int advisory-lock form, so key hashes live in their own lock space and can never meet the
    // engine's fixed single-bigint keys (stats refresh, compaction, reschedule, …). "rp" as the class id.
    private const int LockClass = 0x7270;

    /// <summary>
    /// Takes the key's lock for the rest of the transaction and returns the wave already created with it, or null
    /// if there is none and the caller should go on to create it (still holding the lock, until it commits).
    /// The returned wave carries no <see cref="Wave.Payload"/>, like the wave any create returns.
    /// </summary>
    public static async Task<Wave?> FindExistingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@lockClass, hashtext(@key))",
            new { lockClass = LockClass, key }, tx, cancellationToken: ct));

        var existing = await conn.QuerySingleOrDefaultAsync<Wave>(new CommandDefinition(
            $"{WaveSql.Select}{Environment.NewLine}where w.idempotency_key = @key",
            new { key }, tx, cancellationToken: ct));
        if (existing is not null)
        {
            // The projection reads the payload into a pooled JsonDocument; a create never hands one back.
            existing.Payload?.Dispose();
            existing.Payload = null;
        }

        return existing;
    }

    /// <summary>Validates a key given to a builder's <c>WithIdempotencyKey</c>.</summary>
    public static string Validate(string key)
        => string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("An idempotency key must be non-empty.", nameof(key))
            : key;
}
