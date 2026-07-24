using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Services;

public sealed class UserSessionContext : IUserSessionContext
{
    private readonly object _gate = new();
    private int? _userId;
    private int? _storeId;
    private UserRole? _role;

    public int? UserId { get { lock (_gate) return _userId; } }
    public int? StoreId { get { lock (_gate) return _storeId; } }
    public UserRole? Role { get { lock (_gate) return _role; } }
    public bool IsAdmin => Role == UserRole.Admin;

    public void SetCurrentUser(User? user)
    {
        lock (_gate)
        {
            _userId = user?.Id;
            _storeId = user?.StoreId;
            _role = user?.Role;
        }
    }
}
