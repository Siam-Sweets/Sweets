using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public class RegisterService : IRegisterService
{
    private readonly AppDbContext _db;

    public RegisterService(AppDbContext db) => _db = db;

    public Task<CashSession?> GetOpenSessionAsync() => _db.CashSessions.AsNoTracking()
        .FirstOrDefaultAsync(session => session.ClosedAt == null);

    public async Task<IReadOnlyList<CashSession>> GetRecentSessionsAsync(int count = 30)
    {
        return await _db.CashSessions.AsNoTracking()
            .OrderByDescending(session => session.OpenedAt)
            .Take(Math.Clamp(count, 1, 200))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<CashMovement>> GetMovementsAsync(int sessionId)
    {
        if (sessionId <= 0) return Array.Empty<CashMovement>();
        return await _db.CashMovements.AsNoTracking()
            .Where(movement => movement.CashSessionId == sessionId)
            .OrderByDescending(movement => movement.CreatedAt)
            .ToListAsync();
    }

    public async Task<CashSession> OpenSessionAsync(decimal openingFloat, int userId, string? note = null)
    {
        var normalizedNote = NormalizeAndValidate(note, 500, "Register note");
        if (openingFloat < 0m)
            throw new InvalidOperationException("Opening cash cannot be negative.");
        if (userId <= 0)
            throw new InvalidOperationException("A signed-in user is required.");
        _db.ChangeTracker.Clear();
        await EnsureActiveUserAsync(userId);
        if (await _db.CashSessions.AnyAsync(session => session.ClosedAt == null))
            throw new InvalidOperationException("A register session is already open.");

        var session = new CashSession
        {
            OpenedAt = DateTime.UtcNow,
            OpenedByUserId = userId,
            OpeningFloat = openingFloat,
            Note = normalizedNote
        };
        _db.CashSessions.Add(session);
        try
        {
            await _db.SaveChangesAsync();
            return session;
        }
        catch (DbUpdateException ex)
        {
            _db.ChangeTracker.Clear();
            if (await _db.CashSessions.AsNoTracking().AnyAsync(item => item.ClosedAt == null))
                throw new InvalidOperationException("A register session is already open.", ex);
            throw;
        }
    }

    public async Task<CashMovement> AddMovementAsync(
        CashMovementType type, decimal amount, string description, int userId)
    {
        if (amount <= 0m)
            throw new InvalidOperationException("Cash amount must be greater than zero.");
        if (!Enum.IsDefined(type))
            throw new InvalidOperationException("Select a valid cash movement type.");
        if (userId <= 0)
            throw new InvalidOperationException("A signed-in user is required.");
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Enter a reason for the cash movement.");
        var normalizedDescription = NormalizeAndValidate(description, 300, "Cash movement reason")!;

        _db.ChangeTracker.Clear();
        await EnsureActiveUserAsync(userId);
        var session = await _db.CashSessions
            .FirstOrDefaultAsync(item => item.ClosedAt == null)
            ?? throw new InvalidOperationException("Open the register before adding or removing cash.");

        var movement = new CashMovement
        {
            CashSession = session,
            Type = type,
            Amount = amount,
            Description = normalizedDescription,
            UserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _db.CashMovements.Add(movement);
        await _db.SaveChangesAsync();
        return movement;
    }

    public async Task<RegisterSummary> GetSummaryAsync(int sessionId, DateTime? through = null)
    {
        if (sessionId <= 0)
            throw new InvalidOperationException("Select a valid register session.");
        var session = await _db.CashSessions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == sessionId)
            ?? throw new InvalidOperationException("Register session not found.");
        var end = through ?? session.ClosedAt ?? DateTime.UtcNow;

        var movements = await _db.CashMovements.AsNoTracking()
            .Where(item => item.CashSessionId == sessionId && item.CreatedAt <= end)
            .Select(item => new { item.Type, item.Amount })
            .ToListAsync();

        var payments = await _db.SalePayments.AsNoTracking()
            .Where(payment => payment.Sale != null &&
                              payment.Sale!.CashSessionId == sessionId &&
                              payment.Sale.SaleDate <= end &&
                              (payment.Sale.Status == SaleStatus.Completed ||
                               payment.Sale.Status == SaleStatus.Refunded))
            .Select(payment => new { payment.Method, payment.Amount })
            .ToListAsync();

        var sales = await _db.Sales.AsNoTracking()
            .Where(sale => sale.CashSessionId == sessionId && sale.SaleDate <= end &&
                           (sale.Status == SaleStatus.Completed || sale.Status == SaleStatus.Refunded))
            .Select(sale => new
            {
                sale.Status,
                sale.Subtotal,
                sale.DiscountTotal,
                sale.TaxTotal,
                sale.Rounding
            })
            .ToListAsync();

        var byPayment = payments
            .GroupBy(payment => payment.Method)
            .ToDictionary(group => group.Key, group => group.Sum(payment => payment.Amount));
        var cashIn = movements
            .Where(item => item.Type == CashMovementType.CashIn)
            .Sum(item => item.Amount);
        var cashOut = movements
            .Where(item => item.Type == CashMovementType.CashOut)
            .Sum(item => item.Amount);
        var cashSales = byPayment.GetValueOrDefault(PaymentMethod.Cash);
        var expected = session.OpeningFloat + cashSales + cashIn - cashOut;

        return new RegisterSummary
        {
            SessionId = session.Id,
            OpenedAt = session.OpenedAt,
            ClosedAt = session.ClosedAt,
            OpeningFloat = session.OpeningFloat,
            CashSales = cashSales,
            CashIn = cashIn,
            CashOut = cashOut,
            GrossSales = sales.Sum(sale =>
                sale.Subtotal - sale.DiscountTotal + sale.TaxTotal + sale.Rounding),
            // Status is authoritative. Amount signs and legacy refund links are
            // not reliable identifiers for legitimate zero-value refunds.
            TransactionCount = sales.Count(sale => sale.Status == SaleStatus.Completed),
            ExpectedCash = expected,
            CountedCash = session.CountedCash,
            Variance = session.Variance,
            ByPaymentMethod = byPayment
        };
    }

    public async Task<RegisterSummary> CloseSessionAsync(
        int sessionId, decimal countedCash, int userId, string? note = null)
    {
        var normalizedNote = NormalizeAndValidate(note, 500, "Register note");
        if (countedCash < 0m)
            throw new InvalidOperationException("Counted cash cannot be negative.");
        if (userId <= 0)
            throw new InvalidOperationException("A signed-in user is required.");
        if (sessionId <= 0)
            throw new InvalidOperationException("Select a valid register session.");

        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            await EnsureActiveUserAsync(userId);
            var session = await _db.CashSessions.FirstOrDefaultAsync(item => item.Id == sessionId)
                ?? throw new InvalidOperationException("Register session not found.");
            if (session.ClosedAt.HasValue)
                throw new InvalidOperationException("This register session is already closed.");

            var closedAt = DateTime.UtcNow;
            var summary = await GetSummaryAsync(sessionId, closedAt);
            session.ClosedAt = closedAt;
            session.ClosedByUserId = userId;
            session.ExpectedCash = summary.ExpectedCash;
            session.CountedCash = countedCash;
            session.Variance = countedCash - summary.ExpectedCash;
            if (normalizedNote != null)
                session.Note = string.IsNullOrWhiteSpace(session.Note)
                    ? normalizedNote
                    : CombineNotes(session.Note, normalizedNote);
            await _db.SaveChangesAsync();
            await _db.CommitExternalTransactionAsync(transaction);

            summary.ClosedAt = closedAt;
            summary.CountedCash = countedCash;
            summary.Variance = session.Variance;
            _db.ChangeTracker.Clear();
            return summary;
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    private static string CombineNotes(string existing, string closingNote)
    {
        const string prefix = "Close: ";
        var closeEntry = prefix + closingNote;
        if (closeEntry.Length >= 500) return closeEntry[..500];

        var combined = $"{existing}\n{closeEntry}";
        if (combined.Length <= 500) return combined;

        var available = Math.Max(0, 500 - closeEntry.Length - 1);
        var preserved = existing[..Math.Min(existing.Length, available)];
        return preserved.Length == 0 ? closeEntry : $"{preserved}\n{closeEntry}";
    }

    private static string? NormalizeAndValidate(string? value, int maxLength, string field)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maxLength)
            throw new InvalidOperationException($"{field} cannot exceed {maxLength} characters.");
        return normalized;
    }

    private async Task EnsureActiveUserAsync(int userId)
    {
        if (!await _db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive))
            throw new InvalidOperationException("The signed-in user no longer exists or is inactive. Sign in again.");
    }
}
