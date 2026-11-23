using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Routines;

namespace SmartGlue.ClpGateway.Common.Extensions;

/// <summary>
/// What "healthy" means for this process: the socket to the machine is up. A gateway that
/// answers HTTP but cannot reach the PLC is not degraded, it is useless — so that case is
/// Unhealthy, not Degraded, and whatever supervises the Windows Service should act on it.
/// </summary>
public class GatewayHealthCheck(
  ModbusAxisConnection connection,
  MachineIdentity identity,
  IRoutineExecutor routines,
  ResultQueue resultQueue,
  IOptions<AxisHardwareOptions> options) : IHealthCheck
{
  private readonly AxisHardwareOptions _options = options.Value;

  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
  {
    var data = new Dictionary<string, object>
    {
      ["enabled"] = _options.Enabled,
      ["host"] = $"{_options.Host}:{_options.ModbusPort}",
      ["connected"] = connection.IsConnected,
      ["machine"] = identity.State.ToString(),
      ["buildCode"] = identity.BuildCode,
      ["contractVersion"] = identity.ContractVersion,
      ["heartbeatEchoStale"] = identity.EchoStale,
      ["routineRunning"] = routines.IsRunning,
      ["pendingResults"] = resultQueue.PendingCount,
    };

    if (!_options.Enabled)
    {
      return Task.FromResult(HealthCheckResult.Unhealthy(
        "AxisHardware:Enabled is false — this gateway is not holding the machine watchdog.", data: data));
    }

    if (!connection.IsConnected)
    {
      return Task.FromResult(HealthCheckResult.Unhealthy(
        $"Not connected to the machine at {_options.Host}:{_options.ModbusPort}.", data: data));
    }

    // Connected to a machine that speaks another contract is as useless as not connected:
    // every movement command is refused.
    if (identity.State == MachineIdentityState.Incompatible)
    {
      return Task.FromResult(HealthCheckResult.Unhealthy(
        $"Incompatible machine: build {identity.BuildCode}, contract {identity.ContractVersion} " +
        $"(expected {MachineAddresses.ExpectedBuildCode} / {MachineAddresses.ExpectedContractVersion}). Movement commands are refused.",
        data: data));
    }

    if (identity.EchoStale)
    {
      return Task.FromResult(HealthCheckResult.Degraded(
        "Connected, but the heartbeat echo (D19982) is not following the counter: the machine may not be taking the heartbeat.",
        data: data));
    }

    return Task.FromResult(HealthCheckResult.Healthy("Connected to the machine.", data));
  }
}
