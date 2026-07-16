using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public class CatalogTransferService : ICatalogTransferService
{
    private readonly AppDbContext _db;
    private static readonly string[] Headers =
    {
        "Name", "SKU", "Barcode", "Category", "Price", "CostPrice", "TaxRate",
        "StockQuantity", "LowStockThreshold", "Unit", "IsWeighted", "IsActive"
    };

    public CatalogTransferService(AppDbContext db) => _db = db;

    public async Task ExportProductsAsync(string filePath)
    {
        var products = await _db.Products.AsNoTracking()
            .Include(product => product.Category)
            .OrderBy(product => product.Name)
            .ToListAsync();

        await using var writer = new StreamWriter(
            filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(',', Headers));
        foreach (var product in products)
        {
            var values = new[]
            {
                product.Name,
                product.Sku ?? "",
                product.Barcode ?? "",
                product.Category?.Name ?? "Uncategorized",
                Format(product.Price),
                Format(product.CostPrice),
                Format(product.TaxRate),
                product.StockQuantity.HasValue ? Format(product.StockQuantity.Value) : "",
                product.LowStockThreshold.HasValue ? Format(product.LowStockThreshold.Value) : "",
                product.Unit.ToString(),
                product.IsWeighted.ToString(),
                product.IsActive.ToString()
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(Escape)));
        }
    }

    public async Task<CatalogImportResult> ImportProductsAsync(
        string filePath, ProductImportMode mode, int? userId = null)
    {
        var csv = await File.ReadAllTextAsync(filePath);
        var records = ParseCsv(csv);
        if (records.Count < 2)
            throw new InvalidOperationException("The CSV file has no product rows.");

        var headerMap = records[0]
            .Select((header, index) => new { Header = NormalizeHeader(header), Index = index })
            .Where(item => !string.IsNullOrEmpty(item.Header))
            .GroupBy(item => item.Header)
            .ToDictionary(group => group.Key, group => group.First().Index);
        if (!headerMap.ContainsKey("name"))
            throw new InvalidOperationException("CSV must contain a Name column.");

        var result = new CatalogImportResult();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var products = await _db.Products.Include(product => product.Category).ToListAsync();
            var categories = await _db.Categories.ToListAsync();
            var nextCategorySort = categories.Count == 0 ? 1 : categories.Max(category => category.SortOrder) + 1;

            for (var rowNumber = 1; rowNumber < records.Count; rowNumber++)
            {
                var row = records[rowNumber];
                var name = Field(row, headerMap, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    result.Warnings.Add($"Row {rowNumber + 1}: skipped because Name is empty.");
                    continue;
                }

                var sku = EmptyToNull(Field(row, headerMap, "sku"));
                var barcode = EmptyToNull(Field(row, headerMap, "barcode"));
                var rowPrice = DecimalField(row, headerMap, "price");
                var rowCost = DecimalField(row, headerMap, "costprice");
                var rowTax = DecimalField(row, headerMap, "taxrate");
                var rowStock = DecimalField(row, headerMap, "stockquantity");
                var rowThreshold = DecimalField(row, headerMap, "lowstockthreshold");
                if ((rowPrice.HasValue && rowPrice.Value < 0m) ||
                    (rowCost.HasValue && rowCost.Value < 0m) ||
                    (rowStock.HasValue && rowStock.Value < 0m) ||
                    (rowThreshold.HasValue && rowThreshold.Value < 0m))
                    throw new InvalidOperationException(
                        $"Row {rowNumber + 1}: price, cost, stock, and threshold cannot be negative.");
                if (rowTax is < 0m or > 100m)
                    throw new InvalidOperationException(
                        $"Row {rowNumber + 1}: tax rate must be between 0 and 100.");
                var categoryName = EmptyToNull(Field(row, headerMap, "category")) ?? "Uncategorized";
                var category = categories.FirstOrDefault(item =>
                    string.Equals(item.Name, categoryName, StringComparison.OrdinalIgnoreCase));
                if (category == null)
                {
                    category = new Category
                    {
                        Name = categoryName,
                        SortOrder = nextCategorySort++,
                        IsActive = true
                    };
                    categories.Add(category);
                    _db.Categories.Add(category);
                }

                var product = products.FirstOrDefault(item =>
                    (!string.IsNullOrEmpty(sku) && string.Equals(item.Sku, sku, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(barcode) && string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase)));
                product ??= products.FirstOrDefault(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                var isNew = product == null;
                if (isNew)
                {
                    product = new Product
                    {
                        Name = name,
                        Sku = sku,
                        Barcode = barcode,
                        Category = category,
                        Price = rowPrice ?? 0m,
                        CostPrice = rowCost ?? 0m,
                        TaxRate = rowTax ?? 0m,
                        LowStockThreshold = rowThreshold,
                        Unit = UnitField(row, headerMap),
                        IsWeighted = BoolField(row, headerMap, "isweighted") ?? false,
                        IsActive = BoolField(row, headerMap, "isactive") ?? true,
                        StockQuantity = rowStock,
                        CreatedAt = DateTime.UtcNow
                    };
                    products.Add(product);
                    _db.Products.Add(product);
                    result.Created++;

                    var openingStock = product.StockQuantity.GetValueOrDefault();
                    if (openingStock != 0m)
                    {
                        var type = mode == ProductImportMode.Purchase
                            ? StockTransactionType.Purchase
                            : mode == ProductImportMode.InventoryCount
                                ? StockTransactionType.Adjustment
                                : StockTransactionType.InitialStock;
                        _db.StockTransactions.Add(new StockTransaction
                        {
                            Product = product,
                            Type = type,
                            Quantity = openingStock,
                            BalanceAfter = openingStock,
                            UnitCost = product.CostPrice,
                            Note = $"CSV import ({mode})",
                            UserId = userId
                        });
                        result.StockAdjusted++;
                    }
                    continue;
                }

                var oldQuantity = product!.StockQuantity;
                var oldCost = product.CostPrice;
                product.Name = name;
                if (sku != null) product.Sku = sku;
                if (barcode != null) product.Barcode = barcode;
                product.Category = category;
                SetDecimal(row, headerMap, "price", value => product.Price = value);
                SetDecimal(row, headerMap, "taxrate", value => product.TaxRate = value);
                SetDecimal(row, headerMap, "lowstockthreshold", value => product.LowStockThreshold = value);
                if (HasValue(row, headerMap, "unit")) product.Unit = UnitField(row, headerMap);
                var weighted = BoolField(row, headerMap, "isweighted");
                if (weighted.HasValue) product.IsWeighted = weighted.Value;
                var active = BoolField(row, headerMap, "isactive");
                if (active.HasValue) product.IsActive = active.Value;

                var importedCost = rowCost;
                var importedStock = rowStock;
                decimal stockDelta = 0m;
                if (mode == ProductImportMode.CatalogOnly)
                {
                    if (importedCost.HasValue) product.CostPrice = importedCost.Value;
                }
                else if (importedStock.HasValue)
                {
                    if (!oldQuantity.HasValue)
                        throw new InvalidOperationException($"Row {rowNumber + 1}: {product.Name} is not stock-tracked.");
                    if (mode == ProductImportMode.InventoryCount)
                    {
                        stockDelta = importedStock.Value - oldQuantity.Value;
                        product.StockQuantity = importedStock.Value;
                        if (importedCost.HasValue) product.CostPrice = importedCost.Value;
                    }
                    else
                    {
                        if (importedStock.Value < 0m)
                            throw new InvalidOperationException($"Row {rowNumber + 1}: purchase quantity cannot be negative.");
                        stockDelta = importedStock.Value;
                        var newQuantity = oldQuantity.Value + stockDelta;
                        var incomingCost = importedCost ?? oldCost;
                        if (newQuantity > 0m)
                            product.CostPrice =
                                (Math.Max(0m, oldQuantity.Value) * oldCost + stockDelta * incomingCost) / newQuantity;
                        product.StockQuantity = newQuantity;
                    }

                    if (stockDelta != 0m)
                    {
                        _db.StockTransactions.Add(new StockTransaction
                        {
                            Product = product,
                            Type = mode == ProductImportMode.Purchase
                                ? StockTransactionType.Purchase
                                : StockTransactionType.Adjustment,
                            Quantity = stockDelta,
                            BalanceAfter = product.StockQuantity!.Value,
                            UnitCost = importedCost,
                            Note = $"CSV import ({mode})",
                            UserId = userId
                        });
                        result.StockAdjusted++;
                    }
                }
                else if (mode == ProductImportMode.InventoryCount && importedCost.HasValue)
                {
                    product.CostPrice = importedCost.Value;
                }
                product.UpdatedAt = DateTime.UtcNow;
                result.Updated++;
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string NormalizeHeader(string value) => new(
        value.Trim().TrimStart('\uFEFF').Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());

    private static string? Field(
        IReadOnlyList<string> row, IReadOnlyDictionary<string, int> headers, string name)
    {
        return headers.TryGetValue(NormalizeHeader(name), out var index) && index < row.Count
            ? row[index]
            : null;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasValue(
        IReadOnlyList<string> row, IReadOnlyDictionary<string, int> headers, string name) =>
        !string.IsNullOrWhiteSpace(Field(row, headers, name));

    private static decimal? DecimalField(
        IReadOnlyList<string> row, IReadOnlyDictionary<string, int> headers, string name)
    {
        var raw = Field(row, headers, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariant))
            return invariant;
        if (decimal.TryParse(raw, out var local)) return local;
        throw new InvalidOperationException($"'{raw}' is not a valid number for {name}.");
    }

    private static void SetDecimal(
        IReadOnlyList<string> row, IReadOnlyDictionary<string, int> headers,
        string name, Action<decimal> setter)
    {
        var value = DecimalField(row, headers, name);
        if (value.HasValue) setter(value.Value);
    }

    private static bool? BoolField(
        IReadOnlyList<string> row, IReadOnlyDictionary<string, int> headers, string name)
    {
        var raw = Field(row, headers, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (bool.TryParse(raw, out var value)) return value;
        if (raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw == "0" || raw.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException($"'{raw}' is not a valid true/false value for {name}.");
    }

    private static UnitOfMeasure UnitField(
        IReadOnlyList<string> row, IReadOnlyDictionary<string, int> headers)
    {
        var raw = Field(row, headers, "unit");
        return Enum.TryParse<UnitOfMeasure>(raw, ignoreCase: true, out var unit)
            ? unit
            : UnitOfMeasure.Piece;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            if (ch == '"') quoted = true;
            else if (ch == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                if (record.Any(value => !string.IsNullOrWhiteSpace(value))) records.Add(record);
                record = new List<string>();
            }
            else if (ch != '\r') field.Append(ch);
        }
        if (quoted) throw new InvalidOperationException("CSV contains an unclosed quoted field.");
        record.Add(field.ToString());
        if (record.Any(value => !string.IsNullOrWhiteSpace(value))) records.Add(record);
        return records;
    }
}
