using System.Data;
using Microsoft.Data.SqlClient;
using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

public interface IInvoiceExtractionRepository
{
    Task<InvoiceExtractionResult> UpsertAsync(InvoiceExtractionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<(string DriveId, string DriveItemId), InvoiceExtraction>> GetBatchAsync(
        IEnumerable<(string DriveId, string DriveItemId)> keys, CancellationToken cancellationToken);
}

public sealed class InvoiceExtractionRepository : IInvoiceExtractionRepository
{
    private readonly string? _connectionString;

    public InvoiceExtractionRepository(IConfiguration configuration) =>
        _connectionString = configuration.GetConnectionString("InvoiceAutomation");

    private SqlConnection CreateConnection() => !string.IsNullOrWhiteSpace(_connectionString)
        ? new SqlConnection(_connectionString)
        : throw new InvalidOperationException("ConnectionStrings:InvoiceAutomation is not configured.");

    public async Task<InvoiceExtractionResult> UpsertAsync(InvoiceExtractionRequest request, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = """
                DECLARE @Id bigint, @Created bit = 0;
                SELECT @Id = Id FROM dbo.InvoiceExtraction WITH (UPDLOCK, HOLDLOCK)
                WHERE DriveId = @DriveId AND DriveItemId = @DriveItemId;
                IF @Id IS NULL
                BEGIN
                    INSERT dbo.InvoiceExtraction
                        (DriveId, DriveItemId, SiteId, SupplierName, InvoiceNumber, InvoiceDate, Reference, Currency, Total, Confidence, Model, ExtractedAt)
                    VALUES
                        (@DriveId, @DriveItemId, @SiteId, @SupplierName, @InvoiceNumber, @InvoiceDate, @Reference, @Currency, @Total, @Confidence, @Model, @ExtractedAt);
                    SET @Id = SCOPE_IDENTITY(); SET @Created = 1;
                END
                ELSE
                    UPDATE dbo.InvoiceExtraction SET SiteId=@SiteId, SupplierName=@SupplierName, InvoiceNumber=@InvoiceNumber,
                        InvoiceDate=@InvoiceDate, Reference=@Reference, Currency=@Currency, Total=@Total, Confidence=@Confidence,
                        Model=@Model, ExtractedAt=@ExtractedAt, UpdatedAt=SYSUTCDATETIME() WHERE Id=@Id;
                SELECT @Id, @Created;
                """;
            await using var command = new SqlCommand(headerSql, connection, transaction);
            Add(command, "@DriveId", SqlDbType.NVarChar, 200, request.DriveId.Trim());
            Add(command, "@DriveItemId", SqlDbType.NVarChar, 200, request.DriveItemId.Trim());
            Add(command, "@SiteId", SqlDbType.NVarChar, 200, Clean(request.SiteId));
            Add(command, "@SupplierName", SqlDbType.NVarChar, 300, Clean(request.SupplierName));
            Add(command, "@InvoiceNumber", SqlDbType.NVarChar, 100, Clean(request.InvoiceNumber));
            Add(command, "@InvoiceDate", SqlDbType.Date, null, request.InvoiceDate?.ToDateTime(TimeOnly.MinValue));
            Add(command, "@Reference", SqlDbType.NVarChar, 100, Clean(request.Reference));
            Add(command, "@Currency", SqlDbType.Char, 3, Clean(request.Currency)?.ToUpperInvariant());
            AddDecimal(command, "@Total", request.Total, 18, 4);
            AddDecimal(command, "@Confidence", request.Confidence, 6, 5);
            Add(command, "@Model", SqlDbType.NVarChar, 100, Clean(request.Model));
            Add(command, "@ExtractedAt", SqlDbType.DateTimeOffset, null, request.ExtractedAt ?? DateTimeOffset.UtcNow);

            long id; bool created;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                id = reader.GetInt64(0); created = reader.GetBoolean(1);
            }

            await using (var delete = new SqlCommand("DELETE dbo.InvoiceExtractionLine WHERE InvoiceExtractionId=@Id", connection, transaction))
            {
                delete.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            const string lineSql = "INSERT dbo.InvoiceExtractionLine (InvoiceExtractionId,LineNumber,Sku,Description,Quantity,UnitPrice,LineTotal) VALUES (@Id,@LineNumber,@Sku,@Description,@Quantity,@UnitPrice,@LineTotal)";
            foreach (var line in request.Lines)
            {
                await using var insert = new SqlCommand(lineSql, connection, transaction);
                insert.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
                insert.Parameters.Add("@LineNumber", SqlDbType.Int).Value = line.LineNumber;
                Add(insert, "@Sku", SqlDbType.NVarChar, 100, Clean(line.Sku));
                Add(insert, "@Description", SqlDbType.NVarChar, 1000, line.Description.Trim());
                AddDecimal(insert, "@Quantity", line.Quantity, 18, 4);
                AddDecimal(insert, "@UnitPrice", line.UnitPrice, 18, 4);
                AddDecimal(insert, "@LineTotal", line.LineTotal, 18, 4);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new InvoiceExtractionResult(id, created, request.Lines.Count);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<IReadOnlyDictionary<(string DriveId, string DriveItemId), InvoiceExtraction>> GetBatchAsync(IEnumerable<(string DriveId, string DriveItemId)> keys, CancellationToken cancellationToken)
    {
        var distinct = keys.Distinct().Take(500).ToList();
        if (distinct.Count == 0) return new Dictionary<(string, string), InvoiceExtraction>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var predicates = new List<string>();
        await using var command = connection.CreateCommand();
        for (var i = 0; i < distinct.Count; i++)
        {
            predicates.Add($"(e.DriveId=@d{i} AND e.DriveItemId=@i{i})");
            Add(command, $"@d{i}", SqlDbType.NVarChar, 200, distinct[i].DriveId);
            Add(command, $"@i{i}", SqlDbType.NVarChar, 200, distinct[i].DriveItemId);
        }
        command.CommandText = $"""
            SELECT e.Id,e.DriveId,e.DriveItemId,e.SiteId,e.SupplierName,e.InvoiceNumber,e.InvoiceDate,e.Reference,e.Currency,e.Total,e.Confidence,e.Model,e.ExtractedAt,
                   l.LineNumber,l.Sku,l.Description,l.Quantity,l.UnitPrice,l.LineTotal
            FROM dbo.InvoiceExtraction e LEFT JOIN dbo.InvoiceExtractionLine l ON l.InvoiceExtractionId=e.Id
            WHERE {string.Join(" OR ", predicates)} ORDER BY e.Id,l.LineNumber
            """;
        var builders = new Dictionary<long, (InvoiceExtraction Header, List<InvoiceExtractionLine> Lines)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            if (!builders.TryGetValue(id, out var entry))
            {
                entry = (new InvoiceExtraction { Id=id, DriveId=reader.GetString(1), DriveItemId=reader.GetString(2), SiteId=NullableString(reader,3), SupplierName=NullableString(reader,4), InvoiceNumber=NullableString(reader,5), InvoiceDate=reader.IsDBNull(6)?null:DateOnly.FromDateTime(reader.GetDateTime(6)), Reference=NullableString(reader,7), Currency=NullableString(reader,8), Total=NullableDecimal(reader,9), Confidence=NullableDecimal(reader,10), Model=NullableString(reader,11), ExtractedAt=reader.GetDateTimeOffset(12) }, []);
                builders[id] = entry;
            }
            if (!reader.IsDBNull(13)) entry.Lines.Add(new InvoiceExtractionLine { LineNumber=reader.GetInt32(13), Sku=NullableString(reader,14), Description=reader.GetString(15), Quantity=NullableDecimal(reader,16), UnitPrice=NullableDecimal(reader,17), LineTotal=NullableDecimal(reader,18) });
        }
        return builders.Values.Select(x => new InvoiceExtraction { Id=x.Header.Id, DriveId=x.Header.DriveId, DriveItemId=x.Header.DriveItemId, SiteId=x.Header.SiteId, SupplierName=x.Header.SupplierName, InvoiceNumber=x.Header.InvoiceNumber, InvoiceDate=x.Header.InvoiceDate, Reference=x.Header.Reference, Currency=x.Header.Currency, Total=x.Header.Total, Confidence=x.Header.Confidence, Model=x.Header.Model, ExtractedAt=x.Header.ExtractedAt, Lines=x.Lines }).ToDictionary(x => (x.DriveId, x.DriveItemId));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NullableString(SqlDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static decimal? NullableDecimal(SqlDataReader r,int i)=>r.IsDBNull(i)?null:r.GetDecimal(i);
    private static void Add(SqlCommand c,string name,SqlDbType type,int? size,object? value) { var p=size.HasValue?c.Parameters.Add(name,type,size.Value):c.Parameters.Add(name,type); p.Value=value??DBNull.Value; }
    private static void AddDecimal(SqlCommand c,string name,decimal? value,byte precision,byte scale) { var p=c.Parameters.Add(name,SqlDbType.Decimal); p.Precision=precision;p.Scale=scale;p.Value=value??(object)DBNull.Value; }
}
