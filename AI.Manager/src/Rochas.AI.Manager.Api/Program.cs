using Rochas.AI.Manager.Domain.Interfaces;
using Serilog;
using Serilog.Events;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.AI.Manager.TrainingEngine;
using Rochas.AI.Manager.Application.Services;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Interfaces;
using Rochas.Data.Specification.Enums;
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


builder.Services.AddControllers();
builder.Services.AddHostedService<Rochas.AI.Manager.Api.Workers.TrainingJobWorker>();
builder.Services.AddSingleton<Rochas.AI.Manager.Application.Services.PermissionService>();
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    if (builder.Environment.IsDevelopment()) jwtSecret = "ai-manager-jwt-secret-change-me";
    else throw new InvalidOperationException("Jwt:Secret deve ser configurado fora de Development.");
}
var jwtKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret));
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
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
builder.Services.AddHttpClient();
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

// Repositorios Dapper (SQLite)

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

builder.Services.AddScoped<IGenericRepository<User>>(sp => sp.GetRequiredService<GenericRepository<User>>());
builder.Services.AddScoped<IPersistenceRepository<User>>(sp => sp.GetRequiredService<GenericRepository<User>>());
builder.Services.AddScoped(_ => new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString));

builder.Services.AddScoped<IGenericRepository<Agent>>(sp => sp.GetRequiredService<GenericRepository<Agent>>());
builder.Services.AddScoped<IPersistenceRepository<Agent>>(sp => sp.GetRequiredService<GenericRepository<Agent>>());
builder.Services.AddScoped(_ => new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString));

// Servicos
builder.Services.AddScoped<IHarnessProfileService>(sp => new HarnessProfileService(
    sp.GetRequiredService<IGenericRepository<HarnessProfile>>(),
    sp.GetRequiredService<IPersistenceRepository<HarnessProfile>>(),
    sp.GetRequiredService<IGenericRepository<HarnessProfileVersion>>(),
    sp.GetRequiredService<IPersistenceRepository<HarnessProfileVersion>>()));
builder.Services.AddScoped<ITrainingJobService>(sp => new TrainingJobService(sp.GetRequiredService<IGenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>>(), sp.GetRequiredService<IPersistenceRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>>()));
builder.Services.AddScoped<ITrainingBackend, TorchSharpTrainingBackend>();
builder.Services.AddSingleton<Rochas.AI.Manager.Application.Tools.BridgeToolExecutor>(sp => new Rochas.AI.Manager.Application.Tools.BridgeToolExecutor(Directory.GetCurrentDirectory()));
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// AppDb is in Web project; replicate init here
Rochas.AI.Manager.Infrastructure.Data.AppDb.Init();
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow })).AllowAnonymous();
app.Run();
