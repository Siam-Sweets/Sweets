using PosApp.Core.Models;

namespace PosApp.Core.Interfaces;

public interface ICloudAccountService
{
    Task<CloudAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task TestConnectionAsync(string endpoint, CancellationToken cancellationToken = default);
    Task<CloudAccountStatus> SignUpAsync(
        CloudSignUpRequest request,
        CancellationToken cancellationToken = default);
    Task<CloudAccountStatus> SignInAsync(
        CloudSignInRequest request,
        CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<CloudSnapshotUploadSummary> UploadInitialSnapshotsAsync(
        CancellationToken cancellationToken = default);
}
