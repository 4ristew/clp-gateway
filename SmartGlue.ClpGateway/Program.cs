using System.Text.Json;
using Carter;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using SmartGlue.ClpGateway.Common.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Runs as a Windows Service on the cabin PC and as a console app on a developer machine —
// the same binary, decided at runtime. Harmless when not hosted as a service.
builder.Host.UseWindowsService(options => options.ServiceName = "smartglue-clp-gateway");

builder.Services
    .AddApiConfiguration(builder.Configuration, builder.Environment.IsDevelopment())
    .AddBackendClient(builder.Configuration)
    .AddMachineLayer(builder.Configuration)
    .AddCarter();

var app = builder.Build();

// Same path base as smt_glue, so /api/axis/* keeps the exact shape it had in the backend and
// the desktop only has to change a host and a port.
app.UsePathBase("/api");
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions
{
  ResponseWriter = async (context, report) =>
  {
    context.Response.ContentType = "application/json";
    var response = new
    {
      status = report.Status.ToString(),
      checks = report.Entries.Select(e => new
      {
        name = e.Key,
        status = e.Value.Status.ToString(),
        description = e.Value.Description,
        data = e.Value.Data,
        duration = e.Value.Duration
      }),
      totalDuration = report.TotalDuration
    };
    await context.Response.WriteAsync(JsonSerializer.Serialize(response));
  }
});

app.MapCarter();

if (app.Environment.IsDevelopment())
{
  app.MapOpenApi();
  app.MapScalarApiReference("/scalar", options =>
  {
    options.WithTheme(ScalarTheme.DeepSpace).WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Axios);
    options.Title = "Smart Glue CLP Gateway";
  });
}

app.Run();

// Exposed so the test project can reference the assembly through WebApplicationFactory later,
// and so AddValidatorsFromAssemblyContaining<Program> has a type to anchor on.
public partial class Program;
