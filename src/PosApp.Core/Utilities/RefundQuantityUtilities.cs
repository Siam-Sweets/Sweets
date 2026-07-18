using PosApp.Core.Entities;

namespace PosApp.Core.Utilities;

/// <summary>
/// Reconstructs returned quantities for modern and legacy refund rows. Older
/// releases linked every matching refund to the first original line, so the
/// allocator also spreads overflow across duplicate product/price lines.
/// </summary>
public static class RefundQuantityUtilities
{
    private const decimal Tolerance = 0.0001m;

    public static Dictionary<int, decimal> BuildReturnedByLine(
        IEnumerable<SaleItem> originalItems,
        IEnumerable<SaleItem> refundItems)
    {
        ArgumentNullException.ThrowIfNull(originalItems);
        ArgumentNullException.ThrowIfNull(refundItems);

        var originals = originalItems.OrderBy(item => item.Id).ToList();
        var returned = originals.ToDictionary(item => item.Id, _ => 0m);

        foreach (var refundItem in refundItems)
        {
            var quantityLeft = Math.Abs(refundItem.Quantity);
            if (quantityLeft <= Tolerance) continue;

            var candidates = new List<SaleItem>();
            if (refundItem.RefundedSaleItemId is int linkedId)
            {
                var linked = originals.FirstOrDefault(item => item.Id == linkedId);
                if (linked != null) candidates.Add(linked);
            }

            candidates.AddRange(originals.Where(item =>
                candidates.All(candidate => candidate.Id != item.Id) &&
                item.ProductId == refundItem.ProductId &&
                Math.Abs(item.UnitPrice - refundItem.UnitPrice) < Tolerance));

            foreach (var candidate in candidates)
            {
                var available = Math.Max(0m, candidate.Quantity - returned[candidate.Id]);
                var allocated = Math.Min(quantityLeft, available);
                returned[candidate.Id] += allocated;
                quantityLeft -= allocated;
                if (quantityLeft <= Tolerance) break;
            }

            // Preserve evidence of malformed/over-refunded legacy data. Assigning
            // any excess to a candidate keeps every matching original line from
            // incorrectly appearing refundable again.
            if (quantityLeft > Tolerance && candidates.Count > 0)
                returned[candidates[0].Id] += quantityLeft;
        }

        return returned;
    }
}
