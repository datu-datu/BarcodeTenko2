using System;
using System.IO;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
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

// 秘密情報の未設定は起動時に即座に失敗させる（安全な既定値は存在しない）
var tenkoOptions = builder.Configuration.GetSection(TenkoServerOptions.SectionName).Get<TenkoServerOptions>() ?? new TenkoServerOptions();
if (string.IsNullOrWhiteSpace(tenkoOptions.AdminPassword))
{
    throw new InvalidOperationException(
        "AdminPassword is not configured. Set 'TenkoServer:AdminPassword' in appsettings.json or the environment variable TenkoServer__AdminPassword.");
}
if (string.IsNullOrWhiteSpace(tenkoOptions.ApiKey))
{
    throw new InvalidOperationException(
        "ApiKey is not configured. Set 'TenkoServer:ApiKey' in appsettings.json or the environment variable TenkoServer__ApiKey.");
}

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
builder.Services.AddSingleton<INotificationStateService, NotificationStateService>();
builder.Services.AddSingleton<IScanAcceptanceService, ScanAcceptanceService>();
builder.Services.AddHostedService<NotificationBackgroundService>();
builder.Services.AddHttpClient("PowerAutomateClient");

// リバースプロキシ (Caddy) 経由のアクセスでも実クライアント IP を取得できるようにする
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// レートリミット (管理画面ログインの総当たり攻撃対策)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"success\":false,\"message\":\"ログイン試行が多すぎます。しばらく待ってから再試行してください。\"}",
            cancellationToken);
    };
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

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

    // EnsureCreated は既存 DB のスキーマを更新しないため、旧 DB への冪等な移行を実行する
    DatabaseMigrator.Migrate(db);
}

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// X-API-Key 認証ミドルウェア (端末用 API)
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// ルートアクセス時は index.html にフォールバック
app.MapFallbackToFile("index.html");

app.Run();
