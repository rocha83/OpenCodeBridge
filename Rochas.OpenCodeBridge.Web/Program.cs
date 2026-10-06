using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

var options = new WebApplicationOptions
{
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    ContentRootPath = AppContext.BaseDirectory
};
var builder = WebApplication.CreateBuilder(options);
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.Configure<MockBridgeOptions>(builder.Configuration.GetSection("MockBridge"));
builder.Services.AddScoped<IBridgeClient>(sp =>
{
    var config = sp.GetRequiredService<IOptions<MockBridgeOptions>>().Value;
    return config.UseMockBridge
        ? sp.GetRequiredService<MockBridgeClient>()
        : sp.GetRequiredService<BridgeClient>();
});
builder.Services.AddScoped<BridgeClient>();
builder.Services.AddScoped<MockBridgeClient>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped(_ => new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped(_ => new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped(_ => new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped(_ => new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/Account/Login"; o.Cookie.Name = "bridge.web"; });
builder.Services.AddAuthorization();

var app = builder.Build();
AppDb.Init(Path.Combine(AppContext.BaseDirectory, "web.db"));

// Seed admin (sempre confere, nunca duplica).
using (var scope = app.Services.CreateScope())
{
    var users = scope.ServiceProvider.GetRequiredService<GenericRepository<User>>();
    var existing = await users.Query(new User { Email = "admin@mova.com" });
    if (!existing.Any())
        await users.Add(new User { Name = "Admin", Email = "admin@mova.com", PasswordHash = PasswordHasher.Hash("Admin@123"), IsAdmin = true });
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Chat}/{action=Index}/{id?}");
app.Run();
