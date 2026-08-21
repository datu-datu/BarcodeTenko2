using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenkoServer.Data;
using TenkoServer.Middleware;
using TenkoServer.Models;
using TenkoServer.Services;

var builder = WebApplication.CreateBuilder(args);

// 設定のバインド
builder.Services.Configure<TenkoServerOptions>(
    builder.Configuration.GetSection(TenkoServerOptions.SectionName));

// データベース (SQLite)
string dbPath = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=data/tenko_server.db";

// data フォルダを確保
string? dbDir = Path.GetDirectoryName(dbPath.Replace("Data Source=", "").Trim());
if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
{
    Directory.CreateDirectory(dbDir);
}

builder.Services.AddDbContext<TenkoDbContext>(options =>
{
    options.UseSqlite(dbPath);
});

// サービス登録
builder.Services.AddSingleton<IStudentMasterService, StudentMasterService>();
builder.Services.AddSingleton<INotificationQueue, NotificationQueue>();
builder.Services.AddHostedService<NotificationBackgroundService>();
builder.Services.AddHttpClient("PowerAutomateClient");

// Cookie 認証 (Web 管理画面用)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "TenkoServer_Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect("/login.html");
            }
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();

var app = builder.Build();

// 起動時に SQLite データベースを作成
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenkoDbContext>();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// X-API-Key 認証ミドルウェア (端末用 API)
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// ルートアクセス時は index.html にフォールバック
app.MapFallbackToFile("index.html");

app.Run();
