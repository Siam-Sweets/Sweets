using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PosApp.Core.Models;

namespace PosApp.Services;

public sealed class CloudApiClient : IDisposable
{
    private readonly CloudSessionManager _session;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public CloudApiClient(CloudSessionManager session)
    {
        _session = session;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PosApp/{CloudProtocol.ClientVersion}");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<T> PostAnonymousAsync<T>(
        string baseUrl,
        string path,
        object body,
        CancellationToken cancellationToken = default)
        => SendCoreAsync<T>(HttpMethod.Post, BuildUri(baseUrl, path), body, null, false, cancellationToken);

    public Task<T> GetAuthorizedAsync<T>(string path, CancellationToken cancellationToken = default)
        => SendAuthorizedAsync<T>(HttpMethod.Get, path, null, cancellationToken);

    public Task<T> PostAuthorizedAsync<T>(string path, object body, CancellationToken cancellationToken = default)
        => SendAuthorizedAsync<T>(HttpMethod.Post, path, body, cancellationToken);

    public Task<T> PatchAuthorizedAsync<T>(string path, object body, CancellationToken cancellationToken = default)
        => SendAuthorizedAsync<T>(HttpMethod.Patch, path, body, cancellationToken);

    public Task<T> DeleteAuthorizedAsync<T>(string path, CancellationToken cancellationToken = default)
        => SendAuthorizedAsync<T>(HttpMethod.Delete, path, null, cancellationToken);

    private async Task<T> SendAuthorizedAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var account = _session.Account ?? throw new CloudApiException(
            "AUTH_REQUIRED", "Sign in to the online account first.", HttpStatusCode.Unauthorized);
        var tokens = _session.Tokens ?? throw new CloudApiException(
            "AUTH_REQUIRED", "Sign in to the online account first.", HttpStatusCode.Unauthorized);

        if (tokens.AccessTokenExpiresAtUtc <= DateTime.UtcNow.AddSeconds(45))
        {
            await RefreshTokensAsync(cancellationToken);
            tokens = _session.Tokens!;
        }

        try
        {
            return await SendCoreAsync<T>(method, BuildUri(account.ApiBaseUrl, path), body,
                tokens.AccessToken, false, cancellationToken);
        }
        catch (CloudApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized &&
                                                  exception.Code is "ACCESS_TOKEN_EXPIRED" or "INVALID_ACCESS_TOKEN")
        {
            // The server can reject a token before the client's local clock says
            // it is near expiry. Pass the attempted token so one waiter refreshes
            // while concurrent requests reuse that newly rotated token.
            await RefreshTokensAsync(cancellationToken, tokens.AccessToken);
            return await SendCoreAsync<T>(method, BuildUri(account.ApiBaseUrl, path), body,
                _session.Tokens!.AccessToken, false, cancellationToken);
        }
        catch (CloudApiException exception) when (IsTerminalSessionError(exception.Code))
        {
            await _session.InvalidateAsync(exception.Code, cancellationToken);
            throw;
        }
    }

    private async Task RefreshTokensAsync(
        CancellationToken cancellationToken,
        string? rejectedAccessToken = null)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var account = _session.Account ?? throw new CloudApiException(
                "AUTH_REQUIRED", "Sign in to the online account first.", HttpStatusCode.Unauthorized);
            var current = _session.Tokens ?? throw new CloudApiException(
                "AUTH_REQUIRED", "Sign in to the online account first.", HttpStatusCode.Unauthorized);
            if (rejectedAccessToken == null)
            {
                if (current.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddSeconds(45)) return;
            }
            else if (!string.Equals(current.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
            {
                return;
            }

            var response = await SendCoreAsync<RefreshEnvelope>(
                HttpMethod.Post,
                BuildUri(account.ApiBaseUrl, "/api/v1/auth/refresh"),
                new
                {
                    refreshToken = current.RefreshToken,
                    sessionId = current.SessionId,
                    deviceId = account.DeviceId
                },
                null,
                false,
                cancellationToken);
            await _session.UpdateTokensAsync(response.Tokens, cancellationToken);
        }
        catch (CloudApiException exception) when (IsTerminalSessionError(exception.Code))
        {
            await _session.InvalidateAsync(exception.Code, cancellationToken);
            throw;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<T> SendCoreAsync<T>(
        HttpMethod method,
        Uri uri,
        object? body,
        string? accessToken,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Request-ID", Guid.NewGuid().ToString("D"));
        request.Headers.TryAddWithoutValidation("X-PosApp-Client-Version", CloudProtocol.ClientVersion);
        request.Headers.TryAddWithoutValidation("X-PosApp-Schema-Version", CloudProtocol.ClientSchemaVersion.ToString());
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body != null)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(body, _json);
            if (json.Length >= 16 * 1024)
            {
                using var output = new MemoryStream();
                await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
                    await gzip.WriteAsync(json, cancellationToken);
                request.Content = new ByteArrayContent(output.ToArray());
                request.Content.Headers.ContentEncoding.Add("gzip");
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            else
            {
                request.Content = new ByteArrayContent(json);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloudApiException("NETWORK_TIMEOUT", "The online service did not respond in time.",
                HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            throw new CloudApiException("NETWORK_UNAVAILABLE", "The online service is currently unreachable.",
                HttpStatusCode.ServiceUnavailable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await CreateApiExceptionAsync(response, cancellationToken);
            if (allowEmpty || response.StatusCode == HttpStatusCode.NoContent)
                return default!;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken)
                       ?? throw new JsonException("The response body was empty.");
            }
            catch (JsonException)
            {
                throw new CloudApiException("INVALID_SERVER_RESPONSE",
                    "The online service returned an invalid response.", HttpStatusCode.BadGateway);
            }
        }
    }

    private async Task<CloudApiException> CreateApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>(_json, cancellationToken);
            if (envelope?.Error != null && !string.IsNullOrWhiteSpace(envelope.Error.Code))
                return new CloudApiException(envelope.Error.Code, envelope.Error.Message,
                    response.StatusCode, envelope.RequestId);
        }
        catch (JsonException)
        {
            // Never expose a proxy page or a raw database error to the desktop UI.
        }
        return new CloudApiException("REMOTE_SERVICE_ERROR",
            "The online service could not complete the request.", response.StatusCode);
    }

    public static Uri BuildUri(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root))
            throw new ArgumentException("Enter a valid Cloudflare Worker address.", nameof(baseUrl));
        if (root.Scheme != Uri.UriSchemeHttps &&
            !(root.Scheme == Uri.UriSchemeHttp && root.IsLoopback))
            throw new ArgumentException("The online service address must use HTTPS.", nameof(baseUrl));
        return new Uri(root, path.TrimStart('/'));
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }

    private static bool IsTerminalSessionError(string code) => code is
        "SESSION_REVOKED" or "REFRESH_TOKEN_EXPIRED" or "REFRESH_TOKEN_REVOKED" or
        "REFRESH_TOKEN_REUSE" or "DEVICE_REVOKED" or "USER_DISABLED" or
        "ORGANIZATION_DISABLED" or "STORE_DISABLED";

    private sealed class RefreshEnvelope
    {
        public CloudAuthTokens Tokens { get; set; } = new();
    }
}
