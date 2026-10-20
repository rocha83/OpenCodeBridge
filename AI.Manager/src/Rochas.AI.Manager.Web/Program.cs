using Microsoft.AspNetCore.Authentication.Cookies;
using Rochas.AI.Manager.Domain.Interfaces;
using Serilog;
using Serilog.Events;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.AI.Manager.TrainingEngine;
using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Web.Data;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Datasets;
using Rochas.AI.Manager.Domain.Experiments;
using Rochas.AI.Manager.Domain.Harness;
using Rochas.AI.Manager.Domain.Entities.Management;
using Rochas.AI.Manager.Domain.Models;
using Rochas.AI.Manager.Domain.Profiles;
using Rochas.AI.Manager.Domain.Training;
using Rochas.AI.Manager.Domain.Entities;

var builder = WebApplication.CreateBuilder(args);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();


builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<IPasswordHasher, Rochas.AI.Manager.Infrastructure.Services.PasswordHasherService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Rochas.AI.Manager API",
        Version = "v1",
        Description = "Gerenciamento, automação e treinamento de modelos de IA (multi-modelo)."
    });
});

// Repositorios Dapper (SQLite), padrao da bridge.
builder.Services.AddScoped<IGenericRepository<ModelArtifact>>(sp => sp.GetRequiredService<GenericRepository<ModelArtifact>>());
builder.Services.AddScoped<IPersistenceRepository<ModelArtifact>>(sp => sp.GetRequiredService<GenericRepository<ModelArtifact>>());
builder.Services.AddScoped(_ => new GenericRepository<ModelArtifact>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<Dataset>>(sp => sp.GetRequiredService<GenericRepository<Dataset>>());
builder.Services.AddScoped<IPersistenceRepository<Dataset>>(sp => sp.GetRequiredService<GenericRepository<Dataset>>());
builder.Services.AddScoped(_ => new GenericRepository<Dataset>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<InstructionProfile>>(sp => sp.GetRequiredService<GenericRepository<InstructionProfile>>());
builder.Services.AddScoped<IPersistenceRepository<InstructionProfile>>(sp => sp.GetRequiredService<GenericRepository<InstructionProfile>>());
builder.Services.AddScoped(_ => new GenericRepository<InstructionProfile>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<TrainingPattern>>(sp => sp.GetRequiredService<GenericRepository<TrainingPattern>>());
builder.Services.AddScoped<IPersistenceRepository<TrainingPattern>>(sp => sp.GetRequiredService<GenericRepository<TrainingPattern>>());
builder.Services.AddScoped(_ => new GenericRepository<TrainingPattern>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<Experiment>>(sp => sp.GetRequiredService<GenericRepository<Experiment>>());
builder.Services.AddScoped<IPersistenceRepository<Experiment>>(sp => sp.GetRequiredService<GenericRepository<Experiment>>());
builder.Services.AddScoped(_ => new GenericRepository<Experiment>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<TrainingJob>>(sp => sp.GetRequiredService<GenericRepository<TrainingJob>>());
builder.Services.AddScoped<IPersistenceRepository<TrainingJob>>(sp => sp.GetRequiredService<GenericRepository<TrainingJob>>());
builder.Services.AddScoped(_ => new GenericRepository<TrainingJob>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<HarnessProfile>>(sp => sp.GetRequiredService<GenericRepository<HarnessProfile>>());
builder.Services.AddScoped<IPersistenceRepository<HarnessProfile>>(sp => sp.GetRequiredService<GenericRepository<HarnessProfile>>());
builder.Services.AddScoped(_ => new GenericRepository<HarnessProfile>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<HarnessProfileVersion>>(sp => sp.GetRequiredService<GenericRepository<HarnessProfileVersion>>());
builder.Services.AddScoped<IPersistenceRepository<HarnessProfileVersion>>(sp => sp.GetRequiredService<GenericRepository<HarnessProfileVersion>>());
builder.Services.AddScoped(_ => new GenericRepository<HarnessProfileVersion>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<HarnessAttachment>>(sp => sp.GetRequiredService<GenericRepository<HarnessAttachment>>());
builder.Services.AddScoped<IPersistenceRepository<HarnessAttachment>>(sp => sp.GetRequiredService<GenericRepository<HarnessAttachment>>());
builder.Services.AddScoped(_ => new GenericRepository<HarnessAttachment>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<ToolPolicy>>(sp => sp.GetRequiredService<GenericRepository<ToolPolicy>>());
builder.Services.AddScoped<IPersistenceRepository<ToolPolicy>>(sp => sp.GetRequiredService<GenericRepository<ToolPolicy>>());
builder.Services.AddScoped(_ => new GenericRepository<ToolPolicy>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<ModelBinding>>(sp => sp.GetRequiredService<GenericRepository<ModelBinding>>());
builder.Services.AddScoped<IPersistenceRepository<ModelBinding>>(sp => sp.GetRequiredService<GenericRepository<ModelBinding>>());
builder.Services.AddScoped(_ => new GenericRepository<ModelBinding>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<HarnessRun>>(sp => sp.GetRequiredService<GenericRepository<HarnessRun>>());
builder.Services.AddScoped<IPersistenceRepository<HarnessRun>>(sp => sp.GetRequiredService<GenericRepository<HarnessRun>>());
builder.Services.AddScoped(_ => new GenericRepository<HarnessRun>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<BridgeCapture>>(sp => sp.GetRequiredService<GenericRepository<BridgeCapture>>());
builder.Services.AddScoped<IPersistenceRepository<BridgeCapture>>(sp => sp.GetRequiredService<GenericRepository<BridgeCapture>>());
builder.Services.AddScoped(_ => new GenericRepository<BridgeCapture>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<EntityStatus>>(sp => sp.GetRequiredService<GenericRepository<EntityStatus>>());
builder.Services.AddScoped<IPersistenceRepository<EntityStatus>>(sp => sp.GetRequiredService<GenericRepository<EntityStatus>>());
builder.Services.AddScoped(_ => new GenericRepository<EntityStatus>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<WorkflowStep>>(sp => sp.GetRequiredService<GenericRepository<WorkflowStep>>());
builder.Services.AddScoped<IPersistenceRepository<WorkflowStep>>(sp => sp.GetRequiredService<GenericRepository<WorkflowStep>>());
builder.Services.AddScoped(_ => new GenericRepository<WorkflowStep>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<TrainingWorkflowCard>>(sp => sp.GetRequiredService<GenericRepository<TrainingWorkflowCard>>());
builder.Services.AddScoped<IPersistenceRepository<TrainingWorkflowCard>>(sp => sp.GetRequiredService<GenericRepository<TrainingWorkflowCard>>());
builder.Services.AddScoped(_ => new GenericRepository<TrainingWorkflowCard>(DatabaseEngine.SQLite, AppDb.ConnectionString));

// Users
builder.Services.AddScoped<IGenericRepository<User>>(sp => sp.GetRequiredService<GenericRepository<User>>());
builder.Services.AddScoped<IPersistenceRepository<User>>(sp => sp.GetRequiredService<GenericRepository<User>>());
builder.Services.AddScoped(_ => new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString));

// Agents
builder.Services.AddScoped<IGenericRepository<Agent>>(sp => sp.GetRequiredService<GenericRepository<Agent>>());
builder.Services.AddScoped<IPersistenceRepository<Agent>>(sp => sp.GetRequiredService<GenericRepository<Agent>>());
builder.Services.AddScoped(_ => new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString));

// Servicos de aplicacao (scoped por causa dos repositorios).
builder.Services.AddScoped<IHarnessProfileService>(sp => new HarnessProfileService(
    sp.GetRequiredService<IGenericRepository<HarnessProfile>>(),
    sp.GetRequiredService<IPersistenceRepository<HarnessProfile>>(),
    sp.GetRequiredService<IGenericRepository<HarnessProfileVersion>>(),
    sp.GetRequiredService<IPersistenceRepository<HarnessProfileVersion>>()));
builder.Services.AddScoped<ITrainingJobService>(sp => new TrainingJobService(sp.GetRequiredService<IGenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>>(), sp.GetRequiredService<IPersistenceRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>>()));
builder.Services.AddScoped<ITrainingBackend, TorchSharpTrainingBackend>();

builder.Services.AddScoped<ITrainingEngine, TrainingEngine>();
builder.Services.AddScoped<Rochas.AI.Manager.Domain.Interfaces.ITrainingRunner, Rochas.AI.Manager.TrainingEngine.TrainingRunner>();

builder.Services.AddScoped<IReportService, Rochas.AI.Manager.Infrastructure.Services.ReportService>();
builder.Services.AddScoped<IHarnessLinter, HarnessLinter>();
builder.Services.AddScoped<IAccuracyEvaluator, AccuracyEvaluator>();
builder.Services.AddScoped<IModelArtifactService, ModelArtifactService>();
builder.Services.AddScoped<IDatasetService, DatasetService>();
builder.Services.AddScoped<IInstructionProfileService, InstructionProfileService>();
builder.Services.AddScoped<ITrainingPatternService, TrainingPatternService>();
builder.Services.AddScoped<IExperimentService, ExperimentService>();
builder.Services.AddScoped<IHardwareService, HardwareService>();
builder.Services.AddScoped<IHarnessAttachmentService, HarnessAttachmentService>();
builder.Services.AddScoped<IToolPolicyService, ToolPolicyService>();
builder.Services.AddScoped<IModelBindingService, ModelBindingService>();
builder.Services.AddScoped<IHarnessRunService, HarnessRunService>();
builder.Services.AddScoped<IBridgeCaptureService, BridgeCaptureService>();
builder.Services.AddScoped<ITrainingWorkflowService>(sp => new TrainingWorkflowService(
    sp.GetRequiredService<IGenericRepository<TrainingWorkflowCard>>(),
    sp.GetRequiredService<IPersistenceRepository<TrainingWorkflowCard>>(),
    sp.GetRequiredService<IAccuracyEvaluator>()));
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAgentService, AgentService>();

var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    if (builder.Environment.IsDevelopment()) jwtSecret = "ai-manager-jwt-secret-change-me";
    else throw new InvalidOperationException("Jwt:Secret deve ser configurado fora de Development.");
}
var jwtKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/Account/Login"; o.Cookie.Name = "ai.manager"; })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = jwtKey
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Harness}/{action=Index}/{id?}");

Rochas.AI.Manager.Infrastructure.Data.AppDb.Init();
using (var scope = app.Services.CreateScope())
{
    var users = scope.ServiceProvider.GetRequiredService<IGenericRepository<User>>();
    var passwords = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var existing = await users.Query(new User { Email = "admin@ai.manager.local" });
    if (!existing.Any())
    {
        await users.Add(new User { Name = "Admin", Email = "admin@ai.manager.local", PasswordHash = passwords.Hash("admin"), IsAdmin = true, IsActive = true });
        await users.Add(new User { Name = "E2E", Email = "teste@e2e.local", PasswordHash = passwords.Hash("Teste@123"), IsAdmin = false, IsActive = true });
    }
}
using (var scope = app.Services.CreateScope())
{
    var profiles = scope.ServiceProvider.GetRequiredService<IHarnessProfileService>();
    var attachments = scope.ServiceProvider.GetRequiredService<IHarnessAttachmentService>();
    var linter = scope.ServiceProvider.GetRequiredService<IHarnessLinter>();
    await HarnessSeeder.SeedAsync(profiles, attachments, linter);
}
using (var scope = app.Services.CreateScope())
{
    var profiles = scope.ServiceProvider.GetRequiredService<IGenericRepository<HarnessProfile>>();
    var versions = scope.ServiceProvider.GetRequiredService<IGenericRepository<HarnessProfileVersion>>();
    var attachments = scope.ServiceProvider.GetRequiredService<IGenericRepository<HarnessAttachment>>();
    await HarnessScenarioSeeder.SeedAsync(profiles, versions, attachments);
}
using (var scope = app.Services.CreateScope())
{
    var statuses = scope.ServiceProvider.GetRequiredService<IGenericRepository<EntityStatus>>();
    var steps = scope.ServiceProvider.GetRequiredService<IGenericRepository<WorkflowStep>>();
    await WorkflowSeeder.SeedAsync(statuses, steps);
}
using (var scope = app.Services.CreateScope())
{
    var models = scope.ServiceProvider.GetRequiredService<IGenericRepository<ModelArtifact>>();
    var agents = scope.ServiceProvider.GetRequiredService<IGenericRepository<Agent>>();
    var bindings = scope.ServiceProvider.GetRequiredService<IGenericRepository<ModelBinding>>();
    var profiles = scope.ServiceProvider.GetRequiredService<IGenericRepository<HarnessProfile>>();
    var versions = scope.ServiceProvider.GetRequiredService<IGenericRepository<HarnessProfileVersion>>();
    var attachments = scope.ServiceProvider.GetRequiredService<IGenericRepository<HarnessAttachment>>();
    var policies = scope.ServiceProvider.GetRequiredService<IGenericRepository<ToolPolicy>>();
    var instructions = scope.ServiceProvider.GetRequiredService<IGenericRepository<InstructionProfile>>();
    var patterns = scope.ServiceProvider.GetRequiredService<IGenericRepository<TrainingPattern>>();
    var experiments = scope.ServiceProvider.GetRequiredService<IGenericRepository<Experiment>>();
    await BridgeFeatureSeeder.SeedAsync(models, agents, bindings, profiles, versions, attachments, policies, instructions, patterns, experiments);
}
using (var scope = app.Services.CreateScope())
{
    var instructions = scope.ServiceProvider.GetRequiredService<IGenericRepository<InstructionProfile>>();
    var patterns = scope.ServiceProvider.GetRequiredService<IGenericRepository<TrainingPattern>>();
    var experiments = scope.ServiceProvider.GetRequiredService<IGenericRepository<Experiment>>();
    var profiles = scope.ServiceProvider.GetRequiredService<IGenericRepository<HarnessProfile>>();
    var models = scope.ServiceProvider.GetRequiredService<IGenericRepository<ModelArtifact>>();
    await ScenarioTemplateSeeder.SeedAsync(instructions, patterns, experiments, profiles, models);
}
app.MapGet("/health", () =>
{
    var diag = Environment.GetEnvironmentVariable("DIAGNOSTICS_ENABLED") is "1" or "true" or "True";
    var payload = new Dictionary<string, object>
    {
        ["status"] = "ok",
        ["timestamp"] = DateTime.UtcNow,
        ["diagnostics"] = diag
    };
    if (diag)
    {
        payload["memory_mb"] = GC.GetTotalMemory(false) / (1024 * 1024);
        payload["uptime_s"] = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;
    }
    return Results.Ok(payload);
});
app.Run();
