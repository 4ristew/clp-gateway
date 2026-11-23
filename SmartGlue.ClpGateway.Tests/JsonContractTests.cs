using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Extensions;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// O desktop lê este gateway e o `smt_glue` com o mesmo conversor, e um enum serializado como
/// número não quebra compilação em lugar nenhum — são processos separados. O sintoma aparece
/// só na tela: `GET /axis/status` devolvendo `0` faz o desktop cair no ramo "status
/// desconhecido" e pintar "Falha de comunicação" com a máquina conectada e respondendo.
/// </summary>
public class JsonContractTests
{
  private static JsonSerializerOptions ConfiguredOptions()
  {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddApiConfiguration(new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["JwtSettings:Secret"] = "TestKeyForJWTTokensLongEnoughForHmacSha256_0123456789",
        ["JwtSettings:Issuer"] = "AuthentifierProviderAPI",
        ["JwtSettings:Audience"] = "AuthentifierProviderClients",
      })
      .Build());

    return services.BuildServiceProvider()
      .GetRequiredService<IOptions<JsonOptions>>()
      .Value.SerializerOptions;
  }

  [Fact]
  public void AxisStatus_IsSerializedAsText_NotAsItsOrdinal()
  {
    var json = JsonSerializer.Serialize(
      new AxisStatusResponse(AxisStatus.Ready, 120m, 50m),
      ConfiguredOptions());

    Assert.Contains("\"Ready\"", json);
    Assert.DoesNotContain("\"status\":0", json);
  }

  [Theory]
  [InlineData(AxisStatus.Moving, "Moving")]
  [InlineData(AxisStatus.Fault, "Fault")]
  [InlineData(AxisStatus.CommunicationFailure, "CommunicationFailure")]
  public void EveryAxisStatus_MatchesTheNameTheDesktopParses(AxisStatus status, string expected)
  {
    var json = JsonSerializer.Serialize(new AxisStatusResponse(status, 0m, 0m), ConfiguredOptions());

    Assert.Contains($"\"{expected}\"", json);
  }
}
