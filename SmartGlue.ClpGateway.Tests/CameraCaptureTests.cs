using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Vision;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// Exercises the camera capture without the simulator: the vision server is a stubbed
/// <see cref="HttpMessageHandler"/> and the axis is the usual <see cref="FakeMachine"/>.
/// </summary>
public class CameraCaptureTests
{
  private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

  private static VisionOptions Options(Action<VisionOptions>? tweak = null)
  {
    var options = new VisionOptions
    {
      Enabled = true,
      BaseUrl = "http://127.0.0.1:8080/",
      Station = "inspection",
      FetchRetryAttempts = 3,
      // Tests must not sleep for the real 5s policy; the retry *count* is what matters here.
      FetchRetryDelaySeconds = 0,
      FieldWidthPulses = 20000,
      FieldHeightPulses = 15000,
      OutputWidthPixels = 640,
      OutputHeightPixels = 480,
    };

    tweak?.Invoke(options);
    return options;
  }

  private static CameraService Build(FakeMachine machine, StubVisionServer server, VisionOptions? vision = null)
  {
    var axisOptions = TestHost.Options();
    var (axis, _) = TestHost.Axis(machine);

    var client = new HttpClient(server) { BaseAddress = new Uri(vision?.BaseUrl ?? "http://127.0.0.1:8080/") };

    return new CameraService(
      client,
      axis,
      TestHost.Application(machine),
      Microsoft.Extensions.Options.Options.Create(vision ?? Options()),
      Microsoft.Extensions.Options.Options.Create(axisOptions),
      NullLogger<CameraService>.Instance);
  }

  [Fact]
  public async Task Capture_returns_the_frame_with_the_geometry_that_locates_it_on_the_board()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.SetPosition(120_000, 50_000);

    var server = new StubVisionServer(Jpeg);
    var camera = Build(machine, server);

    var snapshot = await camera.CaptureAsync(CancellationToken.None);

    Assert.Equal(Convert.ToBase64String(Jpeg), snapshot.ImageBase64);
    Assert.Equal("image/jpeg", snapshot.ContentType);
    Assert.Equal(640, snapshot.WidthPixels);
    Assert.Equal(480, snapshot.HeightPixels);

    // PulsesPerMillimeter is 1000 in the test options.
    Assert.Equal(120m, snapshot.CenterXMm);
    Assert.Equal(50m, snapshot.CenterYMm);
    Assert.Equal(20m, snapshot.FieldWidthMm);
    Assert.Equal(15m, snapshot.FieldHeightMm);

    Assert.Equal("/inspection/snapshot.jpg", server.LastPath);
  }

  [Fact]
  public async Task Capture_retries_three_times_before_reporting_a_communication_failure()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);

    var server = new StubVisionServer(Jpeg) { FailuresBeforeSuccess = int.MaxValue };
    var camera = Build(machine, server);

    await Assert.ThrowsAsync<IntegrationException>(() => camera.CaptureAsync(CancellationToken.None));

    Assert.Equal(3, server.Calls);
  }

  [Fact]
  public async Task Capture_succeeds_when_the_camera_answers_on_a_later_attempt()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);

    var server = new StubVisionServer(Jpeg) { FailuresBeforeSuccess = 2 };
    var camera = Build(machine, server);

    var snapshot = await camera.CaptureAsync(CancellationToken.None);

    Assert.Equal(3, server.Calls);
    Assert.Equal(Convert.ToBase64String(Jpeg), snapshot.ImageBase64);
  }

  [Fact]
  public async Task Capture_refuses_a_frame_it_cannot_locate_on_the_board()
  {
    // A frame without X-Pose-Pulses (a real camera) falls back to Modbus for its centre. The
    // axis is unreachable, so GetStatusAsync reports CommunicationFailure at 0,0. Returning
    // a frame centred on that fake origin would make every click convert to the wrong target.
    // The frame is fetched first since contract v2 (the pose may come with it), so each
    // attempt reaches the server — and each is still refused.
    var machine = new FakeMachine { IsReachable = false };
    var server = new StubVisionServer(Jpeg);
    var camera = Build(machine, server);

    await Assert.ThrowsAsync<IntegrationException>(() => camera.CaptureAsync(CancellationToken.None));

    Assert.Equal(3, server.Calls);
  }

  [Fact]
  public async Task Capture_uses_the_pose_that_came_with_the_frame_and_does_not_read_modbus()
  {
    // Unreachable on purpose: with the pose in the frame's own header, the axis is not asked.
    var machine = new FakeMachine { IsReachable = false };
    var server = new StubVisionServer(Jpeg) { Pose = "77500,20000,0" };
    var camera = Build(machine, server);

    var snapshot = await camera.CaptureAsync(CancellationToken.None);

    Assert.Equal(77.5m, snapshot.CenterXMm);
    Assert.Equal(20m, snapshot.CenterYMm);
    Assert.Equal(1, server.Calls);
  }

  [Theory]
  [InlineData("")]
  [InlineData("garbage")]
  [InlineData("12,")]
  public async Task A_malformed_pose_header_falls_back_to_modbus(string pose)
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.SetPosition(120_000, 50_000);
    var server = new StubVisionServer(Jpeg) { Pose = pose };
    var camera = Build(machine, server);

    var snapshot = await camera.CaptureAsync(CancellationToken.None);

    Assert.Equal(120m, snapshot.CenterXMm);
    Assert.Equal(50m, snapshot.CenterYMm);
  }

  [Fact]
  public async Task Capture_is_refused_when_the_camera_integration_is_disabled()
  {
    var machine = new FakeMachine();
    var server = new StubVisionServer(Jpeg);
    var camera = Build(machine, server, Options(o => o.Enabled = false));

    await Assert.ThrowsAsync<IntegrationException>(() => camera.CaptureAsync(CancellationToken.None));

    Assert.Equal(0, server.Calls);
  }
}

/// <summary>Stands in for the simulator's vision HTTP server.</summary>
public sealed class StubVisionServer(byte[] jpeg) : HttpMessageHandler
{
  public int Calls { get; private set; }
  public string? LastPath { get; private set; }
  public int FailuresBeforeSuccess { get; set; }

  /// <summary>When set, sent as X-Pose-Pulses, as the kit simulator does with every frame.</summary>
  public string? Pose { get; set; }

  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    Calls++;
    LastPath = request.RequestUri?.AbsolutePath;

    if (Calls <= FailuresBeforeSuccess)
    {
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
      {
        Content = new StringContent("camera unavailable", Encoding.UTF8, "text/plain"),
      });
    }

    var content = new ByteArrayContent(jpeg);
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    if (Pose is not null) response.Headers.TryAddWithoutValidation("X-Pose-Pulses", Pose);

    return Task.FromResult(response);
  }
}
