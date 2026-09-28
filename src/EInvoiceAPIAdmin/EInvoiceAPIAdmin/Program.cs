using InvoiceWebApp.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using LiteDB;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

//var MyAllowSpecificOrigins = "_myAllowSpecificOrigins";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddControllers(); // This registers controllers
builder.Services.AddRazorPages();
builder.Services.AddScoped<AccountCookieEvents>();
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
builder.Services.AddHttpsRedirection(options => options.HttpsPort = builder.Configuration.GetValue<int?>("HttpsPort") ?? 443);
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(180));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// Register LiteDB
var databaseSecurity = new DatabaseSecurity(builder.Configuration);
if (builder.Configuration.GetValue<bool>("Database:Migration:Enabled"))
{
    try
    {
        var result = databaseSecurity.MigrateLegacy(builder.Configuration);
        Console.WriteLine($"{result.Format} migration validated. Destination: {result.DestinationPath}");
        Console.WriteLine($"Source snapshot retained at: {result.BackupPath}");
        foreach (var collection in result.RecordCounts)
            Console.WriteLine($"{collection.Key}: {collection.Value} records");
        if (result.RequiresDataProtectionKeys)
            Console.WriteLine("Graph payloads were preserved, not authenticated. Preserve the matching Data Protection key ring and verify the connections before enabling mail.");
        Console.WriteLine("Remove Database:Migration settings, retain Database:Path and Database:Password, then restart to switch to the validated database.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Offline migration failed ({ex.GetType().Name}). Do not start the application against the destination. Review the source format/key, schema, permissions, and migration artifacts; see docs/security-hardening.md.");
        Environment.ExitCode = 1;
    }
    return;
}
if (builder.Configuration.GetValue<bool>("Database:RotateKey"))
{
    databaseSecurity.RotateKey(builder.Configuration);
    Console.WriteLine("Database key rotation completed. Secure the recovery backup and remove the previous key and rotation setting before starting the app.");
    return;
}
builder.Services.AddSingleton(databaseSecurity);
builder.Services.AddSingleton<LiteDatabase>(_ => databaseSecurity.Open());
builder.Services.AddScoped<AppDbContext>(sp =>
{
    var liteDb = sp.GetRequiredService<LiteDatabase>();
    return new AppDbContext(liteDb);
});

builder.Services.AddSingleton<SmtpEmailSender>();
builder.Services.AddDataProtection().SetApplicationName("InvoiceWebApp");
builder.Services.AddHttpClient(GraphMailService.AuthenticationScheme);
builder.Services.AddSingleton<GraphAppRegistrationStore>();
builder.Services.AddSingleton<GraphMailService>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddScoped<AlertRunner>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddHostedService<AlertBackgroundService>();

// Enable session
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
});

//builder.Services.AddCors(options =>
//{
//    options.AddPolicy(name: MyAllowSpecificOrigins,
//        policy =>
//        {
//            policy.WithOrigins("http://localhost:5000") // Add your frontend URL here
//                  .AllowAnyHeader()
//                  .AllowAnyMethod();
//        });
//});

var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Home/Login";
        options.LogoutPath = "/Home/Logout";
        options.Cookie.Name = "__Host-InvoiceAdmin";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
        options.EventsType = typeof(AccountCookieEvents);
    });

    authentication.AddOAuth<Microsoft.AspNetCore.Authentication.OAuth.OAuthOptions, ProfileGraphOAuthHandler>(GraphMailService.AuthenticationScheme, options =>
    {
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // Required by options validation; the custom handler resolves real credentials per profile.
        options.ClientId = "per-profile";
        options.ClientSecret = "per-profile";
        options.CallbackPath = "/signin-microsoft-mail";
        options.AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
        options.TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
        options.UsePkce = true;
        options.Scope.Clear();
        foreach (var scope in GraphMailService.Scopes.Split(' '))
            options.Scope.Add(scope);
        options.Events.OnRedirectToAuthorizationEndpoint = context =>
        {
            context.Response.Redirect(context.RedirectUri + "&prompt=select_account");
            return Task.CompletedTask;
        };
        options.Events.OnCreatingTicket = async context =>
        {
            var local = await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!local.Succeeded || string.IsNullOrWhiteSpace(local.Principal?.Identity?.Name)
                || !context.Properties.Items.TryGetValue("LocalUser", out var user)
                || local.Principal.Identity.Name != user
                || !context.Properties.Items.TryGetValue("ProfileId", out var rawId)
                || !int.TryParse(rawId, out var profileId)
                || string.IsNullOrWhiteSpace(context.AccessToken)
                || string.IsNullOrWhiteSpace(context.RefreshToken))
                throw new InvalidOperationException("The Microsoft connection could not be completed. Sign in locally and reconnect.");

            var graph = context.HttpContext.RequestServices.GetRequiredService<GraphMailService>();
            if (!context.Properties.Items.TryGetValue("RegistrationVersion", out var registrationVersion)
                || string.IsNullOrEmpty(registrationVersion))
                throw new InvalidOperationException("Missing registration version. Reconnect the Microsoft account.");
            await graph.ConnectAsync(profileId, context.AccessToken, context.RefreshToken, context.HttpContext.RequestAborted, registrationVersion);
        };
        options.Events.OnTicketReceived = context =>
        {
            // Keep the local login cookie; this flow only connects a mail account.
            context.HandleResponse();
            context.Response.Redirect("/SmtpProfiles/Index?message=Microsoft%20mailbox%20connected.");
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect("/SmtpProfiles/Index?error=Microsoft%20connection%20failed.%20Check%20app%20registration%2C%20consent%2C%20and%20local%20login%2C%20then%20reconnect.");
            return Task.CompletedTask;
        };
    });

var app = builder.Build();
DatabaseSecurity.InitializeAccounts(app.Services.GetRequiredService<LiteDatabase>(), builder.Configuration);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseSession(); // Add session middleware!
app.UseAuthentication(); // Add this before app.UseAuthorization!
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers(); // This enables attribute routing for controllers

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Login}/{id?}");

app.Run();
