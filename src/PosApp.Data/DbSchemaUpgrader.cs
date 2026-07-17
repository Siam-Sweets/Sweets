using System.Data;
using Microsoft.EntityFrameworkCore;

namespace PosApp.Data;

/// <summary>
/// Idempotent schema additions for installations created before EF models
/// included operational tables. Existing databases are upgraded in place.
/// </summary>
public static class DbSchemaUpgrader
{
    public static async Task ApplyAsync(AppDbContext db)
    {
        var commands = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "Suppliers" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Suppliers" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Phone" TEXT NULL,
                "Email" TEXT NULL,
                "Address" TEXT NULL,
                "TaxId" TEXT NULL,
                "Notes" TEXT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PurchaseDocuments" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PurchaseDocuments" PRIMARY KEY AUTOINCREMENT,
                "DocumentNumber" TEXT NOT NULL,
                "ExternalReference" TEXT NULL,
                "SupplierId" INTEGER NULL,
                "UserId" INTEGER NOT NULL,
                "DocumentDate" TEXT NOT NULL,
                "StockDate" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "Subtotal" decimal(18,4) NOT NULL,
                "TaxTotal" decimal(18,4) NOT NULL,
                "Total" decimal(18,4) NOT NULL,
                "Note" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NULL,
                CONSTRAINT "FK_PurchaseDocuments_Suppliers_SupplierId" FOREIGN KEY ("SupplierId") REFERENCES "Suppliers" ("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_PurchaseDocuments_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PurchaseItems" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PurchaseItems" PRIMARY KEY AUTOINCREMENT,
                "PurchaseDocumentId" INTEGER NOT NULL,
                "ProductId" INTEGER NOT NULL,
                "ProductName" TEXT NOT NULL,
                "Sku" TEXT NULL,
                "Quantity" decimal(18,4) NOT NULL,
                "UnitCost" decimal(18,4) NOT NULL,
                "TaxRate" decimal(6,3) NOT NULL,
                CONSTRAINT "FK_PurchaseItems_PurchaseDocuments_PurchaseDocumentId" FOREIGN KEY ("PurchaseDocumentId") REFERENCES "PurchaseDocuments" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PurchaseItems_Products_ProductId" FOREIGN KEY ("ProductId") REFERENCES "Products" ("Id") ON DELETE RESTRICT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CashSessions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CashSessions" PRIMARY KEY AUTOINCREMENT,
                "OpenedAt" TEXT NOT NULL,
                "OpenedByUserId" INTEGER NOT NULL,
                "OpeningFloat" decimal(18,4) NOT NULL,
                "ClosedAt" TEXT NULL,
                "ClosedByUserId" INTEGER NULL,
                "ExpectedCash" decimal(18,4) NULL,
                "CountedCash" decimal(18,4) NULL,
                "Variance" decimal(18,4) NULL,
                "Note" TEXT NULL,
                CONSTRAINT "FK_CashSessions_Users_OpenedByUserId" FOREIGN KEY ("OpenedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_CashSessions_Users_ClosedByUserId" FOREIGN KEY ("ClosedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CashMovements" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CashMovements" PRIMARY KEY AUTOINCREMENT,
                "CashSessionId" INTEGER NOT NULL,
                "Type" INTEGER NOT NULL,
                "Amount" decimal(18,4) NOT NULL,
                "Description" TEXT NOT NULL,
                "UserId" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_CashMovements_CashSessions_CashSessionId" FOREIGN KEY ("CashSessionId") REFERENCES "CashSessions" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CashMovements_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SalePayments" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SalePayments" PRIMARY KEY AUTOINCREMENT,
                "SaleId" INTEGER NOT NULL,
                "Method" INTEGER NOT NULL,
                "Amount" decimal(18,4) NOT NULL,
                "Reference" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_SalePayments_Sales_SaleId" FOREIGN KEY ("SaleId") REFERENCES "Sales" ("Id") ON DELETE CASCADE
            );
            """,
            "CREATE INDEX IF NOT EXISTS \"IX_Suppliers_Name\" ON \"Suppliers\" (\"Name\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PurchaseDocuments_DocumentNumber\" ON \"PurchaseDocuments\" (\"DocumentNumber\");",
            "CREATE INDEX IF NOT EXISTS \"IX_PurchaseDocuments_DocumentDate\" ON \"PurchaseDocuments\" (\"DocumentDate\");",
            "CREATE INDEX IF NOT EXISTS \"IX_PurchaseDocuments_SupplierId\" ON \"PurchaseDocuments\" (\"SupplierId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_PurchaseItems_PurchaseDocumentId\" ON \"PurchaseItems\" (\"PurchaseDocumentId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_PurchaseItems_ProductId\" ON \"PurchaseItems\" (\"ProductId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_CashSessions_OpenedAt\" ON \"CashSessions\" (\"OpenedAt\");",
            "CREATE INDEX IF NOT EXISTS \"IX_CashSessions_OpenedByUserId\" ON \"CashSessions\" (\"OpenedByUserId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_CashSessions_ClosedByUserId\" ON \"CashSessions\" (\"ClosedByUserId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_CashMovements_CashSessionId\" ON \"CashMovements\" (\"CashSessionId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_CashMovements_CreatedAt\" ON \"CashMovements\" (\"CreatedAt\");",
            "CREATE INDEX IF NOT EXISTS \"IX_CashMovements_UserId\" ON \"CashMovements\" (\"UserId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_SalePayments_SaleId\" ON \"SalePayments\" (\"SaleId\");"
        };

        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command);

        // EnsureCreated only creates a brand-new database; it does not add later
        // model columns to an existing store. These upgrades keep installed data
        // intact while bringing older databases up to the current checkout schema.
        await EnsureColumnAsync(db, "Sales", "Change",
            "\"Change\" decimal(18,4) NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "SaleItems", "DiscountReason",
            "\"DiscountReason\" TEXT NULL");
        await EnsureColumnAsync(db, "StockTransactions", "SaleItemId",
            "\"SaleItemId\" INTEGER NULL");
    }

    private static async Task EnsureColumnAsync(
        AppDbContext db,
        string tableName,
        string columnName,
        string columnDeclaration)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();

        try
        {
            var quotedTable = tableName.Replace("\"", "\"\"");
            var exists = false;

            await using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info(\"{quotedTable}\");";
                await using var reader = await info.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (exists) return;

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{quotedTable}\" ADD COLUMN {columnDeclaration};";
            await alter.ExecuteNonQueryAsync();
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }
}
