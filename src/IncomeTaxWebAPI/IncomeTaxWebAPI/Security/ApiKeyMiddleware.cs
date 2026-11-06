namespace IncomeTaxWebAPI.Security
{
    /// <summary>
    /// Rejects every request whose Authorization header is not the expected API key.
    /// Ported from the Web API 2 ApiKeyHandler; the 401 response body is unchanged (a JSON string).
    /// </summary>
    public class ApiKeyMiddleware
    {
        // TODO: move to configuration; this key was hard-coded in the original ApiKeyHandler.
        private const string ExpectedApiKey = "ApiKey FreshTax2022";

        private readonly RequestDelegate _next;

        public ApiKeyMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context)
        {
            var apiKeyClient = context.Request.Headers.Authorization.FirstOrDefault();
            if (apiKeyClient != ExpectedApiKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("\"Bad API Key\"");
                return;
            }

            await _next(context);
        }
    }
}
