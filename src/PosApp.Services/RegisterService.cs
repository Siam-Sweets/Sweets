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
        return await _db.CashMovements.AsNoTracking()
            .Where(movement => movement.CashSessionId == sessionId)
            .OrderByDescending(movement => movement.CreatedAt)
            .ToListAsync();
    }

    public async Task<CashSession> OpenSessionAsync(decimal openingFloat, int userId, string? note = null)
    {
        if (openingFloat < 0m)
            throw new InvalidOperationException("Opening cash cannot be negative.");
        if (await _db.CashSessions.AnyAsync(session => session.ClosedAt == null))
            throw new InvalidOperationException("A register session is already open.");

        var session = new CashSession
        {
            OpenedAt = DateTime.UtcNow,
            OpenedByUserId = userId,
            OpeningFloat = openingFloat,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
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
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Enter a reason for the cash movement.");

        var session = await _db.CashSessions
            .FirstOrDefaultAsync(item => item.ClosedAt == null)
            ?? throw new InvalidOperationException("Open the register before adding or removing cash.");

        var movement = new CashMovement
        {
            CashSession = session,
            Type = type,
            Amount = amount,
            Description = description.Trim(),
            UserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _db.CashMovements.Add(movement);
        await _db.SaveChangesAsync();
        return movement;
    }

    public async Task<RegisterSummary> GetSummaryAsync(int sessionId, DateTime? through = null)
    {
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
                              payment.Sale.SaleDate >= session.OpenedAt &&
                              payment.Sale.SaleDate <= end &&
                              (payment.Sale.Status == SaleStatus.Completed ||
                               payment.Sale.Status == SaleStatus.Refunded))
            .Select(payment => new { payment.Method, payment.Amount })
            .ToListAsync();

        var sales = await _db.Sales.AsNoTracking()
            .Where(sale => sale.SaleDate >= session.OpenedAt && sale.SaleDate <= end &&
                           (sale.Status == SaleStatus.Completed || sale.Status == SaleStatus.Refunded))
            .Select(sale => new
            {
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
            TransactionCount = sales.Count(sale => sale.Subtotal >= 0m),
            ExpectedCash = expected,
            CountedCash = session.CountedCash,
            Variance = session.Variance,
            ByPaymentMethod = byPayment
        };
    }

    public async Task<RegisterSummary> CloseSessionAsync(
        int sessionId, decimal countedCash, int userId, string? note = null)
    {
        if (countedCash < 0m)
            throw new InvalidOperationException("Counted cash cannot be negative.");

        var session = await _db.CashSessions.FindAsync(sessionId)
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
        if (!string.IsNullOrWhiteSpace(note))
            session.Note = string.IsNullOrWhiteSpace(session.Note)
                ? note.Trim()
                : $"{session.Note}\nClose: {note.Trim()}";
        await _db.SaveChangesAsync();

        summary.ClosedAt = closedAt;
        summary.CountedCash = countedCash;
        summary.Variance = session.Variance;
        return summary;
    }
}
