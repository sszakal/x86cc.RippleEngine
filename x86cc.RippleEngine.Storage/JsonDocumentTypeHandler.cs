using System.Data;
using System.Text.Json;
using Dapper;
using NpgsqlTypes;
using Npgsql;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// Dapper type handler mapping a <c>jsonb</c> column to/from <see cref="JsonDocument"/> — used when
/// hydrating a claimed ripple's payload and the shared wave payload. (Writes elsewhere pass raw JSON
/// text with an explicit <c>::jsonb</c> cast, so they don't depend on this handler.)
/// </summary>
internal sealed class JsonDocumentTypeHandler : SqlMapper.TypeHandler<JsonDocument>
{
    public override JsonDocument? Parse(object value)
        => value is string s ? JsonDocument.Parse(s) : null;

    public override void SetValue(IDbDataParameter parameter, JsonDocument? value)
    {
        parameter.Value = value?.RootElement.GetRawText() ?? (object)DBNull.Value;
        if (parameter is NpgsqlParameter np)
        {
            np.NpgsqlDbType = NpgsqlDbType.Jsonb;
        }
    }
}
