using Npgsql;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// Owns the dedicated <see cref="NpgsqlDataSource"/> the engine uses for all relational access
/// (promotion, claim, completion, recovery, dashboard). A single framework-owned connection pool,
/// separate from any application data access.
/// </summary>
public sealed class RippleDataSource : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public RippleDataSource(string connectionString)
        => _dataSource = NpgsqlDataSource.Create(connectionString);

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        => _dataSource.OpenConnectionAsync(ct);

    public NpgsqlConnection OpenConnection() => _dataSource.OpenConnection();

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
