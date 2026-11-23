namespace SmartGlue.ClpGateway.Modbus;

/// <summary>
/// The wire to the machine. Extracted from the concrete <see cref="ModbusAxisConnection"/>
/// (which MR!14 used directly) so the command cycle can be exercised in tests without a
/// socket, a simulator or a PLC — the gateway executes routines unattended, and code that can
/// only be tested on a bench is code that is not tested.
/// </summary>
public interface IAxisConnection
{
  bool IsConnected { get; }

  /// <summary>
  /// Serializes a whole command lifecycle (verify -> write -> pulse -> poll -> ack). STOP
  /// deliberately does not take it.
  /// </summary>
  Task<IDisposable> AcquireCommandLockAsync(CancellationToken ct);

  Task<bool> ReadCoilAsync(int address, CancellationToken ct);
  Task WriteCoilAsync(int address, bool value, CancellationToken ct);
  Task PulseCoilAsync(int address, CancellationToken ct);
  Task<int> ReadDwordAsync(int address, CancellationToken ct);
  Task WriteDwordAsync(int address, int value, CancellationToken ct);
  Task<ushort> ReadUInt16Async(int address, CancellationToken ct);
}
