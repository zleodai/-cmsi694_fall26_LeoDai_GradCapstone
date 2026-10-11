using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Services;
using Microsoft.Data.Sqlite;
using PlaytestOps.Web.Bridge;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// Resolve the file against the content root so launching from another directory
// does not silently create a different database.
var sqlite = new SqliteConnectionStringBuilder(connectionString);
if (string.IsNullOrWhiteSpace(sqlite.DataSource) || sqlite.DataSource == ":memory:" || sqlite.Mode == SqliteOpenMode.Memory)
    throw new InvalidOperationException("Configure a persistent SQLite file for DefaultConnection.");
sqlite.DataSource = Path.GetFullPath(sqlite.DataSource, builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(sqlite.DataSource)!);
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(sqlite.ToString()));
builder.Services.AddScoped<IPlaytestService, PlaytestService>();
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddRazorPages();
builder.Services.AddSingleton<BridgeRegistry>();
builder.Services.AddSingleton<SourceService>();
builder.Services.AddSingleton<IProjectSourceProvider>(provider => provider.GetRequiredService<SourceService>());
builder.Services.AddSingleton<VersionControlService>();
builder.Services.AddSingleton<IProjectVersionControlProvider>(provider => provider.GetRequiredService<VersionControlService>());
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<RunService>();
builder.Services.AddSingleton<RunMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RunMonitor>());
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("pair", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Local Editor clients can use loopback HTTP without trusting a development certificate.
app.UseWhen(context => !(context.Request.Path.StartsWithSegments("/api/editor") &&
    context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)), branch => branch.UseHttpsRedirection());

app.UseRouting();

app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
app.MapEditorBridge();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
