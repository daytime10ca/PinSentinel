using System.Buffers.Binary;

namespace PinSentinel.Core;

public readonly record struct PinReading(double Volts, double Amps);

/// <summary>One sample of all six +12 V pins. Index 0 is pin 1.</summary>
public sealed record PinFrame(DateTimeOffset Timestamp, IReadOnlyList<PinReading> Pins)
{
    public const int PinCount = 6;
    public const int RawLength = PinCount * 4;

    // Outside these the frame is treated as a bad read, not as a measurement.
    public const double MinValidVolts = 6.0;
    public const double MaxValidVolts = 16.0;
    public const double MaxValidAmps = 30.0;

    public double TotalAmps => Pins.Sum(p => p.Amps);
    public double MeanAmps => TotalAmps / PinCount;
    public double MaxAmps => Pins.Max(p => p.Amps);
    public double MinAmps => Pins.Min(p => p.Amps);
    public double VoltSpread => Pins.Max(p => p.Volts) - Pins.Min(p => p.Volts);
    public double Watts => Pins.Sum(p => p.Volts * p.Amps);

    /// <summary>
    /// Parses the 24-byte block from register 0x80. Each pin is u16 BE millivolts
    /// then u16 BE milliamps, and the chip returns pin 6 first.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> raw, DateTimeOffset timestamp, out PinFrame? frame)
    {
        frame = null;
        if (raw.Length < RawLength) return false;

        var pins = new PinReading[PinCount];
        for (int i = 0; i < PinCount; i++)
        {
            var block = raw.Slice(i * 4, 4);
            double volts = BinaryPrimitives.ReadUInt16BigEndian(block) / 1000.0;
            double amps = BinaryPrimitives.ReadUInt16BigEndian(block[2..]) / 1000.0;
            if (volts < MinValidVolts || volts > MaxValidVolts || amps > MaxValidAmps) return false;
            pins[PinCount - 1 - i] = new PinReading(volts, amps);
        }

        frame = new PinFrame(timestamp, pins);
        return true;
    }
}
