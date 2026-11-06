using IncomeTaxWebAPI.Security;

// ASP.NET Core host for IncomeTaxWebAPI (ported from ASP.NET Web API 2 / Global.asax).
// Routes are defined with [Route] attributes on the controllers, exactly as before.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

// Under IIS the application path (/EInvoiceAPI/ETANotifications) is applied automatically. ASPNETCORE_PATHBASE
// reproduces it when running outside IIS (Kestrel / Visual Studio) so URLs match production.
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);

// Replaces the Web API 2 ApiKeyHandler message handler: every request needs the API key.
app.UseMiddleware<ApiKeyMiddleware>();

app.MapControllers();

app.Run();
