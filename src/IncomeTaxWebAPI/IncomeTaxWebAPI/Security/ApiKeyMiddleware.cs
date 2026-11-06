using System.Security.Cryptography;
using System.Text;
using IncomeTaxWebAPI.Services;

namespace IncomeTaxWebAPI.Security
{
    /// <summary>
    /// Rejects every request whose Authorization header is not <c>ApiKey &lt;key&gt;</c>, where the key is the one set in
    /// EInvoiceAPIAdmin (Admin > ETA Notifications), or by the installer. The key is read from config.db on every
    /// request, so changing it in Admin applies immediately. With no key set, every request is rejected.
    /// Ported from the Web API 2 ApiKeyHandler; the 401 response body is unchanged (a JSON string).
    /// </summary>
    public class ApiKeyMiddleware
    {
        private const string Scheme = "ApiKey ";

        private readonly RequestDelegate _next;
        private readonly ILogger<ApiKeyMiddleware> _logger;

        public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, SapEndpointStore settings)
        {
            var expectedKey = settings.GetApiKey();
            if (expectedKey == null)
                _logger.LogWarning("No ETA notifications API key is set; rejecting the request. Set it in Admin > ETA Notifications.");

            var header = context.Request.Headers.Authorization.FirstOrDefault();
            if (expectedKey == null || !Matches(header, expectedKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("\"Bad API Key\"");
                return;
            }

            await _next(context);
        }

        private static bool Matches(string header, string expectedKey)
        {
            if (header == null || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            var supplied = Encoding.UTF8.GetBytes(header.Substring(Scheme.Length).Trim());
            var expected = Encoding.UTF8.GetBytes(expectedKey);
            // Constant-time comparison, so response timing does not reveal how much of the key matched.
            return CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
    }
}
