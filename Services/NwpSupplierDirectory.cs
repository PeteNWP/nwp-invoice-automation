using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

public interface INwpSupplierDirectory
{
    Task<IReadOnlyList<NwpSupplier>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<NwpSupplier?> FindByOidAsync(string oid, CancellationToken cancellationToken = default);
}

/// <summary>Cached, read-only access to the live NWP supplier master.</summary>
public sealed class NwpSupplierDirectory : INwpSupplierDirectory
{
    private const string CacheKey = "nwp-supplier-master-v1";
    private readonly string? _connectionString;
    private readonly IMemoryCache _cache;

    public NwpSupplierDirectory(IConfiguration configuration, IMemoryCache cache)
    {
        _connectionString = configuration.GetConnectionString("NwpMaster");
        _cache = cache;
    }

    public async Task<IReadOnlyList<NwpSupplier>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<NwpSupplier>? cached) && cached is not null)
            return cached;

        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("ConnectionStrings:NwpMaster is not configured.");

        // O_I_D and Reference have varied SQL types in legacy environments. Convert them
        // explicitly so callers always receive stable strings without relying on provider casts.
        const string sql = """
            SELECT CONVERT(nvarchar(100), [O_I_D]) AS [O_I_D],
                   CONVERT(nvarchar(100), [Reference]) AS [Reference],
                   [Name], [Status], [VATNumber], [Email], [PaymentCurrency]
            FROM [NWPDev].[dbo].[Supplier]
            WHERE NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(300), [Name]))), N'') IS NOT NULL
            ORDER BY [Name], CONVERT(nvarchar(100), [Reference]);
            """;

        var suppliers = new List<NwpSupplier>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var oid = Value(reader, "O_I_D");
            var name = Value(reader, "Name");
            if (string.IsNullOrWhiteSpace(oid) || string.IsNullOrWhiteSpace(name))
                continue;

            suppliers.Add(new NwpSupplier(oid.Trim(), Value(reader, "Reference")?.Trim() ?? "", name.Trim(),
                Value(reader, "Status"), Value(reader, "VATNumber"), Value(reader, "Email"), Value(reader, "PaymentCurrency")));
        }

        var result = suppliers.AsReadOnly();
        _cache.Set(CacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });
        return result;
    }

    public async Task<NwpSupplier?> FindByOidAsync(string oid, CancellationToken cancellationToken = default) =>
        (await GetAllAsync(cancellationToken)).FirstOrDefault(s => string.Equals(s.Oid, oid, StringComparison.Ordinal));

    private static string? Value(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }
}
