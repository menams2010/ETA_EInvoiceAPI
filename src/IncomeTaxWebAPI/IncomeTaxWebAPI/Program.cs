using IncomeTaxWebAPI.Security;
using IncomeTaxWebAPI.Services;

// ASP.NET Core host for IncomeTaxWebAPI (ported from ASP.NET Web API 2 / Global.asax).
// Routes are defined with [Route] attributes on the controllers, exactly as before.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// SAP endpoints and the API key are managed in EInvoiceAPIAdmin and read from the shared config.db.
// Created at startup so missing Database:* settings fail fast instead of on the first notification.
builder.Services.AddSingleton<SapEndpointStore>();

var app = builder.Build();

// Folder for the notification SQLite logs (default C:\EInvoice).
var notificationsFolder = app.Configuration["Notifications:DataFolder"];
if (!string.IsNullOrWhiteSpace(notificationsFolder)) IncomeTaxWebAPI.DBManager.DataFolder = notificationsFolder;
_ = app.Services.GetRequiredService<SapEndpointStore>();

// Under IIS the application path (/EInvoiceAPI/ETANotifications) is applied automatically. ASPNETCORE_PATHBASE
// reproduces it when running outside IIS (Kestrel / Visual Studio) so URLs match production.
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);

// Replaces the Web API 2 ApiKeyHandler message handler: every request needs the API key.
app.UseMiddleware<ApiKeyMiddleware>();

app.MapControllers();

app.Run();
