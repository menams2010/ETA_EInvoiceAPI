using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Register cookie authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Home/Login";
        options.LogoutPath = "/Home/Logout";
    });

builder.Services.AddSession();

var app = builder.Build();

app.UseSession();

// Add authentication/authorization middleware
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultControllerRoute();

app.Run();