using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;
using Serilog;
using Serilog.Events;

var options = new WebApplicationOptions
{
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    ContentRootPath = AppContext.BaseDirectory
};
var builder = WebApplication.CreateBuilder(options);
// Porta via appsettings Web:Port (default 4130) ou env BRIDGE_WEB_PORT.
// 4150 é reservado ao Caddy (Grafana); a Web não binda nele.
int webPort = builder.Configuration.GetValue<int?>("Web:Port") ?? 4130;
if (int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_WEB_PORT"), out int envPort) && envPort > 0)
    webPort = envPort;
builder.WebHost.UseUrls($"http://*:{webPort}");

// Diagnóstico (appsettings Diagnostics + env DIAGNOSTICS_ENABLED=1).
// O env entra na configuração para valer em IOptions, Serilog e middleware.
if (Environment.GetEnvironmentVariable("DIAGNOSTICS_ENABLED") is "1" or "true" or "True")
    builder.Configuration["Diagnostics:Enabled"] = "true";
var diagOptions = builder.Configuration.GetSection("Diagnostics").Get<DiagnosticOptions>() ?? new DiagnosticOptions();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(diagOptions.Enabled ? LogEventLevel.Debug : LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.SQLite(
        sqliteDbPath: !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRIDGE_WEB_DB_DIR"))
            ? Path.Combine(Environment.GetEnvironmentVariable("BRIDGE_WEB_DB_DIR")!, "diagnostics.db")
            : Path.Combine(AppContext.BaseDirectory, "diagnostics.db"),
        tableName: string.IsNullOrWhiteSpace(diagOptions.SqliteTable) ? "logs" : diagOptions.SqliteTable,
        storeTimestampInUtc: true)
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.Configure<BridgeAuthOptions>(builder.Configuration.GetSection("BridgeAuth"));
builder.Services.AddSingleton<BridgeAuthService>();
builder.Services.AddTransient<BridgeAuthHandler>();
builder.Services.AddHttpClient("bridge").AddHttpMessageHandler<BridgeAuthHandler>();
builder.Services.Configure<MockBridgeOptions>(builder.Configuration.GetSection("MockBridge"));
builder.Services.Configure<DiagnosticOptions>(builder.Configuration.GetSection("Diagnostics"));
builder.Services.Configure<OrchestrationOptions>(builder.Configuration.GetSection("Orchestration"));
builder.Services.Configure<ContextWindowOptions>(builder.Configuration.GetSection("ContextWindow"));
// Aplica o orçamento de contexto (CPU 16k slot único por padrão) ao estático.
var ctxOpts = builder.Configuration.GetSection("ContextWindow").Get<ContextWindowOptions>() ?? new ContextWindowOptions();
ContextWindow.Configure(ctxOpts.CpuContextTokens, ctxOpts.CpuMaxOutputTokens, ctxOpts.CpuMaxInputTokens, ctxOpts.GpuMaxInputTokens);
builder.Services.AddSingleton<IDiagnosticTelemetry, DiagnosticTelemetry>();
builder.Services.AddScoped<IBridgeClient>(sp =>
{
    var config = sp.GetRequiredService<IOptions<MockBridgeOptions>>().Value;
    return config.UseMockBridge
        ? sp.GetRequiredService<MockBridgeClient>()
        : sp.GetRequiredService<BridgeClient>();
});
builder.Services.AddScoped<BridgeClient>();
builder.Services.AddScoped<MockBridgeClient>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasherService>();
builder.Services.AddScoped<IToolHandler, ShellToolHandler>();
builder.Services.AddScoped<IToolHandler, ReadToolHandler>();
builder.Services.AddScoped<IToolHandler, WriteToolHandler>();
builder.Services.AddScoped<IToolHandler, EditToolHandler>();
builder.Services.AddScoped<IToolHandler, GrepToolHandler>();
builder.Services.AddScoped<IToolHandler, GlobToolHandler>();
builder.Services.AddScoped<IToolHandler, FetchToolHandler>();
builder.Services.AddScoped<IToolHandler, GitToolHandler>();
builder.Services.AddScoped<IToolHandler>(sp => new SubagentToolHandler(sp.GetRequiredService<IBridgeClient>()));
builder.Services.AddScoped<IToolExecutor>(sp => new ToolExecutor(
    sp.GetServices<IToolHandler>(),
    repoPath: AppContext.BaseDirectory,
    logPath: Path.Combine(AppContext.BaseDirectory, "tool-executor.log"),
    telemetry: sp.GetRequiredService<IDiagnosticTelemetry>()));
builder.Services.AddScoped<IGenericRepository<User>>(sp => sp.GetRequiredService<GenericRepository<User>>());
builder.Services.AddScoped<IPersistenceRepository<User>>(sp => sp.GetRequiredService<GenericRepository<User>>());
builder.Services.AddScoped<IGenericRepository<Agent>>(sp => sp.GetRequiredService<GenericRepository<Agent>>());
builder.Services.AddScoped<IPersistenceRepository<Agent>>(sp => sp.GetRequiredService<GenericRepository<Agent>>());
builder.Services.AddScoped<IGenericRepository<Session>>(sp => sp.GetRequiredService<GenericRepository<Session>>());
builder.Services.AddScoped<IPersistenceRepository<Session>>(sp => sp.GetRequiredService<GenericRepository<Session>>());
builder.Services.AddScoped<IGenericRepository<SessionMessage>>(sp => sp.GetRequiredService<GenericRepository<SessionMessage>>());
builder.Services.AddScoped<IPersistenceRepository<SessionMessage>>(sp => sp.GetRequiredService<GenericRepository<SessionMessage>>());
builder.Services.AddScoped(_ => new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped(_ => new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped<IGenericRepository<PromptControl>>(sp => sp.GetRequiredService<GenericRepository<PromptControl>>());
builder.Services.AddScoped<IPersistenceRepository<PromptControl>>(sp => sp.GetRequiredService<GenericRepository<PromptControl>>());
builder.Services.AddScoped(_ => new GenericRepository<PromptControl>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped<PromptControlService>();
builder.Services.AddScoped(_ => new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped(_ => new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddScoped<IGenericRepository<RefinementLesson>>(sp => sp.GetRequiredService<GenericRepository<RefinementLesson>>());
builder.Services.AddScoped<IPersistenceRepository<RefinementLesson>>(sp => sp.GetRequiredService<GenericRepository<RefinementLesson>>());
builder.Services.AddScoped(_ => new GenericRepository<RefinementLesson>(DatabaseEngine.SQLite, AppDb.ConnectionString));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/Account/Login"; o.Cookie.Name = "bridge.web"; });
builder.Services.AddAuthorization();

var app = builder.Build();
// Banco fora do disco externo (blocos defeituosos -> SQLite I/O error):
// BRIDGE_WEB_DB_DIR aponta p/ disco local; padrão segue no bin.
var localDbDir = Environment.GetEnvironmentVariable("BRIDGE_WEB_DB_DIR");
AppDb.Init(!string.IsNullOrWhiteSpace(localDbDir)
    ? Path.Combine(localDbDir, "web.db")
    : Path.Combine(AppContext.BaseDirectory, "web.db"));

// Seed admin (sempre confere, nunca duplica).
using (var scope = app.Services.CreateScope())
{
    var users = scope.ServiceProvider.GetRequiredService<IGenericRepository<User>>();
    var passwords = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var existing = await users.Query(new User { Email = "admin@bridge.pcss.com.br" });
    if (!existing.Any())
        await users.Add(new User { Name = "Admin", Email = "admin@bridge.pcss.com.br", PasswordHash = passwords.Hash("123"), IsAdmin = true, Active = true });
    // Usuário isolado da bateria de integração (sessões de teste nunca na UI do admin).
    var etest = await users.Query(new User { Email = "teste@e2e.local" });
    if (!etest.Any())
        await users.Add(new User { Name = "E2E", Email = "teste@e2e.local", PasswordHash = passwords.Hash("Teste@123"), IsAdmin = false, Active = true });
}

app.UseStaticFiles();
if (diagOptions.Enabled) app.UseMiddleware<DiagnosticMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
// Cookie de usuário apagado/desativado (ex.: era login-livre): desloga e
// manda ao login em vez de servir listas vazias e confusas.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true
        && (ctx.Request.Path.StartsWithSegments("/Chat") || ctx.Request.Path.StartsWithSegments("/Agents") || ctx.Request.Path.StartsWithSegments("/Users")))
    {
        bool ok = false;
        try
        {
            var users = ctx.RequestServices.GetRequiredService<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.OpenCodeBridge.Web.Models.User>>();
            if (int.TryParse(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out int uid) && uid > 0)
            {
                var u = await users.Get(new Rochas.OpenCodeBridge.Web.Models.User { Id = uid });
                ok = u is not null && u.Active;
            }
        }
        catch { ok = false; }
        if (!ok)
        {
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(ctx);
            ctx.Response.StatusCode = 401;
            return;
        }
    }
    await next();
});
app.MapControllerRoute("default", "{controller=Chat}/{action=Index}/{id?}");
app.MapControllers();
app.Run();
