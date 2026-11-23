using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Trace;
using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using SmartGlue.ClpGateway.Backend;
using SmartGlue.ClpGateway.Common.Authentication;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Routines;
using SmartGlue.ClpGateway.Services;
using SmartGlue.ClpGateway.Vision;

namespace SmartGlue.ClpGateway.Common.Extensions;

public static class DependencyInjection
{
  // Everything here is a singleton. The gateway has no database, no per-request state and no
  // unit of work — it has one socket and one machine. Scoped lifetimes in the backend existed
  // to scope a DbContext; carrying them over would only mean rebuilding the same objects on
  // every request, and would have made the in-memory limits cache useless.
  public static IServiceCollection AddMachineLayer(this IServiceCollection services, IConfiguration configuration)
  {
    services.Configure<AxisHardwareOptions>(configuration.GetSection(AxisHardwareOptions.SectionName));
    services.Configure<ResultQueueOptions>(configuration.GetSection(ResultQueueOptions.SectionName));
    services.Configure<TraceOptions>(configuration.GetSection(TraceOptions.SectionName));

    // The trace is registered before the connection on purpose: the connection opens a trace
    // session the moment it connects, and a session that starts after the first frames would
    // leave the beginning of every run unrecorded.
    services.AddSingleton<IMachineTrace, JsonlTraceSink>();
    services.AddSingleton<TraceReader>();
    services.AddSingleton<MachineMetrics>();

    services.AddSingleton<ModbusAxisConnection>();

    // What the machine said it is (build, contract version) when the session opened. Written by
    // the heartbeat, read by every command and by the health check.
    services.AddSingleton<MachineIdentity>();

    // Everything above the wire gets the traced connection, never the raw one: a caller that
    // resolved IAxisConnection and skipped the decorator would move the machine without leaving
    // any record of it, which is the one thing the trace exists to prevent.
    services.AddSingleton<IAxisConnection>(sp => new TracingAxisConnection(
      sp.GetRequiredService<ModbusAxisConnection>(),
      sp.GetRequiredService<IMachineTrace>(),
      sp.GetRequiredService<IOptions<AxisHardwareOptions>>()));

    services.AddSingleton<IAxisLimitsProvider, AxisLimitsProvider>();
    services.AddSingleton<IAxisControlService, AxisControlService>();

    // The second station (SGF-141). Singleton for the same reason as the first — and for one
    // more: it holds the jog deadman, which has to outlive the request that started the jog.
    services.AddSingleton<ApplicationAxisService>();
    services.AddSingleton<IApplicationAxisService>(sp => sp.GetRequiredService<ApplicationAxisService>());

    services.AddSingleton<ResultQueue>();
    services.AddSingleton<IRoutineExecutor, RoutineExecutor>();

    services.AddCamera(configuration);

    // The watchdog is the reason this process exists as a daemon: it must hold the machine
    // from the moment it starts, not from the first HTTP request.
    services.AddHostedService<AxisHeartbeatService>();

    services.AddHealthChecks().AddCheck<GatewayHealthCheck>("clp-gateway");

    return services;
  }

  // The camera is the second wire to the machine, and the only other one this process owns.
  // It is HTTP, not Modbus, so it gets its own typed client instead of sharing the socket —
  // but it stays here, in the machine layer, because a frame without the axis position that
  // goes with it is not usable (see CameraService).
  private static IServiceCollection AddCamera(this IServiceCollection services, IConfiguration configuration)
  {
    var options = configuration.GetSection(VisionOptions.SectionName).Get<VisionOptions>() ?? new VisionOptions();
    services.Configure<VisionOptions>(configuration.GetSection(VisionOptions.SectionName));

    services.AddHttpClient<ICameraService, CameraService>(client =>
    {
      var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
      client.BaseAddress = new Uri(baseUrl);
      client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    });

    return services;
  }

  public static IServiceCollection AddBackendClient(this IServiceCollection services, IConfiguration configuration)
  {
    var options = configuration.GetSection(BackendOptions.SectionName).Get<BackendOptions>() ?? new BackendOptions();
    services.Configure<BackendOptions>(configuration.GetSection(BackendOptions.SectionName));

    services.AddHttpClient<IBackendClient, BackendClient>(client =>
    {
      var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
      client.BaseAddress = new Uri(baseUrl);
      client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    });

    return services;
  }

  public static IServiceCollection AddApiConfiguration(
    this IServiceCollection services,
    IConfiguration configuration,
    bool useDevelopmentAuthentication = false)
  {
    services.AddHttpContextAccessor();
    services.AddSingleton<IRequesterContext, RequesterContext>();
    services.AddValidatorsFromAssemblyContaining<Program>();

    services.AddExceptionHandler<GlobalExceptionHandler>();
    services.AddProblemDetails();

    // Enums saem como texto ("Ready"), não como o ordinal que o System.Text.Json usa por
    // padrão. Não é preferência de estilo: o `smt_glue` serializa assim, e o desktop lê os dois
    // servidores com o mesmo conversor — sem isto, `GET /axis/status` devolvia `0` e o desktop
    // caía no ramo "status desconhecido", pintando "Falha de comunicação" com a máquina
    // conectada e respondendo. Nada disso quebra em compilação: são processos separados.
    services.ConfigureHttpJsonOptions(options =>
    {
      options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    services.AddGatewayOpenApi();

    services.AddJwtAuthentication(configuration, useDevelopmentAuthentication);

    return services;
  }

  // Every route here requires a bearer token, so the document has to say so — otherwise Scalar
  // renders a page where nothing can actually be called. Same shape as the smt_glue transformer,
  // so both documents describe authentication identically.
  private static IServiceCollection AddGatewayOpenApi(this IServiceCollection services)
  {
    services.AddOpenApi(options =>
    {
      options.AddDocumentTransformer((document, _, _) =>
      {
        document.Info = new OpenApiInfo
        {
          Title = "Smart Glue - CLP Gateway",
          Version = "v1",
          Description =
            "## Daemon de célula do Smart Glue\n\n" +
            "Único mestre do CLP: dono do socket Modbus TCP, do heartbeat e do ciclo de comando. " +
            "Roda no PC da cabine e escuta apenas em `127.0.0.1`.\n\n" +
            "**Autenticação:** o token é emitido pelo `smt_glue` — este processo apenas valida. " +
            "Faça login no backend e cole o `accessToken` aqui.\n\n" +
            "**Três regras que valem para toda esta API:**\n\n" +
            "- `POST /axis/stop` nunca é recusado: não exige nível de perfil, não espera comando em " +
            "voo e funciona durante uma rotina.\n" +
            "- Uma rotina por vez. Enquanto uma roda, `/axis/move` e `/axis/zero` respondem **409** " +
            "— menos o STOP.\n" +
            "- Depois de um STOP a máquina fica em `1006 STOPPED`: chame `/axis/fault-reset` antes " +
            "de comandar de novo.\n\n" +
            "---",
          Contact = new OpenApiContact
          {
            Name = "Smart Glue Team - Conecthus"
          }
        };

        document.Servers = [new OpenApiServer { Url = "/api" }];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
          ["Bearer"] = new OpenApiSecurityScheme
          {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Cole o accessToken devolvido por POST /auth/login do smt_glue."
          }
        };

        document.Tags = new HashSet<OpenApiTag>
        {
          new()
          {
            Name = "Axis",
            Description =
              "Comando direto do eixo de inspeção. O handshake é o do kit: verificar BUSY/DONE/FAULT, " +
              "escrever alvo e velocidade, pulsar a requisição, poll, e ACK do MOVE_DONE. No contrato v2 o " +
              "HOME também termina com MOVE_DONE (e o ACK), e um alvo fora da faixa satura no limite em vez de " +
              "ser recusado: a validação contra os limites é feita aqui, antes do pulso. Posições em milímetros."
          },
          new()
          {
            Name = "Camera",
            Description =
              "Captura do campo de visão da câmera de inspeção. A imagem volta junto com o centro X/Y (mm) " +
              "em que foi tirada e o tamanho que ela cobre — é essa geometria que permite ao desktop " +
              "converter um clique na imagem em um alvo X/Y em milímetros."
          },
          new()
          {
            Name = "Routines",
            Description =
              "Execução de uma rotina de inspeção inteira. O gateway carrega e valida todos os pontos " +
              "contra os limites vivos do CLP **antes do primeiro movimento** — se algum estiver fora " +
              "de faixa, a rotina é recusada por completo e nada se move. Execuções ficam em memória e " +
              "somem no restart."
          }
        };

        return Task.CompletedTask;
      });

      options.AddOperationTransformer((operation, _, _) =>
      {
        operation.Security =
        [
          new OpenApiSecurityRequirement { { new OpenApiSecuritySchemeReference("Bearer"), [] } }
        ];
        return Task.CompletedTask;
      });
    });

    return services;
  }

  // Validates the token the backend issued. The gateway never mints one and has no user table.
  //
  // Known limitation, deliberate for now: the backend revokes a session by broadcasting over
  // RabbitMQ into its own memory cache. The gateway does not consume that feed (it has no
  // message broker by design), so a revoked token stays usable here until it expires — at most
  // AccessTokenExpirationMinutes. Acceptable while this process listens on loopback only, on
  // the same PC as the operator. It stops being acceptable the day it listens on the LAN.
  private static IServiceCollection AddJwtAuthentication(
    this IServiceCollection services,
    IConfiguration configuration,
    bool useDevelopmentAuthentication)
  {
    var jwtSettings = configuration.GetSection("JwtSettings");
    var secret = jwtSettings["Secret"]
      ?? throw new InvalidOperationException(
        "JwtSettings:Secret is not configured. The gateway validates the token issued by smt_glue, so it needs the same key.");

    var secretKey = Encoding.UTF8.GetBytes(secret);

    var defaultScheme = useDevelopmentAuthentication
      ? DevelopmentLoopbackAuthenticationHandler.SchemeName
      : JwtBearerDefaults.AuthenticationScheme;

    var authentication = services.AddAuthentication(options =>
    {
      options.DefaultAuthenticateScheme = defaultScheme;
      options.DefaultChallengeScheme = defaultScheme;
    });

    if (useDevelopmentAuthentication)
    {
      authentication.AddScheme<AuthenticationSchemeOptions, DevelopmentLoopbackAuthenticationHandler>(
        DevelopmentLoopbackAuthenticationHandler.SchemeName, _ => { });
    }

    authentication.AddJwtBearer(options =>
    {
      options.TokenValidationParameters = new TokenValidationParameters
      {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(secretKey),

        ValidateIssuer = true,
        ValidIssuer = jwtSettings["Issuer"],

        ValidateAudience = true,
        ValidAudience = jwtSettings["Audience"],

        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
      };

      options.Events = new JwtBearerEvents
      {
        OnAuthenticationFailed = context =>
        {
          switch (context.Exception)
          {
            case SecurityTokenExpiredException:
              context.Response.Headers.Append("Token-Expired", "The token has expired. Please obtain a new token.");
              break;
            case SecurityTokenException:
              context.Response.Headers.Append("Token-Invalid", "The token is invalid, possibly due to tampering or corruption.");
              break;
          }

          return Task.CompletedTask;
        },

        OnForbidden = context =>
        {
          context.Response.Headers.Append("Access-Denied", "Insufficient permissions to access this resource.");
          return Task.CompletedTask;
        }
      };
    });

    services.AddAuthorization();

    return services;
  }
}
