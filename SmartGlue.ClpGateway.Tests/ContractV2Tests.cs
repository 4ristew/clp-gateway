using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartGlue.ClpGateway.Modbus;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// Every address constant this gateway uses, checked against the contract the kit publishes
/// (simulador/contracts/contrato_publico_clp.v2.json, generated from the simulator's own map).
///
/// The v1 → v2 change moved the application limits onto what had been their addresses' new
/// meaning — the JOG speeds — and nothing failed to compile or to pass: the gateway simply
/// validated every move against ±(jog speed). This is the test that catches the next move.
///
/// The JSON is a COPY, pinned by SHA. A new kit means replacing the file and the hash on
/// purpose, and then seeing which constants this suite says moved.
/// </summary>
public class ContractV2Tests
{
  // SHA-256 of the kit 2.0.0 file with line endings normalized to LF (a Windows checkout with
  // core.autocrlf would otherwise change the hash without changing the contract).
  private const string ContractSha256 = "b55e5b976236cf5df1f347492ef23197fef347e2d175982937f2d9586051631d";

  private static readonly string ContractPath = Path.Combine(AppContext.BaseDirectory, "Contract", "contrato_publico_clp.v2.json");

  private static JsonElement Contract { get; } = JsonDocument.Parse(File.ReadAllText(ContractPath)).RootElement;

  private static JsonElement Station(string name) => Contract.GetProperty("stations").GetProperty(name);

  /// <summary>"M5000" / "D24000" → 5000 / 24000. The device number is the Modbus address in the kit.</summary>
  private static int Device(JsonElement element) => int.Parse(element.GetString()![1..]);

  private static int Device(JsonElement station, string key) => Device(station.GetProperty(key));

  private static int Device(JsonElement station, string key, int index) => Device(station.GetProperty(key)[index]);

  /// <summary>"D19980/D19981" → 19980.</summary>
  private static int Base(string pair) => int.Parse(pair.Split('/')[0][1..]);

  [Fact]
  public void The_copy_is_the_kit_2_0_0_contract()
  {
    var normalized = File.ReadAllText(ContractPath).Replace("\r\n", "\n");
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    Assert.Equal(ContractSha256, hash);
    Assert.Equal(2, Contract.GetProperty("contract_version").GetInt32());
  }

  [Fact]
  public void Identity_matches_the_contract()
  {
    var identity = Contract.GetProperty("identity");

    Assert.Equal(MachineAddresses.BuildCode, Device(identity.GetProperty("build_code"), "device"));
    Assert.Equal(MachineAddresses.ExpectedBuildCode, identity.GetProperty("build_code").GetProperty("value").GetInt32());
    Assert.Equal(MachineAddresses.ContractVersion, Device(identity.GetProperty("contract_version"), "device"));
    Assert.Equal(MachineAddresses.ExpectedContractVersion, identity.GetProperty("contract_version").GetProperty("value").GetUInt16());
  }

  [Fact]
  public void Watchdog_addresses_and_default_timings_match_the_contract()
  {
    var watchdog = Contract.GetProperty("watchdog");
    var defaults = new AxisHardwareOptions();

    Assert.Equal(MachineAddresses.HeartbeatCounter, Base(watchdog.GetProperty("external_counter").GetString()!));
    Assert.Equal(MachineAddresses.HeartbeatEcho, Base(watchdog.GetProperty("echo").GetString()!));
    Assert.Equal(watchdog.GetProperty("timeout_ms").GetInt32(), defaults.HeartbeatTimeoutMs);
    Assert.Equal(watchdog.GetProperty("recommended_period_ms").GetInt32(), defaults.HeartbeatIntervalMs);
  }

  [Fact]
  public void Application_station_matches_the_contract()
  {
    var s = Station("application");

    Assert.Equal(ApplicationStationAddresses.Home, Device(s, "home"));
    Assert.Equal(ApplicationStationAddresses.MoveXy, Device(s, "move_xy"));
    Assert.Equal(ApplicationStationAddresses.MoveZ, Device(s, "move_z"));
    Assert.Equal(ApplicationStationAddresses.Stop, Device(s, "stop"));
    Assert.Equal(ApplicationStationAddresses.FaultReset, Device(s, "fault_reset"));

    Assert.Equal(ApplicationStationAddresses.Busy, Device(s, "busy_done_fault", 0));
    Assert.Equal(ApplicationStationAddresses.MoveDone, Device(s, "busy_done_fault", 1));
    Assert.Equal(ApplicationStationAddresses.Fault, Device(s, "busy_done_fault", 2));
    Assert.Equal(ApplicationStationAddresses.ReferencedX, Device(s, "referenced_xyz", 0));
    Assert.Equal(ApplicationStationAddresses.ReferencedY, Device(s, "referenced_xyz", 1));
    Assert.Equal(ApplicationStationAddresses.ReferencedZ, Device(s, "referenced_xyz", 2));

    Assert.Equal(ApplicationStationAddresses.TargetX, Device(s, "target_xyz", 0));
    Assert.Equal(ApplicationStationAddresses.TargetY, Device(s, "target_xyz", 1));
    Assert.Equal(ApplicationStationAddresses.TargetZ, Device(s, "target_xyz", 2));
    Assert.Equal(ApplicationStationAddresses.CurrentX, Device(s, "current_xyz", 0));
    Assert.Equal(ApplicationStationAddresses.CurrentY, Device(s, "current_xyz", 1));
    Assert.Equal(ApplicationStationAddresses.CurrentZ, Device(s, "current_xyz", 2));
    Assert.Equal(ApplicationStationAddresses.SpeedXy, Device(s, "speed_xy_z", 0));
    Assert.Equal(ApplicationStationAddresses.SpeedZ, Device(s, "speed_xy_z", 1));
    Assert.Equal(ApplicationStationAddresses.ErrorCode, Device(s, "error_code"));

    Assert.Equal(ApplicationStationAddresses.JogXNegative, Device(s, "jog_x_negative_positive", 0));
    Assert.Equal(ApplicationStationAddresses.JogXPositive, Device(s, "jog_x_negative_positive", 1));
    Assert.Equal(ApplicationStationAddresses.JogYNegative, Device(s, "jog_y_negative_positive", 0));
    Assert.Equal(ApplicationStationAddresses.JogYPositive, Device(s, "jog_y_negative_positive", 1));

    Assert.Equal(ApplicationStationAddresses.JogSpeedXNegative, Device(s, "jog_speed_x_negative_positive", 0));
    Assert.Equal(ApplicationStationAddresses.JogSpeedXPositive, Device(s, "jog_speed_x_negative_positive", 1));
    Assert.Equal(ApplicationStationAddresses.JogSpeedYNegative, Device(s, "jog_speed_y_negative_positive", 0));
    Assert.Equal(ApplicationStationAddresses.JogSpeedYPositive, Device(s, "jog_speed_y_negative_positive", 1));
    Assert.Equal(ApplicationStationAddresses.JogSpeedZNegative, Device(s, "jog_speed_z_negative_positive", 0));
    Assert.Equal(ApplicationStationAddresses.JogSpeedZPositive, Device(s, "jog_speed_z_negative_positive", 1));
  }

  [Fact]
  public void Application_limits_follow_the_ladder_order_not_xyz_min_max()
  {
    // limits_xyz_min_max is X min, X max, Y min, Y max, Z min, Z max by NAME, and the devices
    // listed are the ladder's: D23070, D23110, D23060, D23100, D23120, D23080. Swapping any
    // pair here would validate X against Y's bounds, or Z upside down.
    var s = Station("application");

    Assert.Equal(ApplicationStationAddresses.LimitXMin, Device(s, "limits_xyz_min_max", 0));
    Assert.Equal(ApplicationStationAddresses.LimitXMax, Device(s, "limits_xyz_min_max", 1));
    Assert.Equal(ApplicationStationAddresses.LimitYMin, Device(s, "limits_xyz_min_max", 2));
    Assert.Equal(ApplicationStationAddresses.LimitYMax, Device(s, "limits_xyz_min_max", 3));
    Assert.Equal(ApplicationStationAddresses.LimitZMin, Device(s, "limits_xyz_min_max", 4));
    Assert.Equal(ApplicationStationAddresses.LimitZMax, Device(s, "limits_xyz_min_max", 5));

    Assert.Equal(23070, ApplicationStationAddresses.LimitXMin);
    Assert.Equal(23060, ApplicationStationAddresses.LimitYMin);
    Assert.Equal(23080, ApplicationStationAddresses.LimitZMax);
  }

  [Fact]
  public void Inspection_station_matches_the_contract()
  {
    var s = Station("inspection");

    Assert.Equal(InspectionStationAddresses.Home, Device(s, "home"));
    Assert.Equal(InspectionStationAddresses.MoveXy, Device(s, "move_xy"));
    Assert.Equal(InspectionStationAddresses.Stop, Device(s, "stop"));
    Assert.Equal(InspectionStationAddresses.FaultReset, Device(s, "fault_reset"));

    Assert.Equal(InspectionStationAddresses.Busy, Device(s, "busy_done_fault", 0));
    Assert.Equal(InspectionStationAddresses.MoveDone, Device(s, "busy_done_fault", 1));
    Assert.Equal(InspectionStationAddresses.Fault, Device(s, "busy_done_fault", 2));
    Assert.Equal(InspectionStationAddresses.ReferencedX, Device(s, "referenced_xyz", 0));
    Assert.Equal(InspectionStationAddresses.ReferencedY, Device(s, "referenced_xyz", 1));
    Assert.Equal(InspectionStationAddresses.ReferencedZ, Device(s, "referenced_xyz", 2));

    Assert.Equal(InspectionStationAddresses.TargetX, Device(s, "target_xyz", 0));
    Assert.Equal(InspectionStationAddresses.TargetY, Device(s, "target_xyz", 1));
    Assert.Equal(InspectionStationAddresses.CurrentX, Device(s, "current_xyz", 0));
    Assert.Equal(InspectionStationAddresses.CurrentY, Device(s, "current_xyz", 1));
    Assert.Equal(InspectionStationAddresses.SpeedXy, Device(s, "speed_xy_z", 0));
    Assert.Equal(InspectionStationAddresses.ErrorCode, Device(s, "error_code"));

    Assert.Equal(InspectionStationAddresses.LimitXMin, Device(s, "limits_xyz_min_max", 0));
    Assert.Equal(InspectionStationAddresses.LimitXMax, Device(s, "limits_xyz_min_max", 1));
    Assert.Equal(InspectionStationAddresses.LimitYMin, Device(s, "limits_xyz_min_max", 2));
    Assert.Equal(InspectionStationAddresses.LimitYMax, Device(s, "limits_xyz_min_max", 3));
  }
}
