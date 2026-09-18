using System.Globalization;
using System.Text.Json;
using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Provides the human-established invoice benchmark (expected extraction values). Reads live from
/// the SharePoint lists via Graph when configured; otherwise falls back to the seed CSVs in the
/// data folder. This is the ground-truth set the extraction pipeline is scored against.
/// </summary>
public sealed class BenchmarkStore
{
    private readonly IWebHostEnvironment _env;
    private readonly GraphBenchmarkClient _graph;
    private readonly BenchmarkGraphOptions _options;
    private readonly ILogger<BenchmarkStore> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<BenchmarkHeader> _headers = new();
    private List<BenchmarkLine> _lines = new();
    private bool _loaded;

    /// <summary>Where the loaded data came from — surfaced on the page so it's obvious which source is live.</summary>
    public string Source { get; private set; } = "not loaded";

    public BenchmarkStore(
        IWebHostEnvironment env,
        GraphBenchmarkClient graph,
        Microsoft.Extensions.Options.IOptions<BenchmarkGraphOptions> options,
        ILogger<BenchmarkStore> logger)
    {
        _env = env;
        _graph = graph;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            if (_graph.Enabled)
            {
                try
                {
                    await LoadFromGraphAsync(cancellationToken);
                    Source = "SharePoint (live)";
                    _loaded = true;
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Live benchmark read from SharePoint failed; falling back to seed CSVs.");
                }
            }

            LoadFromCsv();
            Source = _graph.Enabled ? "seed CSV (SharePoint unreachable)" : "seed CSV";
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Force a re-read on the next access (e.g. after benchmark rows change in SharePoint).</summary>
    public void Invalidate() => _loaded = false;

    public IReadOnlyList<BenchmarkHeader> Headers() => _headers;

    public IReadOnlyList<BenchmarkLine> Lines() => _lines;

    public IReadOnlyList<BenchmarkLine> LinesFor(string invoiceNumber) => _lines
        .Where(l => string.Equals(l.InvoiceNumber, invoiceNumber, StringComparison.OrdinalIgnoreCase))
        .ToList();

    // --- Graph (live) source ---

    private async Task LoadFromGraphAsync(CancellationToken cancellationToken)
    {
        var headerFields = await _graph.GetListItemFieldsAsync(_options.HeadersListId, cancellationToken);
        var lineFields = await _graph.GetListItemFieldsAsync(_options.LinesListId, cancellationToken);

        _headers = headerFields.Select(f => new BenchmarkHeader
        {
            SourceFilename = Str(f, "Title", "SourceFilename", "Source filename", "Source file"),
            DocType = Str(f, "DocType", "Doc type", "Type"),
            Supplier = Str(f, "Supplier"),
            InvoiceNumber = Str(f, "InvoiceNumber", "Invoice number", "Invoice #", "Invoice no"),
            InvoiceDate = DatePart(Str(f, "InvoiceDate", "Invoice date")),
            DueDate = DatePart(Str(f, "DueDate", "Due date")),
            Currency = Str(f, "Currency", "Ccy"),
            InvoiceTotal = Dec(f, "InvoiceTotal", "Invoice total", "Total"),
            PORef = Str(f, "PORef", "PO ref", "PO / ref", "PO / reference"),
            ValidLineCount = Int(f, "ValidLineCount", "Valid line count", "Lines"),
            ValidLineSum = Dec(f, "ValidLineSum", "Valid line sum", "Line sum"),
            Reconciles = Str(f, "Reconciles"),
            Notes = Str(f, "Notes")
        }).ToList();

        _lines = lineFields.Select(f => new BenchmarkLine
        {
            SourceFilename = Str(f, "Title", "SourceFilename", "Source filename", "Source file"),
            InvoiceNumber = Str(f, "InvoiceNumber", "Invoice number", "Invoice #", "Invoice no"),
            LineNo = Str(f, "LineNo", "Line no", "Line"),
            Description = Str(f, "Description"),
            Quantity = Dec(f, "Quantity", "Qty"),
            UnitPrice = Dec(f, "UnitPrice", "Unit price"),
            LineAmount = Str(f, "LineAmount", "Line amount", "Amount")
        }).ToList();
    }

    private static string Str(JsonElement fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(fields, name, out var el))
            {
                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString() ?? "",
                    JsonValueKind.Number => el.GetRawText(),
                    JsonValueKind.True => "Yes",
                    JsonValueKind.False => "No",
                    _ => ""
                };
            }
        }
        return "";
    }

    private static decimal? Dec(JsonElement fields, params string[] names)
    {
        JsonElement el = default;
        var found = false;
        foreach (var name in names)
        {
            if (TryGetProperty(fields, name, out el))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    private static int? Int(JsonElement fields, params string[] names)
    {
        var d = Dec(fields, names);
        return d.HasValue ? (int)d.Value : null;
    }

    private static bool TryGetProperty(JsonElement fields, string requestedName, out JsonElement value)
    {
        if (fields.TryGetProperty(requestedName, out value))
        {
            return true;
        }

        var normalisedRequested = NormaliseColumnName(requestedName);
        foreach (var property in fields.EnumerateObject())
        {
            if (NormaliseColumnName(property.Name) == normalisedRequested)
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormaliseColumnName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>SharePoint returns dates as ISO timestamps; keep just the yyyy-MM-dd part.</summary>
    private static string DatePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;
    }

    // --- CSV (seed / fallback) source ---

    private void LoadFromCsv()
    {
        var dataDir = Path.Combine(_env.ContentRootPath, "data");
        _headers = LoadHeaderCsv(Path.Combine(dataDir, "InvoiceBenchmarkHeaders.csv"));
        _lines = LoadLineCsv(Path.Combine(dataDir, "InvoiceBenchmarkLines.csv"));
    }

    private static List<BenchmarkHeader> LoadHeaderCsv(string path)
    {
        var result = new List<BenchmarkHeader>();
        foreach (var row in ReadCsv(path))
        {
            result.Add(new BenchmarkHeader
            {
                SourceFilename = Field(row, 0),
                DocType = Field(row, 1),
                Supplier = Field(row, 2),
                InvoiceNumber = Field(row, 3),
                InvoiceDate = Field(row, 4),
                DueDate = Field(row, 5),
                Currency = Field(row, 6),
                InvoiceTotal = ParseDecimal(Field(row, 7)),
                PORef = Field(row, 8),
                ValidLineCount = ParseInt(Field(row, 9)),
                ValidLineSum = ParseDecimal(Field(row, 10)),
                Reconciles = Field(row, 11),
                Notes = Field(row, 12)
            });
        }
        return result;
    }

    private static List<BenchmarkLine> LoadLineCsv(string path)
    {
        var result = new List<BenchmarkLine>();
        foreach (var row in ReadCsv(path))
        {
            result.Add(new BenchmarkLine
            {
                SourceFilename = Field(row, 0),
                InvoiceNumber = Field(row, 1),
                LineNo = Field(row, 2),
                Description = Field(row, 3),
                Quantity = ParseDecimal(Field(row, 4)),
                UnitPrice = ParseDecimal(Field(row, 5)),
                LineAmount = Field(row, 6)
            });
        }
        return result;
    }

    private static IEnumerable<string[]> ReadCsv(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        var first = true;
        foreach (var line in File.ReadLines(path))
        {
            if (first)
            {
                first = false;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            yield return ParseCsvLine(line);
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    value.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(c);
            }
        }
        fields.Add(value.ToString());
        return fields.ToArray();
    }

    private static string Field(string[] row, int index) =>
        index < row.Length ? row[index].Trim() : "";

    private static decimal? ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : null;
}
