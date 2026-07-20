using System.Net;

namespace PosApp.Services;

public sealed class CloudApiException : Exception
{
    public CloudApiException(string code, string message, HttpStatusCode statusCode, string? requestId = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        RequestId = requestId;
    }

    public string Code { get; }
    public HttpStatusCode StatusCode { get; }
    public string? RequestId { get; }
}
