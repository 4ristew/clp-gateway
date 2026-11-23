namespace SmartGlue.ClpGateway.Modbus;

// Bound from the "AxisHardware" section in appsettings.*.json. Talks to the "inspection"
// station of the SmartGlue_Kit_Desenvolvedores simulator today, and to the real Delta PLC
// later — same Modbus TCP contract, only Host/Port/Enabled change.
//
// Ported verbatim from smt_glue/Features/Axis/Modbus/AxisHardwareOptions.cs (MR!14), except
// for the Enabled default: in the backend it was false so that nobody needed the simulator
// running to work on unrelated features. Here the process IS the integration, so the useful
// default is true — a gateway that silently does not talk to the PLC is a gateway that
// fails silently.
public class AxisHardwareOptions
{
  public const string SectionName = "AxisHardware";

  public bool Enabled { get; set; } = true;

  public string Host { get; set; } = "127.0.0.1";
  public int ModbusPort { get; set; } = 5020;
  public byte UnitId { get; set; } = 1;

  // TODO: placeholder. The kit does not define a pulses-per-millimeter scale (it depends on
  // the real mechanical calibration — lead screw, gear ratio, encoder — which does not exist
  // in the simulator). Replace with the real calibrated value once the mechanical/PLC team
  // provides it. In a routine this error multiplies by the number of points.
  public decimal PulsesPerMillimeter { get; set; } = 1000;

  // Placeholder default speed (pulses/s) used on every MOVE_XY, since the API does not
  // expose a speed parameter today. Matches the order of magnitude of the kit examples.
  public int DefaultSpeedPulsesPerSecond { get; set; } = 20000;

  // Modbus TCP *connection* retry — the acceptance criteria "3 reconnection attempts, 5s
  // apart" maps to reconnecting the socket, not to re-issuing an in-flight move command
  // (the kit contract forbids repeating a command whose completion is indeterminate).
  public int ConnectRetryAttempts { get; set; } = 3;
  public int ConnectRetryDelaySeconds { get; set; } = 5;

  // Watchdog: an external counter must be written to EXTERNAL_HEARTBEAT at least every
  // HeartbeatTimeoutMs, recommended every HeartbeatIntervalMs, or the simulator/PLC raises
  // FAULT (HEARTBEAT_LOST) on every station. Contract v2 takes both from the real ladder
  // (TMR T450 K10, base 100 ms): 1000 ms window, 200 ms period. Contract v1 had 2000/500 — at
  // 500 ms against a 1000 ms window a single slow transaction on the shared socket is a 1009.
  public int HeartbeatIntervalMs { get; set; } = 200;
  public int HeartbeatTimeoutMs { get; set; } = 1000;

  // The heartbeat echo (D19982) is read back every this many ticks; an echo that stops
  // following the counter means the machine is not taking the heartbeat (health Degraded).
  public int HeartbeatEchoCheckEveryTicks { get; set; } = 5;

  // Upper bound for one Modbus transaction. Every transaction holds the socket's I/O gate, the
  // heartbeat included, so one that hangs is a heartbeat not written. Kept well under the
  // watchdog window; 0 leaves the library default.
  public int ModbusTransactionTimeoutMs { get; set; } = 400;

  // Command handshake polling (BUSY/DONE/FAULT) after a request bit is pulsed. This interval
  // is how long MOVE_DONE sits latched before we ACK it, and therefore most of the latency of
  // a short move: at 200 ms a 0.1 mm keyboard step (SGF-212) took ~210 ms, almost all of it
  // waiting for this poll. Only paid while a command is in flight.
  public int CommandPollIntervalMs { get; set; } = 50;
  public int CommandTimeoutSeconds { get; set; } = 30;

  // HOME has its own deadline. In contract v2 it references X → Y → Z one axis at a time:
  // from the far end of a ±100 000 p travel at 10 000 p/s that is ~30 s, the whole move
  // deadline.
  public int HomeTimeoutSeconds { get; set; } = 90;

  // How HOME is recognised as finished. Done: MOVE_DONE, acknowledged like any move (contract
  // v2, simulator and SG ladder layer). Referenced: not BUSY and referenced, after having seen
  // the HOME start — for a baseline ladder without the SG layer, whose HOME has neither BUSY
  // nor DONE. Which one the real machine needs is still open (kit PENDENCIAS §4.1).
  public HomeCompletionMode HomeCompletion { get; set; } = HomeCompletionMode.Done;

  // A refusal that does not raise FAULT (1001, 1004, 1005, 1010…) and was ALREADY showing
  // before the edge cannot be told apart by a change of code; it is taken as a refusal when it
  // is still there this long after the edge with neither BUSY nor DONE seen.
  public int RefusalGraceMs { get; set; } = 500;

  // A completed move that ends farther than this from its target, in pulses, is reported as
  // not reached (contract v2 saturates an out-of-range target at the limit).
  public int PositionTolerancePulses { get; set; } = 2;

  // With the kit default z_axis_mode=simulated the inspection refuses X/Y moves until its Z is
  // referenced too (M5015). False for a machine whose inspection has no Z (z_axis_mode=absent),
  // where that bit never comes on.
  public bool InspectionRequiresReferencedZ { get; set; } = true;

  // Deadman for the level-held JOG bits of the application station. A jog is "keep moving
  // while the operator holds the arrow", and the operator is on the other side of an HTTP
  // call: a dropped connection, a closed window or a frozen UI would otherwise leave the bit
  // at 1 and the axis travelling until it hits a soft limit. The gateway therefore releases
  // the bit on its own this long after the last request, and the desktop refreshes while the
  // button stays pressed.
  public int JogDeadmanMs { get; set; } = 700;

  // How long the axis limits cached in memory are trusted when a fresh Modbus read fails.
  // Past this age the gateway fails loudly instead of validating a routine against limits
  // that may no longer match the hardware.
  public int LimitsCacheMaxAgeSeconds { get; set; } = 300;

  // On the first successful heartbeat after connecting, a machine that was left without a
  // master sits latched in FAULT 1009 HEARTBEAT_LOST and never recovers on its own. When
  // true, the gateway clears that one fault once per session (and only that one).
  public bool ClearHeartbeatLostOnSessionOpen { get; set; } = true;
}

public enum HomeCompletionMode
{
  Done,
  Referenced
}
