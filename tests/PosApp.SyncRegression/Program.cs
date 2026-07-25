using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Services;
using PosApp.Data;

var databasePath = Path.Combine(
    Path.GetTempPath(), $"posapp-sync-regression-{Guid.NewGuid():N}.db");
try
{
    await using var db = new AppDbContext($"Data Source={databasePath};Pooling=False");
    await db.Database.EnsureCreatedAsync();

    var store = new Store
    {
        SyncId = "store-sync",
        Code = "MAIN",
        Name = "Main Store",
        CloudVersion = 5
    };
    db.Stores.Add(store);
    await db.SaveChangesAsync();

    db.Categories.AddRange(
        new Category
        {
            StoreId = store.Id,
            SyncId = "already-synced",
            Name = "Already Synced",
            CloudVersion = 5
        },
        new Category
        {
            StoreId = store.Id,
            SyncId = "lower-revision",
            Name = "Lower Revision",
            CloudVersion = 4
        },
        new Category
        {
            StoreId = store.Id,
            SyncId = "still-pending",
            Name = "Still Pending",
            CloudVersion = 5
        });
    db.SyncOutbox.Add(new SyncOutboxItem
    {
        StoreId = store.Id,
        ChangeId = "pending-change",
        OperationId = "pending-operation",
        EntityType = nameof(Category),
        EntitySyncId = "still-pending",
        EntityVersion = 2,
        BaseCloudVersion = 4,
        PayloadJson = "{}",
        LastError = "Conflict: retry fixture"
    });
    db.SyncConflicts.AddRange(
        Conflict(store.Id, "synced-conflict", nameof(Category), "already-synced", 5),
        Conflict(store.Id, "lower-conflict", nameof(Category), "lower-revision", 5),
        Conflict(store.Id, "pending-conflict", nameof(Category), "still-pending", 5),
        Conflict(store.Id, "delete-conflict", nameof(Category), "cloud-deleted", 5, "delete"),
        Conflict(store.Id, "ledger-delete-conflict", nameof(Sale), "cloud-deleted", 5, "delete"),
        Conflict(store.Id, "unknown-conflict", "UnknownEntity", "cloud-deleted", 5, "delete"));
    await db.SaveChangesAsync();

    var service = new CloudSyncService(
        db, new TestStoreContext(store.Id, store.SyncId), null!, null!);
    var repaired = await InvokeAsync<int>(
        service, "ResolveSynchronizedConflictsAsync", CancellationToken.None);

    Assert(repaired == 2, $"Expected two stale conflicts to close, but closed {repaired}.");
    await AssertResolutionAsync(db, "synced-conflict", expectedResolved: true);
    await AssertResolutionAsync(db, "delete-conflict", expectedResolved: true);
    await AssertResolutionAsync(db, "lower-conflict", expectedResolved: false);
    await AssertResolutionAsync(db, "pending-conflict", expectedResolved: false);
    await AssertResolutionAsync(db, "ledger-delete-conflict", expectedResolved: false);
    await AssertResolutionAsync(db, "unknown-conflict", expectedResolved: false);

    await InvokeTaskAsync(
        service,
        "ResolveEntityConflictsAsync",
        store.Id,
        nameof(Category),
        "lower-revision",
        5L,
        "synchronized",
        "{}",
        CancellationToken.None);
    await db.SaveChangesAsync();
    await AssertResolutionAsync(db, "lower-conflict", expectedResolved: true);

    await InvokeTaskAsync(
        service,
        "UpdateEntityCloudVersionAsync",
        store.Id,
        nameof(Category),
        "still-pending",
        5L,
        0L,
        CancellationToken.None);
    await InvokeTaskAsync(
        service,
        "ResolveEntityConflictsAsync",
        store.Id,
        nameof(Category),
        "still-pending",
        5L,
        "synchronized",
        "{}",
        CancellationToken.None);
    await db.SaveChangesAsync();

    var rebased = await db.SyncOutbox.AsNoTracking()
        .SingleAsync(x => x.ChangeId == "pending-change");
    Assert(rebased.BaseCloudVersion == 5, "Own-device pull did not rebase the later edit.");
    Assert(rebased.LastError == null, "Own-device pull did not unblock the rebased edit.");
    await AssertResolutionAsync(db, "pending-conflict", expectedResolved: true);

    await AssertStoreSeedingAsync(db, store);

    Console.WriteLine("PosApp synchronized-conflict and store-seeding regressions passed.");
}
finally
{
    if (File.Exists(databasePath)) File.Delete(databasePath);
    if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
    if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
}

static SyncConflict Conflict(
    int storeId,
    string changeId,
    string entityType,
    string entitySyncId,
    long remoteCloudVersion,
    string remoteOperation = "upsert")
    => new()
    {
        StoreId = storeId,
        ChangeId = changeId,
        EntityType = entityType,
        EntitySyncId = entitySyncId,
        LocalBaseCloudVersion = 0,
        RemoteCloudVersion = remoteCloudVersion,
        LocalOperation = "upsert",
        RemoteOperation = remoteOperation,
        LocalPayloadJson = "{}",
        RemotePayloadJson = "{}",
        Message = "Regression fixture"
    };

static async Task AssertResolutionAsync(
    AppDbContext db,
    string changeId,
    bool expectedResolved)
{
    var resolvedAt = await db.SyncConflicts.AsNoTracking()
        .Where(x => x.ChangeId == changeId)
        .Select(x => x.ResolvedAt)
        .SingleAsync();
    Assert(
        (resolvedAt != null) == expectedResolved,
        $"Conflict {changeId} resolution state was not {expectedResolved}.");
}

static async Task<T> InvokeAsync<T>(
    CloudSyncService service,
    string methodName,
    params object[] arguments)
{
    var method = FindMethod(methodName);
    return await (Task<T>)method.Invoke(service, arguments)!;
}

static async Task InvokeTaskAsync(
    CloudSyncService service,
    string methodName,
    params object[] arguments)
{
    var method = FindMethod(methodName);
    await (Task)method.Invoke(service, arguments)!;
}

static MethodInfo FindMethod(string methodName)
    => typeof(CloudSyncService).GetMethod(
           methodName, BindingFlags.Instance | BindingFlags.NonPublic)
       ?? throw new InvalidOperationException($"CloudSyncService.{methodName} was not found.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task AssertStoreSeedingAsync(AppDbContext db, Store sourceStore)
{
    var (hash, salt) = DbSeeder.HashPin("1234");
    db.Users.Add(new User
    {
        StoreId = sourceStore.Id,
        Username = "store-admin",
        FullName = "Store Admin",
        PasswordHash = hash,
        PasswordSalt = salt,
        Role = UserRole.Admin,
        IsActive = true
    });

    var targetStore = new Store
    {
        Code = "BRANCH",
        Name = "Branch Store",
        SyncId = "branch-store-sync"
    };
    db.Stores.Add(targetStore);
    await db.SaveChangesAsync();

    db.Categories.Add(new Category
    {
        StoreId = targetStore.Id,
        Name = "Beverages",
        Color = "#000000",
        SortOrder = 99,
        IsActive = true
    });

    var service = new StoreService(
        db,
        new TestStoreContext(sourceStore.Id, sourceStore.SyncId),
        new TestUserSessionContext(null));
    await InvokeTaskAsync(service, "SeedNewStoreAsync", targetStore);

    var categories = await db.Categories
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(category => category.StoreId == targetStore.Id)
        .ToListAsync();
    Assert(
        categories.Count(category =>
            string.Equals(category.Name, "Beverages", StringComparison.OrdinalIgnoreCase)) == 1,
        "New-store seeding created duplicate Beverages categories.");
    Assert(
        categories.Select(category => category.Name.ToUpperInvariant()).Distinct().Count() == 6,
        "New-store seeding did not create exactly the six default categories.");
}

file sealed class TestStoreContext(int storeId, string storeSyncId) : IStoreContext
{
    public int StoreId { get; } = storeId;
    public string StoreSyncId { get; } = storeSyncId;
    public bool IsCloudSyncEnabled => false;
    public bool IsCloudCaptureSuppressed => false;
    public IDisposable SuppressCloudCapture() => EmptyScope.Instance;
    public void SetCurrentStore(Store store) { }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}

file sealed class TestUserSessionContext(int? userId) : IUserSessionContext
{
    public int? UserId { get; } = userId;
    public int? StoreId => null;
    public UserRole? Role => null;
    public bool IsAdmin => false;
    public void SetCurrentUser(User? user) { }
}
