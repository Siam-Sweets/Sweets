using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace PosApp.Data;

/// <summary>
/// Idempotent schema additions for installations created before EF models
/// included operational tables. Existing databases are upgraded in place.
/// </summary>
public static class DbSchemaUpgrader
{
    public static async Task ApplyAsync(AppDbContext db)
    {
        // SQLite supports transactional DDL. Keep the complete idempotent upgrade
        // in one transaction so a disk error, power loss, or bad legacy row can
        // never leave the installed database halfway between two schemas.
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
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
            "CREATE INDEX IF NOT EXISTS \"IX_PurchaseDocuments_DocumentNumber\" ON \"PurchaseDocuments\" (\"DocumentNumber\");",
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

        await ApplyCloudSyncSchemaAsync(db);
        await EnsureColumnAsync(db, "SyncOutboxOperations", "CreatedByUserId",
            "\"CreatedByUserId\" TEXT NOT NULL DEFAULT ''");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_SyncOutbox_UserStatusRetry\" ON \"SyncOutboxOperations\" (\"CreatedByUserId\", \"Status\", \"NextAttemptAtUtc\", \"Id\")");
        await EnsureColumnAsync(db, "CloudAccountStates", "ReconciliationBackupPath",
            "\"ReconciliationBackupPath\" TEXT NULL");
        await EnsureColumnAsync(db, "CloudAccountStates", "ActiveMigrationId",
            "\"ActiveMigrationId\" TEXT NULL");
        await EnsureColumnAsync(db, "CloudAccountStates", "ActiveMigrationStoreId",
            "\"ActiveMigrationStoreId\" TEXT NULL");
        await EnsureColumnAsync(db, "CloudAccountStates", "ActiveMigrationBackupPath",
            "\"ActiveMigrationBackupPath\" TEXT NULL");
        await EnsureColumnAsync(db, "CloudAccountStates", "IsMigrationSnapshotQueued",
            "\"IsMigrationSnapshotQueued\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "CloudCachedDeviceSessions", "StoreId", "\"StoreId\" TEXT NULL");
        await EnsureColumnAsync(db, "CloudCachedDeviceSessions", "StoreName", "\"StoreName\" TEXT NULL");
        await EnsureColumnAsync(db, "SyncConflicts", "ServerStoreId", "\"ServerStoreId\" TEXT NULL");
        await EnsureColumnAsync(db, "SyncConflicts", "ServerUpdatedAtUtc", "\"ServerUpdatedAtUtc\" TEXT NULL");
        await EnsureColumnAsync(db, "SyncConflicts", "ServerDeletedAtUtc", "\"ServerDeletedAtUtc\" TEXT NULL");
        await EnsureColumnAsync(db, "SyncConflicts", "ServerLastModifiedDeviceId",
            "\"ServerLastModifiedDeviceId\" TEXT NULL");

        // EnsureCreated only creates a brand-new database; it does not add later
        // model columns to an existing store. These upgrades keep installed data
        // intact while bringing older databases up to the current checkout schema.
        await EnsureColumnAsync(db, "Sales", "Change",
            "\"Change\" decimal(18,4) NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "Sales", "RefundedSaleId",
            "\"RefundedSaleId\" INTEGER NULL");
        var productUnitColumnAdded = await EnsureColumnAsync(db, "Products", "Unit",
            "\"Unit\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "Products", "AllowDiscount",
            "\"AllowDiscount\" INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(db, "SaleItems", "DiscountReason",
            "\"DiscountReason\" TEXT NULL");
        await EnsureColumnAsync(db, "StockTransactions", "SaleItemId",
            "\"SaleItemId\" INTEGER NULL");
        await EnsureColumnAsync(db, "StockTransactions", "PurchaseDocumentId",
            "\"PurchaseDocumentId\" INTEGER NULL");
        await EnsureColumnAsync(db, "StockTransactions", "PurchaseItemId",
            "\"PurchaseItemId\" INTEGER NULL");
        var costPriceColumnAdded = await EnsureColumnAsync(db, "SaleItems", "CostPrice",
            "\"CostPrice\" decimal(18,4) NOT NULL DEFAULT 0");
        var unitColumnAdded = await EnsureColumnAsync(db, "SaleItems", "Unit",
            "\"Unit\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "SaleItems", "PromotionId",
            "\"PromotionId\" INTEGER NULL");
        await EnsureColumnAsync(db, "SaleItems", "IsRefunded",
            "\"IsRefunded\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "SaleItems", "RefundedSaleItemId",
            "\"RefundedSaleItemId\" INTEGER NULL");
        await EnsureColumnAsync(db, "Sales", "ServiceType",
            "\"ServiceType\" TEXT NOT NULL DEFAULT 'Retail'");
        await EnsureColumnAsync(db, "Sales", "CashSessionId",
            "\"CashSessionId\" INTEGER NULL REFERENCES \"CashSessions\"(\"Id\") ON DELETE RESTRICT");
        await EnsureColumnAsync(db, "Customers", "IsActive",
            "\"IsActive\" INTEGER NOT NULL DEFAULT 1");

        // Releases that stored only IsWeighted implicitly meant kilograms. Keep
        // that behaviour when adding the explicit unit column to those databases.
        if (productUnitColumnAdded)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Products SET Unit = 1 WHERE IsWeighted = 1;");
        }

        // Existing sale rows predate cost snapshots. Backfill once from the current
        // catalog so later product-cost edits no longer change those reports.
        if (costPriceColumnAdded)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE SaleItems SET CostPrice = COALESCE((SELECT CostPrice FROM Products WHERE Products.Id = SaleItems.ProductId), 0);");
        }

        // Sale-item units are immutable receipt facts. When upgrading a database
        // created before unit snapshots existed, copy the product's current unit
        // once so historical weighted/volume lines do not all appear as pieces.
        if (unitColumnAdded)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE SaleItems SET Unit = COALESCE((SELECT Unit FROM Products WHERE Products.Id = SaleItems.ProductId), 0);");
        }

        // Repair legacy global invariants before relaxing catalog indexes for
        // branch-scoped data.
        await ResolveLegacyDuplicatesAsync(db);

        // Older versions only linked a reversal to the original sale. Normalize
        // those legacy documents first, then link each negative line to the best
        // matching original line. On later starts, the link also identifies modern
        // partial refunds so legacy repair never overwrites their tender or rounding.
        // Run this repair on every start, not only when the column is first
        // introduced. That also heals a database copied from an interrupted or
        // experimental build where the column existed but legacy links did not.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "SaleItems" AS refundItem
            SET "RefundedSaleItemId" = (
                SELECT originalItem."Id"
                FROM "Sales" AS refundSale
                JOIN "SaleItems" AS originalItem
                  ON originalItem."SaleId" = refundSale."RefundedSaleId"
                WHERE refundSale."Id" = refundItem."SaleId"
                  AND originalItem."ProductId" = refundItem."ProductId"
                  AND ABS(originalItem."UnitPrice" - refundItem."UnitPrice") < 0.0001
                ORDER BY originalItem."Id"
                LIMIT 1
            )
            WHERE refundItem."RefundedSaleItemId" IS NULL
              AND EXISTS (
                SELECT 1 FROM "Sales" AS refundSale
                WHERE refundSale."Id" = refundItem."SaleId"
                  AND refundSale."Status" = 3
                  AND refundSale."RefundedSaleId" IS NOT NULL
              );
            """);

        // Legacy releases inferred register ownership from timestamps. Persist
        // that ownership once so later refunds cannot alter a closed session.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "Sales"
            SET "CashSessionId" = (
                SELECT session."Id"
                FROM "CashSessions" AS session
                WHERE session."OpenedAt" <= "Sales"."SaleDate"
                  AND (session."ClosedAt" IS NULL OR "Sales"."SaleDate" <= session."ClosedAt")
                ORDER BY session."OpenedAt" DESC, session."Id" DESC
                LIMIT 1
            )
            WHERE "CashSessionId" IS NULL
              AND EXISTS (
                SELECT 1
                FROM "CashSessions" AS session
                WHERE session."OpenedAt" <= "Sales"."SaleDate"
                  AND (session."ClosedAt" IS NULL OR "Sales"."SaleDate" <= session."ClosedAt")
              );
            """);

        await CreateNoCaseUniqueIndexIfSafeAsync(db, "UX_Users_Username_NOCASE", "Users", "Username");
        await RelaxStoreScopedUniqueIndexesAsync(db);
        // Partial refunds legitimately create several reversal documents for one
        // sale. Remove the old one-refund-only constraint before publishing the
        // normal lookup indexes.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"UX_Sales_RefundedSaleId\";");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Sales_RefundedSaleId\";");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_Sales_RefundedSaleId\" ON \"Sales\" (\"RefundedSaleId\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_SaleItems_RefundedSaleItemId\" ON \"SaleItems\" (\"RefundedSaleItemId\");");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"UX_CashSessions_OneOpen\";");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_Sales_CashSessionId\" ON \"Sales\" (\"CashSessionId\");");

        // Purchase stock rows created by releases before cloud sync recorded a
        // stable document number in Note. Recover document and line ownership in
        // deterministic insertion order so their first upload is source-checked.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "StockTransactions" AS movement
            SET "PurchaseDocumentId" = (
                SELECT document."Id"
                FROM "PurchaseDocuments" AS document
                WHERE movement."Note" LIKE 'Purchase ' || document."DocumentNumber" || '%'
                ORDER BY document."Id"
                LIMIT 1
            )
            WHERE movement."Type" = 2 AND movement."PurchaseDocumentId" IS NULL;
            """);
        await db.Database.ExecuteSqlRawAsync(
            """
            WITH movement_rank AS (
                SELECT movement."Id", movement."PurchaseDocumentId", movement."ProductId",
                       movement."Quantity",
                       ROW_NUMBER() OVER (
                           PARTITION BY movement."PurchaseDocumentId", movement."ProductId"
                           ORDER BY movement."Id") AS line_rank
                FROM "StockTransactions" AS movement
                WHERE movement."Type" = 2 AND movement."PurchaseDocumentId" IS NOT NULL
            ), item_rank AS (
                SELECT item."Id", item."PurchaseDocumentId", item."ProductId", item."Quantity",
                       ROW_NUMBER() OVER (
                           PARTITION BY item."PurchaseDocumentId", item."ProductId"
                           ORDER BY item."Id") AS line_rank
                FROM "PurchaseItems" AS item
            )
            UPDATE "StockTransactions"
            SET "PurchaseItemId" = (
                SELECT item_rank."Id"
                FROM movement_rank
                JOIN item_rank
                  ON item_rank."PurchaseDocumentId" = movement_rank."PurchaseDocumentId"
                 AND item_rank."ProductId" = movement_rank."ProductId"
                 AND item_rank.line_rank = movement_rank.line_rank
                 AND ABS(item_rank."Quantity" - movement_rank."Quantity") < 0.0001
                WHERE movement_rank."Id" = "StockTransactions"."Id"
            )
            WHERE "Type" = 2 AND "PurchaseItemId" IS NULL;
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_StockTransactions_PurchaseDocumentId\" ON \"StockTransactions\" (\"PurchaseDocumentId\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_StockTransactions_PurchaseItemId\" ON \"StockTransactions\" (\"PurchaseItemId\") WHERE \"PurchaseItemId\" IS NOT NULL;");

        await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task ApplyCloudSyncSchemaAsync(AppDbContext db)
    {
        // v3 keeps all sync state beside the operational SQLite database. No
        // secret token is stored in these tables; tokens live in DPAPI storage.
        var commands = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "CloudAccountStates" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CloudAccountStates" PRIMARY KEY,
                "ApiBaseUrl" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "TenantName" TEXT NOT NULL,
                "CurrentStoreId" TEXT NOT NULL,
                "CurrentStoreName" TEXT NOT NULL,
                "CurrentCloudUserId" TEXT NOT NULL,
                "DeviceId" TEXT NOT NULL,
                "DeviceName" TEXT NOT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "IsDeviceRevoked" INTEGER NOT NULL,
                "RequiresReconciliation" INTEGER NOT NULL,
                "ReconciliationBackupPath" TEXT NULL,
                "ActiveMigrationId" TEXT NULL,
                "ActiveMigrationStoreId" TEXT NULL,
                "ActiveMigrationBackupPath" TEXT NULL,
                "IsMigrationSnapshotQueued" INTEGER NOT NULL,
                "LastServerCursor" INTEGER NOT NULL,
                "LastSuccessfulSyncAtUtc" TEXT NULL,
                "LastLoginAtUtc" TEXT NULL,
                "ServerApiVersion" INTEGER NOT NULL,
                "ServerSchemaVersion" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "CK_CloudAccountState_SingleRow" CHECK ("Id" = 1)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncIdentities" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncIdentities" PRIMARY KEY AUTOINCREMENT,
                "EntityType" TEXT NOT NULL,
                "LocalId" INTEGER NOT NULL,
                "RecordId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "StoreId" TEXT NULL,
                "ServerVersion" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "DeletedAtUtc" TEXT NULL,
                "LastModifiedDeviceId" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncOutboxOperations" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncOutboxOperations" PRIMARY KEY AUTOINCREMENT,
                "OperationId" TEXT NOT NULL,
                "IdempotencyKey" TEXT NOT NULL,
                "EntityType" TEXT NOT NULL,
                "RecordId" TEXT NOT NULL,
                "LocalId" INTEGER NOT NULL,
                "StoreId" TEXT NULL,
                "CreatedByUserId" TEXT NOT NULL,
                "Operation" INTEGER NOT NULL,
                "BaseVersion" INTEGER NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "AttemptCount" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "LastAttemptAtUtc" TEXT NULL,
                "NextAttemptAtUtc" TEXT NULL,
                "LastErrorCode" TEXT NULL,
                "LastErrorMessage" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncCursorStates" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncCursorStates" PRIMARY KEY AUTOINCREMENT,
                "TenantId" TEXT NOT NULL,
                "StoreId" TEXT NOT NULL,
                "DeviceId" TEXT NOT NULL,
                "Cursor" INTEGER NOT NULL,
                "LastPullAtUtc" TEXT NULL,
                "LastPushAtUtc" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncConflicts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncConflicts" PRIMARY KEY AUTOINCREMENT,
                "ConflictId" TEXT NOT NULL,
                "EntityType" TEXT NOT NULL,
                "RecordId" TEXT NOT NULL,
                "OperationId" TEXT NULL,
                "LocalBaseVersion" INTEGER NOT NULL,
                "ServerVersion" INTEGER NOT NULL,
                "LocalPayloadJson" TEXT NOT NULL,
                "ServerPayloadJson" TEXT NOT NULL,
                "ServerStoreId" TEXT NULL,
                "ServerUpdatedAtUtc" TEXT NULL,
                "ServerDeletedAtUtc" TEXT NULL,
                "ServerLastModifiedDeviceId" TEXT NULL,
                "Status" INTEGER NOT NULL,
                "DetectedAtUtc" TEXT NOT NULL,
                "ResolvedAtUtc" TEXT NULL,
                "ResolvedByUserId" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CloudCachedStores" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CloudCachedStores" PRIMARY KEY AUTOINCREMENT,
                "CloudStoreId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Code" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "CloudCachedDeviceSessions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CloudCachedDeviceSessions" PRIMARY KEY AUTOINCREMENT,
                "CloudSessionId" TEXT NOT NULL,
                "DeviceId" TEXT NOT NULL,
                "DeviceName" TEXT NOT NULL,
                "StoreId" TEXT NULL,
                "StoreName" TEXT NULL,
                "OperatingSystem" TEXT NOT NULL,
                "FirstRegisteredAtUtc" TEXT NOT NULL,
                "LastLoginAtUtc" TEXT NULL,
                "LastSyncAtUtc" TEXT NULL,
                "ExpiresAtUtc" TEXT NULL,
                "IsCurrent" INTEGER NOT NULL,
                "IsRevoked" INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "Expenses" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Expenses" PRIMARY KEY AUTOINCREMENT,
                "Category" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "Amount" decimal(18,4) NOT NULL,
                "ExpenseDate" TEXT NOT NULL,
                "UserId" INTEGER NOT NULL,
                "IsVoided" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_Expenses_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """,
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_SyncIdentities_Entity_Local\" ON \"SyncIdentities\" (\"EntityType\", \"LocalId\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_SyncIdentities_RecordId\" ON \"SyncIdentities\" (\"RecordId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_SyncIdentities_Scope\" ON \"SyncIdentities\" (\"TenantId\", \"StoreId\", \"EntityType\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_SyncOutbox_OperationId\" ON \"SyncOutboxOperations\" (\"OperationId\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_SyncOutbox_IdempotencyKey\" ON \"SyncOutboxOperations\" (\"IdempotencyKey\");",
            "CREATE INDEX IF NOT EXISTS \"IX_SyncOutbox_StatusRetry\" ON \"SyncOutboxOperations\" (\"Status\", \"NextAttemptAtUtc\", \"Id\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_SyncCursor_ScopeDevice\" ON \"SyncCursorStates\" (\"TenantId\", \"StoreId\", \"DeviceId\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_SyncConflicts_ConflictId\" ON \"SyncConflicts\" (\"ConflictId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_SyncConflicts_Status\" ON \"SyncConflicts\" (\"Status\", \"DetectedAtUtc\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_CloudCachedStores_CloudStoreId\" ON \"CloudCachedStores\" (\"CloudStoreId\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_CloudCachedDeviceSessions_SessionId\" ON \"CloudCachedDeviceSessions\" (\"CloudSessionId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_Expenses_ExpenseDate\" ON \"Expenses\" (\"ExpenseDate\");",
            "CREATE INDEX IF NOT EXISTS \"IX_Expenses_UserId\" ON \"Expenses\" (\"UserId\");"
        };

        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command);
    }


    private static async Task ResolveLegacyDuplicatesAsync(AppDbContext db)
    {
        var commands = new[]
        {
            // Usernames cannot be empty; retain the original text and append a stable suffix.
            """
            WITH ranked AS (
                SELECT "Id", ROW_NUMBER() OVER (
                    PARTITION BY LOWER(TRIM("Username")) ORDER BY "Id") AS rn
                FROM "Users" WHERE TRIM("Username") <> ''
            )
            UPDATE "Users"
            SET "Username" = "Username" || '-dup-' || "Id"
            WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > 1);
            """,
            // If an older race created multiple open sessions, retain the newest one
            // per synchronized branch. Unlinked legacy rows share one bootstrap scope.
            // This must not close a valid register that happens to be open in another
            // branch cached in the same v2 SQLite working copy.
            """
            UPDATE "CashSessions" AS current
            SET "ClosedAt" = COALESCE("ClosedAt", CURRENT_TIMESTAMP),
                "ExpectedCash" = COALESCE("ExpectedCash", "OpeningFloat"),
                "CountedCash" = COALESCE("CountedCash", "OpeningFloat"),
                "Variance" = COALESCE("Variance", 0),
                "Note" = CASE WHEN "Note" IS NULL OR TRIM("Note") = ''
                    THEN 'Automatically closed during database upgrade'
                    ELSE "Note" || CHAR(10) || 'Automatically closed during database upgrade' END
            WHERE current."ClosedAt" IS NULL
              AND EXISTS (
                  SELECT 1
                  FROM "CashSessions" AS newer
                  WHERE newer."ClosedAt" IS NULL
                    AND (
                        newer."OpenedAt" > current."OpenedAt"
                        OR (newer."OpenedAt" = current."OpenedAt" AND newer."Id" > current."Id")
                    )
                    AND COALESCE(
                        (SELECT identity."StoreId" FROM "SyncIdentities" AS identity
                         WHERE identity."EntityType" = 'register_sessions'
                           AND identity."LocalId" = newer."Id" LIMIT 1),
                        '__legacy__'
                    ) = COALESCE(
                        (SELECT identity."StoreId" FROM "SyncIdentities" AS identity
                         WHERE identity."EntityType" = 'register_sessions'
                           AND identity."LocalId" = current."Id" LIMIT 1),
                        '__legacy__'
                    )
              );
            """,
            // Older versions marked both the original sale and its signed reversal
            // as Refunded. Restore the original to Completed so financial reports
            // include one positive sale and one negative refund transaction.
            """
            UPDATE "Sales"
            SET "Status" = 0,
                "UpdatedAt" = COALESCE("UpdatedAt", CURRENT_TIMESTAMP)
            WHERE "Status" = 3
              AND "Id" IN (
                  SELECT "RefundedSaleId" FROM "Sales"
                  WHERE "RefundedSaleId" IS NOT NULL
              );
            """,
            // Normalize signed legacy reversal amounts to the current refund model.
            """
            UPDATE "Sales"
            SET "Subtotal" = -ABS("Subtotal"),
                "DiscountTotal" = -ABS("DiscountTotal"),
                "TaxTotal" = -ABS("TaxTotal"),
                -- Make the reversal reconcile exactly to the original receipt.
                -- Negating ABS(Rounding) was wrong when the original receipt had
                -- negative rounding and could leave the refund a few cents short.
                "Rounding" = -ABS(
                    (SELECT original."Subtotal" - original."DiscountTotal" + original."TaxTotal" + original."Rounding"
                     FROM "Sales" AS original WHERE original."Id" = "Sales"."RefundedSaleId")
                ) - (-ABS("Subtotal") + ABS("DiscountTotal") - ABS("TaxTotal")),
                "AmountPaid" = -ABS(
                    (SELECT original."Subtotal" - original."DiscountTotal" + original."TaxTotal" + original."Rounding"
                     FROM "Sales" AS original WHERE original."Id" = "Sales"."RefundedSaleId")
                ),
                "Change" = 0
            WHERE "Status" = 3
              AND "RefundedSaleId" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "SaleItems" AS refundItem
                  WHERE refundItem."SaleId" = "Sales"."Id"
                    AND refundItem."RefundedSaleItemId" IS NOT NULL
              );
            """,
            // Legacy refunds did not create reverse tender rows, leaving register
            // cash and payment reports overstated. Add them only when the reversal
            // currently has no payment rows.
            """
            INSERT INTO "SalePayments" ("SaleId", "Method", "Amount", "Reference", "CreatedAt")
            SELECT refund."Id", originalPayment."Method", -ABS(originalPayment."Amount"),
                   'Legacy refund ' || original."ReceiptNumber",
                   COALESCE(refund."SaleDate", CURRENT_TIMESTAMP)
            FROM "Sales" AS refund
            JOIN "Sales" AS original ON original."Id" = refund."RefundedSaleId"
            JOIN "SalePayments" AS originalPayment ON originalPayment."SaleId" = original."Id"
            WHERE refund."Status" = 3
              AND refund."RefundedSaleId" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "SalePayments" AS existing WHERE existing."SaleId" = refund."Id"
              );
            """
        };

        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command);
    }

    private static async Task RelaxStoreScopedUniqueIndexesAsync(AppDbContext db)
    {
        // v1.x used database-wide uniqueness. In v2, the same SKU, category,
        // promotion code, setting key, receipt sequence, or document number may
        // legitimately exist in separate branches cached in one SQLite file.
        // Normal services remain scoped by SyncIdentities and retain their
        // within-store duplicate checks.
        var legacyCommands = new[]
        {
            "DROP TRIGGER IF EXISTS \"TR_Products_IdentifierNamespace_Insert\";",
            "DROP TRIGGER IF EXISTS \"TR_Products_IdentifierNamespace_Update\";",
            "DROP INDEX IF EXISTS \"UX_Products_Sku_NOCASE\";",
            "DROP INDEX IF EXISTS \"UX_Products_Barcode_NOCASE\";",
            "DROP INDEX IF EXISTS \"UX_Discounts_Code_NOCASE\";"
        };

        foreach (var command in legacyCommands)
            await db.Database.ExecuteSqlRawAsync(command);

        await EnsureNonUniqueIndexAsync(db, "IX_Products_Sku", "Products", "Sku");
        await EnsureNonUniqueIndexAsync(db, "IX_Products_Barcode", "Products", "Barcode");
        await EnsureNonUniqueIndexAsync(db, "IX_Categories_Name", "Categories", "Name");
        await EnsureNonUniqueIndexAsync(db, "IX_Discounts_Code", "Discounts", "Code");
        await EnsureNonUniqueIndexAsync(db, "IX_Settings_Key", "Settings", "Key");
        await EnsureNonUniqueIndexAsync(db, "IX_Sales_ReceiptNumber", "Sales", "ReceiptNumber");
        await EnsureNonUniqueIndexAsync(
            db, "IX_PurchaseDocuments_DocumentNumber", "PurchaseDocuments", "DocumentNumber");
    }

    private static async Task EnsureNonUniqueIndexAsync(
        AppDbContext db,
        string indexName,
        string tableName,
        string columnName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();
        try
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            lookup.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
            var parameter = lookup.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = indexName;
            lookup.Parameters.Add(parameter);
            var definition = Convert.ToString(await lookup.ExecuteScalarAsync());
            if (!string.IsNullOrWhiteSpace(definition) &&
                !definition.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase))
                return;

            await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{indexName}\";");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE INDEX \"{indexName}\" ON \"{tableName}\" (\"{columnName}\");");
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task CreateNoCaseUniqueIndexIfSafeAsync(
        AppDbContext db, string indexName, string tableName, string columnName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();
        try
        {
            await using var duplicate = connection.CreateCommand();
            duplicate.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            duplicate.CommandText =
                $"SELECT COUNT(*) FROM (SELECT \"{columnName}\" COLLATE NOCASE FROM \"{tableName}\" " +
                $"WHERE \"{columnName}\" IS NOT NULL AND TRIM(\"{columnName}\") <> '' " +
                $"GROUP BY \"{columnName}\" COLLATE NOCASE HAVING COUNT(*) > 1);";
            var duplicateGroups = Convert.ToInt32(await duplicate.ExecuteScalarAsync());
            if (duplicateGroups > 0) return;

            await using var create = connection.CreateCommand();
            create.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            create.CommandText =
                $"CREATE UNIQUE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" (\"{columnName}\" COLLATE NOCASE) " +
                $"WHERE \"{columnName}\" IS NOT NULL AND TRIM(\"{columnName}\") <> '';";
            await create.ExecuteNonQueryAsync();
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task<bool> EnsureColumnAsync(
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
                info.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
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

            if (exists) return false;

            await using var alter = connection.CreateCommand();
            alter.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            alter.CommandText = $"ALTER TABLE \"{quotedTable}\" ADD COLUMN {columnDeclaration};";
            await alter.ExecuteNonQueryAsync();
            return true;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }
}
