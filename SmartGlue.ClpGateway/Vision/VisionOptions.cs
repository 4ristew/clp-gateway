namespace SmartGlue.ClpGateway.Vision;

// Bound from the "Vision" section in appsettings.*.json. Describes the camera feed of the
// inspection station: today the HTTP vision server of the SmartGlue simulator
// (SmartGlue_Kit_Desenvolvedores, 01_Simulador_Maquina_CLP), tomorrow the real camera server
// on the cabin PC — same contract, only BaseUrl changes.
//
// The simulator exposes, per station ("application" / "inspection"):
//   GET {BaseUrl}/{Station}/snapshot.jpg  -> image/jpeg of the current field of view
//   GET {BaseUrl}/{Station}/live.mjpg     -> multipart MJPEG stream of the same view
// The snapshot is always OutputWidthPixels x OutputHeightPixels and always covers
// FieldWidthPulses x FieldHeightPulses centred on the station's current position — which is
// what lets the desktop turn a click on the image back into a X/Y target in millimetres.
public class VisionOptions
{
  public const string SectionName = "Vision";

  public bool Enabled { get; set; } = true;

  /// <summary>Root of the camera HTTP server. The simulator listens on 127.0.0.1:8080.</summary>
  public string BaseUrl { get; set; } = "http://127.0.0.1:8080";

  /// <summary>
  /// Station whose field of view this gateway serves. The gateway is the master of the
  /// inspection station only (see AxisControlService), so this must stay "inspection" unless
  /// the whole process is repurposed.
  /// </summary>
  public string Station { get; set; } = "inspection";

  public int TimeoutSeconds { get; set; } = 10;

  // Same "3 attempts, 5s apart" policy the Modbus connection uses (AxisHardwareOptions), for
  // the same acceptance criterion: a camera that blinks must be retried before the operator is
  // told communication failed.
  public int FetchRetryAttempts { get; set; } = 3;
  public int FetchRetryDelaySeconds { get; set; } = 5;

  // Geometry of the frame the camera server returns. Mirrors the simulator's
  // visual.inspection_fov block; on real hardware it comes from the lens/sensor calibration.
  // Pulses (not millimetres) because that is the unit the machine speaks — the conversion to
  // mm uses the same AxisHardware:PulsesPerMillimeter factor as every other position here, so
  // the two can never drift apart.
  public int FieldWidthPulses { get; set; } = 20000;
  public int FieldHeightPulses { get; set; } = 15000;
  public int OutputWidthPixels { get; set; } = 640;
  public int OutputHeightPixels { get; set; } = 480;
}
