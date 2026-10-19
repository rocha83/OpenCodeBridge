using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Application.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
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

builder.Services.AddSingleton<IHarnessProfileService, InMemoryHarnessProfileService>();
builder.Services.AddSingleton<ITrainingJobService, InMemoryTrainingJobService>();
builder.Services.AddSingleton<IHarnessLinter, HarnessLinter>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Harness}/{action=Index}/{id?}");

app.Run();
