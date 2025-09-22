using InvoiceWebApp.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using LiteDB;

//var MyAllowSpecificOrigins = "_myAllowSpecificOrigins";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddControllers(); // This registers controllers
builder.Services.AddRazorPages();

// Register LiteDB
var dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config.db"));
builder.Services.AddSingleton<LiteDatabase>(_ => new LiteDatabase($"Filename={dbPath};Password=@Mina&29012017&Mariam@;Connection=shared"));
builder.Services.AddScoped<AppDbContext>(sp =>
{
    var liteDb = sp.GetRequiredService<LiteDatabase>();
    return new AppDbContext(liteDb);
});

// Enable session
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Home/Login";
        options.LogoutPath = "/Home/Logout";
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseStaticFiles();
app.UseRouting();
app.UseSession(); // Add session middleware!
app.UseAuthentication(); // Add this before app.UseAuthorization!
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers(); // This enables attribute routing for controllers

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Login}/{id?}");

app.Run();
