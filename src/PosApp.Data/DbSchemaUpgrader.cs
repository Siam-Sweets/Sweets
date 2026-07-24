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
            CREATE TABLE IF NOT EXISTS "Stores" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Stores" PRIMARY KEY AUTOINCREMENT,
                "SyncId" TEXT NOT NULL,
                "Code" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Address" TEXT NULL,
                "Phone" TEXT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncOutbox" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncOutbox" PRIMARY KEY AUTOINCREMENT,
                "StoreId" INTEGER NOT NULL,
                "ChangeId" TEXT NOT NULL,
                "EntityType" TEXT NOT NULL,
                "EntitySyncId" TEXT NOT NULL,
                "Operation" TEXT NOT NULL,
                "EntityVersion" INTEGER NOT NULL,
                "BaseCloudVersion" INTEGER NOT NULL DEFAULT 0,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "AttemptCount" INTEGER NOT NULL DEFAULT 0,
                "LastAttemptAt" TEXT NULL,
                "LastError" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncStates" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncStates" PRIMARY KEY AUTOINCREMENT,
                "StoreId" INTEGER NOT NULL,
                "DeviceId" TEXT NOT NULL,
                "PullCursor" INTEGER NOT NULL DEFAULT 0,
                "LastSyncAt" TEXT NULL,
                "LastSuccessfulSyncAt" TEXT NULL,
                "LastSnapshotUploadedAt" TEXT NULL,
                "LastError" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncConflicts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncConflicts" PRIMARY KEY AUTOINCREMENT,
                "StoreId" INTEGER NOT NULL,
                "EntityType" TEXT NOT NULL,
                "EntitySyncId" TEXT NOT NULL,
                "ChangeId" TEXT NOT NULL,
                "LocalBaseCloudVersion" INTEGER NOT NULL,
                "RemoteCloudVersion" INTEGER NOT NULL,
                "LocalOperation" TEXT NOT NULL DEFAULT 'upsert',
                "RemoteOperation" TEXT NOT NULL DEFAULT 'upsert',
                "LocalPayloadJson" TEXT NOT NULL,
                "RemotePayloadJson" TEXT NOT NULL,
                "Message" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ResolvedAt" TEXT NULL,
                "Resolution" TEXT NULL,
                "ResolvedPayloadJson" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "SyncRuns" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncRuns" PRIMARY KEY AUTOINCREMENT,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "Status" TEXT NOT NULL,
                "DeviceId" TEXT NOT NULL,
                "StoreCount" INTEGER NOT NULL DEFAULT 0,
                "PushedChanges" INTEGER NOT NULL DEFAULT 0,
                "PulledChanges" INTEGER NOT NULL DEFAULT 0,
                "ConflictCount" INTEGER NOT NULL DEFAULT 0,
                "PendingAfter" INTEGER NOT NULL DEFAULT 0,
                "Error" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "StockTransfers" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_StockTransfers" PRIMARY KEY AUTOINCREMENT,
                "StoreId" INTEGER NOT NULL,
                "SyncId" TEXT NOT NULL,
                "SyncVersion" INTEGER NOT NULL DEFAULT 1,
                "SyncUpdatedAt" TEXT NOT NULL,
                "CloudVersion" INTEGER NOT NULL DEFAULT 0,
                "TransferNumber" TEXT NOT NULL,
                "DestinationStoreId" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "Note" TEXT NULL,
                "CreatedByUserId" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL,
                "DispatchedByUserId" INTEGER NULL,
                "DispatchedAt" TEXT NULL,
                "ReceivedByUserId" INTEGER NULL,
                "ReceivedAt" TEXT NULL,
                "CancelledByUserId" INTEGER NULL,
                "CancelledAt" TEXT NULL,
                "CancellationReason" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "StockTransferItems" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_StockTransferItems" PRIMARY KEY AUTOINCREMENT,
                "StoreId" INTEGER NOT NULL,
                "SyncId" TEXT NOT NULL,
                "SyncVersion" INTEGER NOT NULL DEFAULT 1,
                "SyncUpdatedAt" TEXT NOT NULL,
                "CloudVersion" INTEGER NOT NULL DEFAULT 0,
                "StockTransferId" INTEGER NOT NULL,
                "ProductId" INTEGER NOT NULL,
                "DestinationProductId" INTEGER NOT NULL,
                "ProductName" TEXT NOT NULL,
                "Sku" TEXT NULL,
                "Unit" INTEGER NOT NULL,
                "Quantity" decimal(18,4) NOT NULL,
                "UnitCost" decimal(18,4) NOT NULL,
                CONSTRAINT "FK_StockTransferItems_StockTransfers_StockTransferId" FOREIGN KEY ("StockTransferId") REFERENCES "StockTransfers" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_StockTransferItems_Products_ProductId" FOREIGN KEY ("ProductId") REFERENCES "Products" ("Id") ON DELETE RESTRICT
            );
            """,
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
            "CREATE INDEX IF NOT EXISTS \"IX_PurchaseDocuments_DocumentNumber_Legacy\" ON \"PurchaseDocuments\" (\"DocumentNumber\");",
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
            "CREATE INDEX IF NOT EXISTS \"IX_SalePayments_SaleId\" ON \"SalePayments\" (\"SaleId\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_StockTransfers_StoreId_TransferNumber\" ON \"StockTransfers\" (\"StoreId\", \"TransferNumber\" COLLATE NOCASE);",
            "CREATE INDEX IF NOT EXISTS \"IX_StockTransfers_DestinationStoreId_Status\" ON \"StockTransfers\" (\"DestinationStoreId\", \"Status\");",
            "CREATE INDEX IF NOT EXISTS \"IX_StockTransferItems_StockTransferId\" ON \"StockTransferItems\" (\"StockTransferId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_StockTransferItems_ProductId\" ON \"StockTransferItems\" (\"ProductId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_StockTransferItems_DestinationProductId\" ON \"StockTransferItems\" (\"DestinationProductId\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Stores_Code\" ON \"Stores\" (\"Code\" COLLATE NOCASE);",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Stores_SyncId\" ON \"Stores\" (\"SyncId\" COLLATE NOCASE);",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SyncOutbox_ChangeId\" ON \"SyncOutbox\" (\"ChangeId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_SyncOutbox_StoreId_Id\" ON \"SyncOutbox\" (\"StoreId\", \"Id\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SyncStates_StoreId\" ON \"SyncStates\" (\"StoreId\");",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SyncConflicts_ChangeId\" ON \"SyncConflicts\" (\"ChangeId\");",
            "CREATE INDEX IF NOT EXISTS \"IX_SyncConflicts_StoreId_ResolvedAt\" ON \"SyncConflicts\" (\"StoreId\", \"ResolvedAt\");",
            "CREATE INDEX IF NOT EXISTS \"IX_SyncRuns_StartedAt\" ON \"SyncRuns\" (\"StartedAt\");"
        };

        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command);

        await EnsureColumnAsync(db, "SyncConflicts", "LocalOperation",
            "\"LocalOperation\" TEXT NOT NULL DEFAULT 'upsert'");
        await EnsureColumnAsync(db, "SyncConflicts", "RemoteOperation",
            "\"RemoteOperation\" TEXT NOT NULL DEFAULT 'upsert'");
        await EnsureColumnAsync(db, "SyncConflicts", "Resolution",
            "\"Resolution\" TEXT NULL");
        await EnsureColumnAsync(db, "SyncConflicts", "ResolvedPayloadJson",
            "\"ResolvedPayloadJson\" TEXT NULL");

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
        await EnsureColumnAsync(db, "StockTransactions", "StockTransferId",
            "\"StockTransferId\" INTEGER NULL");
        await EnsureColumnAsync(db, "StockTransactions", "StockTransferItemId",
            "\"StockTransferItemId\" INTEGER NULL");
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
        await EnsureColumnAsync(db, "Stores", "SyncVersion",
            "\"SyncVersion\" INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(db, "Stores", "SyncUpdatedAt",
            "\"SyncUpdatedAt\" TEXT NOT NULL DEFAULT '1970-01-01 00:00:00'");
        await EnsureColumnAsync(db, "Stores", "CloudVersion",
            "\"CloudVersion\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "SyncOutbox", "BaseCloudVersion",
            "\"BaseCloudVersion\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "SyncStates", "LastSnapshotUploadedAt",
            "\"LastSnapshotUploadedAt\" TEXT NULL");
        await EnsureColumnAsync(db, "Products", "StockVersion",
            "\"StockVersion\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "Discounts", "UsageVersion",
            "\"UsageVersion\" INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "Sales", "OperationId",
            "\"OperationId\" TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "PurchaseDocuments", "OperationId",
            "\"OperationId\" TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "StockTransfers", "OperationId",
            "\"OperationId\" TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "SaleItems", "CategoryName",
            "\"CategoryName\" TEXT NOT NULL DEFAULT 'Uncategorized'");
        await EnsureColumnAsync(db, "SaleItems", "RefundedQuantity",
            "\"RefundedQuantity\" decimal(18,4) NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "StockTransactions", "OperationKey",
            "\"OperationKey\" TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "SyncOutbox", "OperationId",
            "\"OperationId\" TEXT NOT NULL DEFAULT ''");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"SyncOutbox\" SET \"OperationId\" = \"ChangeId\" " +
            "WHERE \"OperationId\" IS NULL OR TRIM(\"OperationId\") = '';");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"SaleItems\" SET \"CategoryName\" = COALESCE((" +
            "SELECT c.\"Name\" FROM \"Products\" p LEFT JOIN \"Categories\" c ON c.\"Id\" = p.\"CategoryId\" " +
            "WHERE p.\"Id\" = \"SaleItems\".\"ProductId\"), 'Uncategorized') " +
            "WHERE \"CategoryName\" IS NULL OR TRIM(\"CategoryName\") = '' OR \"CategoryName\" = 'Uncategorized';");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"SaleItems\" SET \"RefundedQuantity\" = COALESCE((" +
            "SELECT SUM(ABS(r.\"Quantity\")) FROM \"SaleItems\" r " +
            "WHERE r.\"RefundedSaleItemId\" = \"SaleItems\".\"Id\"), 0) " +
            "WHERE \"Quantity\" > 0;");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Stores\" SET \"SyncUpdatedAt\" = CURRENT_TIMESTAMP " +
            "WHERE \"SyncUpdatedAt\" = '1970-01-01 00:00:00';");

        var storeScopedTables = new[]
        {
            "Products", "Categories", "Customers", "Users", "Sales", "SaleItems",
            "SalePayments", "StockTransactions", "Taxes", "Discounts", "Settings",
            "Suppliers", "PurchaseDocuments", "PurchaseItems", "CashSessions", "CashMovements",
            "StockTransfers", "StockTransferItems"
        };
        foreach (var table in storeScopedTables)
        {
            await EnsureColumnAsync(db, table, "StoreId", "\"StoreId\" INTEGER NOT NULL DEFAULT 1");
            await EnsureColumnAsync(db, table, "SyncId", "\"SyncId\" TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(db, table, "SyncVersion", "\"SyncVersion\" INTEGER NOT NULL DEFAULT 1");
            await EnsureColumnAsync(db, table, "SyncUpdatedAt", "\"SyncUpdatedAt\" TEXT NOT NULL DEFAULT '1970-01-01 00:00:00'");
            await EnsureColumnAsync(db, table, "CloudVersion", "\"CloudVersion\" INTEGER NOT NULL DEFAULT 0");
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{table}\" SET \"SyncId\" = lower(hex(randomblob(16))), " +
                $"\"SyncUpdatedAt\" = CASE WHEN \"SyncUpdatedAt\" = '1970-01-01 00:00:00' THEN CURRENT_TIMESTAMP ELSE \"SyncUpdatedAt\" END " +
                $"WHERE \"SyncId\" IS NULL OR TRIM(\"SyncId\") = '' OR \"SyncUpdatedAt\" = '1970-01-01 00:00:00';");
        }
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Sales\" SET \"OperationId\" = 'legacy-sale:' || \"SyncId\" " +
            "WHERE \"OperationId\" IS NULL OR TRIM(\"OperationId\") = '';");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"PurchaseDocuments\" SET \"OperationId\" = 'legacy-purchase:' || \"SyncId\" " +
            "WHERE \"OperationId\" IS NULL OR TRIM(\"OperationId\") = '';");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"StockTransfers\" SET \"OperationId\" = 'legacy-transfer:' || \"SyncId\" " +
            "WHERE \"OperationId\" IS NULL OR TRIM(\"OperationId\") = '';");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"StockTransactions\" SET \"OperationKey\" = 'legacy-stock:' || \"SyncId\" " +
            "WHERE \"OperationKey\" IS NULL OR TRIM(\"OperationKey\") = '';");

        // EnsureCreated builds the current Store table with non-null sync metadata
        // columns. Supply those values explicitly; INSERT OR IGNORE previously hid
        // the constraint failure and left a fresh installation with no active store.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Stores"
                ("Id", "SyncId", "Code", "Name", "Address", "Phone", "IsActive", "CreatedAt",
                 "SyncVersion", "SyncUpdatedAt", "CloudVersion")
            SELECT 1, lower(hex(randomblob(16))), 'MAIN',
                   COALESCE((SELECT CASE WHEN json_valid("Value") THEN json_extract("Value", '$.StoreName') END FROM "Settings" WHERE "Key" = 'store:config' ORDER BY "Id" LIMIT 1), 'Main Store'),
                   COALESCE((SELECT CASE WHEN json_valid("Value") THEN json_extract("Value", '$.Address') END FROM "Settings" WHERE "Key" = 'store:config' ORDER BY "Id" LIMIT 1), ''),
                   COALESCE((SELECT CASE WHEN json_valid("Value") THEN json_extract("Value", '$.Phone') END FROM "Settings" WHERE "Key" = 'store:config' ORDER BY "Id" LIMIT 1), ''),
                   1, CURRENT_TIMESTAMP, 1, CURRENT_TIMESTAMP, 0
            WHERE NOT EXISTS (SELECT 1 FROM "Stores");
            """);

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

        // Older databases could contain identifiers that differ only by letter case.
        // Keep the oldest record authoritative and make later duplicates unambiguous
        // before publishing case-insensitive unique indexes.
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
                WHERE session."StoreId" = "Sales"."StoreId"
                  AND session."OpenedAt" <= "Sales"."SaleDate"
                  AND (session."ClosedAt" IS NULL OR "Sales"."SaleDate" <= session."ClosedAt")
                ORDER BY session."OpenedAt" DESC, session."Id" DESC
                LIMIT 1
            )
            WHERE "CashSessionId" IS NULL
              AND EXISTS (
                SELECT 1
                FROM "CashSessions" AS session
                WHERE session."StoreId" = "Sales"."StoreId"
                  AND session."OpenedAt" <= "Sales"."SaleDate"
                  AND (session."ClosedAt" IS NULL OR "Sales"."SaleDate" <= session."ClosedAt")
              );
            """);

        var obsoleteGlobalIndexes = new[]
        {
            "IX_Products_Sku", "IX_Products_Barcode", "IX_Categories_Name",
            "IX_Users_Username", "IX_Sales_ReceiptNumber", "IX_Discounts_Code",
            "IX_Settings_Key", "IX_PurchaseDocuments_DocumentNumber", "IX_PurchaseDocuments_DocumentNumber_Legacy",
            "UX_Users_Username_NOCASE", "UX_Products_Sku_NOCASE",
            "UX_Products_Barcode_NOCASE", "UX_Discounts_Code_NOCASE"
        };
        foreach (var index in obsoleteGlobalIndexes)
            await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{index}\";");

        await CreateNoCaseUniqueIndexIfSafeAsync(db, "UX_Users_Store_Username_NOCASE", "Users", "Username");
        await CreateNoCaseUniqueIndexIfSafeAsync(db, "UX_Products_Store_Sku_NOCASE", "Products", "Sku");
        await CreateNoCaseUniqueIndexIfSafeAsync(db, "UX_Products_Store_Barcode_NOCASE", "Products", "Barcode");
        await CreateNoCaseUniqueIndexIfSafeAsync(db, "UX_Discounts_Store_Code_NOCASE", "Discounts", "Code");
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Categories_StoreId_Name\" ON \"Categories\" (\"StoreId\", \"Name\" COLLATE NOCASE);");
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Sales_StoreId_ReceiptNumber\" ON \"Sales\" (\"StoreId\", \"ReceiptNumber\");");
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Settings_StoreId_Key\" ON \"Settings\" (\"StoreId\", \"Key\");");
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PurchaseDocuments_StoreId_DocumentNumber\" ON \"PurchaseDocuments\" (\"StoreId\", \"DocumentNumber\");");
        foreach (var table in storeScopedTables)
        {
            await db.Database.ExecuteSqlRawAsync($"CREATE INDEX IF NOT EXISTS \"IX_{table}_StoreId\" ON \"{table}\" (\"StoreId\");");
            await db.Database.ExecuteSqlRawAsync($"CREATE UNIQUE INDEX IF NOT EXISTS \"IX_{table}_StoreId_SyncId\" ON \"{table}\" (\"StoreId\", \"SyncId\");");
        }
        await CreateProductIdentifierGuardsAsync(db);
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
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_CashSessions_OneOpen\" ON \"CashSessions\" (\"StoreId\") WHERE \"ClosedAt\" IS NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_Sales_CashSessionId\" ON \"Sales\" (\"CashSessionId\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_StockTransactions_StockTransferId\" ON \"StockTransactions\" (\"StockTransferId\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_StockTransactions_StockTransferItemId\" ON \"StockTransactions\" (\"StockTransferItemId\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_Sales_Store_OperationId\" ON \"Sales\" (\"StoreId\", \"OperationId\") WHERE TRIM(\"OperationId\") <> '';");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_PurchaseDocuments_Store_OperationId\" ON \"PurchaseDocuments\" (\"StoreId\", \"OperationId\") WHERE TRIM(\"OperationId\") <> '';");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_StockTransfers_Store_OperationId\" ON \"StockTransfers\" (\"StoreId\", \"OperationId\") WHERE TRIM(\"OperationId\") <> '';");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_StockTransactions_Store_OperationKey\" ON \"StockTransactions\" (\"StoreId\", \"OperationKey\") WHERE TRIM(\"OperationKey\") <> '';");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_SyncOutbox_OperationId_Id\" ON \"SyncOutbox\" (\"OperationId\", \"Id\");");
        await EnsureIntegrityGuardsAsync(db);

        await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }


    private static async Task ResolveLegacyDuplicatesAsync(AppDbContext db)
    {
        var commands = new[]
        {
            // Usernames cannot be empty; retain the original text and append a stable suffix.
            """
            WITH ranked AS (
                SELECT "Id", ROW_NUMBER() OVER (
                    PARTITION BY "StoreId", LOWER(TRIM("Username")) ORDER BY "Id") AS rn
                FROM "Users" WHERE TRIM("Username") <> ''
            )
            UPDATE "Users"
            SET "Username" = "Username" || '-dup-' || "Id"
            WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > 1);
            """,
            // Duplicate catalog identifiers were already ambiguous. Preserve the first
            // product and clear later copies so they can be assigned deliberately.
            """
            WITH ranked AS (
                SELECT "Id", ROW_NUMBER() OVER (
                    PARTITION BY "StoreId", LOWER(TRIM("Sku")) ORDER BY "Id") AS rn
                FROM "Products" WHERE "Sku" IS NOT NULL AND TRIM("Sku") <> ''
            )
            UPDATE "Products" SET "Sku" = NULL
            WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > 1);
            """,
            """
            WITH ranked AS (
                SELECT "Id", ROW_NUMBER() OVER (
                    PARTITION BY "StoreId", LOWER(TRIM("Barcode")) ORDER BY "Id") AS rn
                FROM "Products" WHERE "Barcode" IS NOT NULL AND TRIM("Barcode") <> ''
            )
            UPDATE "Products" SET "Barcode" = NULL
            WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > 1);
            """,
            // SKU and barcode are both accepted by the scanner, so they share one
            // namespace even though SQLite can only put a unique index on each
            // individual column. Preserve the oldest occurrence of an identifier
            // across both fields and clear every later ambiguous copy.
            """
            WITH identifiers AS (
                SELECT "Id", "StoreId", 'Sku' AS field, LOWER(TRIM("Sku")) AS value, 0 AS priority
                FROM "Products" WHERE "Sku" IS NOT NULL AND TRIM("Sku") <> ''
                UNION ALL
                SELECT "Id", "StoreId", 'Barcode' AS field, LOWER(TRIM("Barcode")) AS value, 1 AS priority
                FROM "Products" WHERE "Barcode" IS NOT NULL AND TRIM("Barcode") <> ''
            ), ranked AS (
                SELECT "Id", field, ROW_NUMBER() OVER (
                    PARTITION BY "StoreId", value ORDER BY "Id", priority) AS rn
                FROM identifiers
            )
            UPDATE "Products" SET "Sku" = NULL
            WHERE "Id" IN (SELECT "Id" FROM ranked WHERE field = 'Sku' AND rn > 1);
            """,
            """
            WITH identifiers AS (
                SELECT "Id", "StoreId", 'Sku' AS field, LOWER(TRIM("Sku")) AS value, 0 AS priority
                FROM "Products" WHERE "Sku" IS NOT NULL AND TRIM("Sku") <> ''
                UNION ALL
                SELECT "Id", "StoreId", 'Barcode' AS field, LOWER(TRIM("Barcode")) AS value, 1 AS priority
                FROM "Products" WHERE "Barcode" IS NOT NULL AND TRIM("Barcode") <> ''
            ), ranked AS (
                SELECT "Id", field, ROW_NUMBER() OVER (
                    PARTITION BY "StoreId", value ORDER BY "Id", priority) AS rn
                FROM identifiers
            )
            UPDATE "Products" SET "Barcode" = NULL
            WHERE "Id" IN (SELECT "Id" FROM ranked WHERE field = 'Barcode' AND rn > 1);
            """,
            """
            WITH ranked AS (
                SELECT "Id", ROW_NUMBER() OVER (
                    PARTITION BY "StoreId", LOWER(TRIM("Code")) ORDER BY "Id") AS rn
                FROM "Discounts" WHERE "Code" IS NOT NULL AND TRIM("Code") <> ''
            )
            UPDATE "Discounts" SET "Code" = NULL
            WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > 1);
            """,
            // If an older race created multiple open sessions, retain the newest one
            // and close the rest before adding the one-open-session constraint.
            """
            UPDATE "CashSessions"
            SET "ClosedAt" = COALESCE("ClosedAt", CURRENT_TIMESTAMP),
                "ExpectedCash" = COALESCE("ExpectedCash", "OpeningFloat"),
                "CountedCash" = COALESCE("CountedCash", "OpeningFloat"),
                "Variance" = COALESCE("Variance", 0),
                "Note" = CASE WHEN "Note" IS NULL OR TRIM("Note") = ''
                    THEN 'Automatically closed during database upgrade'
                    ELSE "Note" || CHAR(10) || 'Automatically closed during database upgrade' END
            WHERE "ClosedAt" IS NULL
              AND EXISTS (
                  SELECT 1 FROM "CashSessions" AS newer
                  WHERE newer."StoreId" = "CashSessions"."StoreId"
                    AND newer."ClosedAt" IS NULL
                    AND (newer."OpenedAt" > "CashSessions"."OpenedAt"
                         OR (newer."OpenedAt" = "CashSessions"."OpenedAt" AND newer."Id" > "CashSessions"."Id"))
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
            INSERT INTO "SalePayments"
                ("SaleId", "Method", "Amount", "Reference", "CreatedAt",
                 "StoreId", "SyncId", "SyncVersion", "SyncUpdatedAt")
            SELECT refund."Id", originalPayment."Method", -ABS(originalPayment."Amount"),
                   'Legacy refund ' || original."ReceiptNumber",
                   COALESCE(refund."SaleDate", CURRENT_TIMESTAMP),
                   refund."StoreId", lower(hex(randomblob(16))), 1, CURRENT_TIMESTAMP
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

    public static async Task DropIntegrityGuardsAsync(AppDbContext db)
    {
        foreach (var name in new[]
                 {
                     "TR_StockTransactions_AppendOnly_Update",
                     "TR_StockTransactions_AppendOnly_Delete",
                     "TR_Products_ProtectLedger_Delete",
                     "TR_StockTransfers_References_Insert",
                     "TR_StockTransfers_References_Update",
                     "TR_StockTransferItems_References_Insert",
                     "TR_StockTransferItems_References_Update",
                     "TR_StockTransactions_References_Insert",
                     "TR_StockTransactions_References_Update"
                 })
        {
            await db.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS \"{name}\";");
        }
    }

    public static async Task EnsureIntegrityGuardsAsync(AppDbContext db)
    {
        var commands = new[]
        {
            "DROP TRIGGER IF EXISTS \"TR_StockTransactions_AppendOnly_Update\";",
            "DROP TRIGGER IF EXISTS \"TR_StockTransactions_AppendOnly_Delete\";",
            "DROP TRIGGER IF EXISTS \"TR_Products_ProtectLedger_Delete\";",
            "DROP TRIGGER IF EXISTS \"TR_StockTransfers_References_Insert\";",
            "DROP TRIGGER IF EXISTS \"TR_StockTransfers_References_Update\";",
            "DROP TRIGGER IF EXISTS \"TR_StockTransferItems_References_Insert\";",
            "DROP TRIGGER IF EXISTS \"TR_StockTransferItems_References_Update\";",
            "DROP TRIGGER IF EXISTS \"TR_StockTransactions_References_Insert\";",
            "DROP TRIGGER IF EXISTS \"TR_StockTransactions_References_Update\";",
            """
            CREATE TRIGGER "TR_StockTransactions_AppendOnly_Update"
            BEFORE UPDATE ON "StockTransactions"
            WHEN NEW."ProductId" <> OLD."ProductId"
              OR NEW."Type" <> OLD."Type"
              OR NEW."Quantity" <> OLD."Quantity"
              OR NEW."BalanceAfter" <> OLD."BalanceAfter"
              OR COALESCE(NEW."UnitCost", -999999999) <> COALESCE(OLD."UnitCost", -999999999)
              OR COALESCE(NEW."SaleId", -1) <> COALESCE(OLD."SaleId", -1)
              OR COALESCE(NEW."SaleItemId", -1) <> COALESCE(OLD."SaleItemId", -1)
              OR COALESCE(NEW."StockTransferId", -1) <> COALESCE(OLD."StockTransferId", -1)
              OR COALESCE(NEW."StockTransferItemId", -1) <> COALESCE(OLD."StockTransferItemId", -1)
              OR COALESCE(NEW."Note", '') <> COALESCE(OLD."Note", '')
              OR COALESCE(NEW."UserId", -1) <> COALESCE(OLD."UserId", -1)
              OR NEW."CreatedAt" <> OLD."CreatedAt"
              OR NEW."StoreId" <> OLD."StoreId"
              OR NEW."SyncId" <> OLD."SyncId"
              OR NEW."OperationKey" <> OLD."OperationKey"
            BEGIN SELECT RAISE(ABORT, 'Stock ledger rows are append-only'); END;
            """,
            """
            CREATE TRIGGER "TR_StockTransactions_AppendOnly_Delete"
            BEFORE DELETE ON "StockTransactions"
            BEGIN SELECT RAISE(ABORT, 'Stock ledger rows are append-only'); END;
            """,
            """
            CREATE TRIGGER "TR_Products_ProtectLedger_Delete"
            BEFORE DELETE ON "Products"
            WHEN EXISTS (SELECT 1 FROM "StockTransactions" WHERE "ProductId" = OLD."Id")
            BEGIN SELECT RAISE(ABORT, 'A product with stock history cannot be deleted'); END;
            """,
            """
            CREATE TRIGGER "TR_StockTransfers_References_Insert"
            BEFORE INSERT ON "StockTransfers"
            WHEN NOT EXISTS (SELECT 1 FROM "Stores" WHERE "Id" = NEW."DestinationStoreId")
              OR (NEW."CreatedByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."CreatedByUserId"))
              OR (NEW."DispatchedByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."DispatchedByUserId"))
              OR (NEW."ReceivedByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."ReceivedByUserId"))
              OR (NEW."CancelledByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."CancelledByUserId"))
            BEGIN SELECT RAISE(ABORT, 'Stock transfer contains an invalid reference'); END;
            """,
            """
            CREATE TRIGGER "TR_StockTransfers_References_Update"
            BEFORE UPDATE ON "StockTransfers"
            WHEN NOT EXISTS (SELECT 1 FROM "Stores" WHERE "Id" = NEW."DestinationStoreId")
              OR (NEW."CreatedByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."CreatedByUserId"))
              OR (NEW."DispatchedByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."DispatchedByUserId"))
              OR (NEW."ReceivedByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."ReceivedByUserId"))
              OR (NEW."CancelledByUserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."CancelledByUserId"))
            BEGIN SELECT RAISE(ABORT, 'Stock transfer contains an invalid reference'); END;
            """,
            """
            CREATE TRIGGER "TR_StockTransferItems_References_Insert"
            BEFORE INSERT ON "StockTransferItems"
            WHEN NOT EXISTS (SELECT 1 FROM "StockTransfers" WHERE "Id" = NEW."StockTransferId")
              OR NOT EXISTS (SELECT 1 FROM "Products" WHERE "Id" = NEW."ProductId")
              OR NOT EXISTS (SELECT 1 FROM "Products" WHERE "Id" = NEW."DestinationProductId")
            BEGIN SELECT RAISE(ABORT, 'Stock transfer item contains an invalid reference'); END;
            """,
            """
            CREATE TRIGGER "TR_StockTransferItems_References_Update"
            BEFORE UPDATE ON "StockTransferItems"
            WHEN NOT EXISTS (SELECT 1 FROM "StockTransfers" WHERE "Id" = NEW."StockTransferId")
              OR NOT EXISTS (SELECT 1 FROM "Products" WHERE "Id" = NEW."ProductId")
              OR NOT EXISTS (SELECT 1 FROM "Products" WHERE "Id" = NEW."DestinationProductId")
            BEGIN SELECT RAISE(ABORT, 'Stock transfer item contains an invalid reference'); END;
            """,
            """
            CREATE TRIGGER "TR_StockTransactions_References_Insert"
            BEFORE INSERT ON "StockTransactions"
            WHEN NOT EXISTS (SELECT 1 FROM "Products" WHERE "Id" = NEW."ProductId")
              OR (NEW."SaleId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Sales" WHERE "Id" = NEW."SaleId"))
              OR (NEW."SaleItemId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "SaleItems" WHERE "Id" = NEW."SaleItemId"))
              OR (NEW."StockTransferId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "StockTransfers" WHERE "Id" = NEW."StockTransferId"))
              OR (NEW."StockTransferItemId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "StockTransferItems" WHERE "Id" = NEW."StockTransferItemId"))
              OR (NEW."UserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."UserId"))
            BEGIN SELECT RAISE(ABORT, 'Stock transaction contains an invalid reference'); END;
            """,
            """
            CREATE TRIGGER "TR_StockTransactions_References_Update"
            BEFORE UPDATE ON "StockTransactions"
            WHEN NOT EXISTS (SELECT 1 FROM "Products" WHERE "Id" = NEW."ProductId")
              OR (NEW."SaleId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Sales" WHERE "Id" = NEW."SaleId"))
              OR (NEW."SaleItemId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "SaleItems" WHERE "Id" = NEW."SaleItemId"))
              OR (NEW."StockTransferId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "StockTransfers" WHERE "Id" = NEW."StockTransferId"))
              OR (NEW."StockTransferItemId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "StockTransferItems" WHERE "Id" = NEW."StockTransferItemId"))
              OR (NEW."UserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Users" WHERE "Id" = NEW."UserId"))
            BEGIN SELECT RAISE(ABORT, 'Stock transaction contains an invalid reference'); END;
            """
        };
        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command);
    }

    private static async Task CreateProductIdentifierGuardsAsync(AppDbContext db)
    {
        // SKU and barcode share one identifier namespace inside each store.
        var commands = new[]
        {
            "DROP TRIGGER IF EXISTS \"TR_Products_IdentifierNamespace_Insert\";",
            "DROP TRIGGER IF EXISTS \"TR_Products_IdentifierNamespace_Update\";",
            """
            CREATE TRIGGER "TR_Products_IdentifierNamespace_Insert"
            BEFORE INSERT ON "Products"
            WHEN EXISTS (
                SELECT 1 FROM "Products" AS existing
                WHERE existing."StoreId" = NEW."StoreId" AND
                      ((NEW."Sku" IS NOT NULL AND TRIM(NEW."Sku") <> '' AND
                        (LOWER(TRIM(existing."Sku")) = LOWER(TRIM(NEW."Sku")) OR
                         LOWER(TRIM(existing."Barcode")) = LOWER(TRIM(NEW."Sku"))))
                       OR (NEW."Barcode" IS NOT NULL AND TRIM(NEW."Barcode") <> '' AND
                        (LOWER(TRIM(existing."Sku")) = LOWER(TRIM(NEW."Barcode")) OR
                         LOWER(TRIM(existing."Barcode")) = LOWER(TRIM(NEW."Barcode")))))
            )
            BEGIN
                SELECT RAISE(ABORT, 'SKU or barcode is already used by another product in this store');
            END;
            """,
            """
            CREATE TRIGGER "TR_Products_IdentifierNamespace_Update"
            BEFORE UPDATE OF "Sku", "Barcode", "StoreId" ON "Products"
            WHEN EXISTS (
                SELECT 1 FROM "Products" AS existing
                WHERE existing."Id" <> NEW."Id"
                  AND existing."StoreId" = NEW."StoreId"
                  AND ((NEW."Sku" IS NOT NULL AND TRIM(NEW."Sku") <> '' AND
                        (LOWER(TRIM(existing."Sku")) = LOWER(TRIM(NEW."Sku")) OR
                         LOWER(TRIM(existing."Barcode")) = LOWER(TRIM(NEW."Sku"))))
                       OR (NEW."Barcode" IS NOT NULL AND TRIM(NEW."Barcode") <> '' AND
                        (LOWER(TRIM(existing."Sku")) = LOWER(TRIM(NEW."Barcode")) OR
                         LOWER(TRIM(existing."Barcode")) = LOWER(TRIM(NEW."Barcode")))))
            )
            BEGIN
                SELECT RAISE(ABORT, 'SKU or barcode is already used by another product in this store');
            END;
            """
        };

        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command);
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
                $"SELECT COUNT(*) FROM (SELECT \"StoreId\", \"{columnName}\" COLLATE NOCASE FROM \"{tableName}\" " +
                $"WHERE \"{columnName}\" IS NOT NULL AND TRIM(\"{columnName}\") <> '' " +
                $"GROUP BY \"StoreId\", \"{columnName}\" COLLATE NOCASE HAVING COUNT(*) > 1);";
            var duplicateGroups = Convert.ToInt32(await duplicate.ExecuteScalarAsync());
            if (duplicateGroups > 0) return;

            await using var create = connection.CreateCommand();
            create.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            create.CommandText =
                $"CREATE UNIQUE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" " +
                $"(\"StoreId\", \"{columnName}\" COLLATE NOCASE) " +
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
