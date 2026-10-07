using PinSentinel.Core;

namespace PinSentinel.Tests;

public class PinFrameTests
{
    // Captured from a ROG Astral RTX 5090 OC (1043:89E3) at idle.
    private const string IdleFrame = "2EF003FC2EE8044C2EE804242EF004242EF004382EF003E8";

    [Fact]
    public void ParsesCapturedFrameWithPinSixFirst()
    {
        Assert.True(PinFrame.TryParse(Convert.FromHexString(IdleFrame), DateTimeOffset.UnixEpoch, out var frame));

        Assert.Equal(new PinReading(12.016, 1.000), frame!.Pins[0]);
        Assert.Equal(new PinReading(12.016, 1.020), frame.Pins[5]);
        Assert.Equal(6.32, frame.TotalAmps, 3);
        Assert.Equal(0.008, frame.VoltSpread, 3);
    }

    [Fact]
    public void RejectsShortBuffer() =>
        Assert.False(PinFrame.TryParse(new byte[23], DateTimeOffset.UnixEpoch, out _));

    [Theory]
    [InlineData("0000")] // bus returned zeros
    [InlineData("FFFF")] // bus returned ones
    public void RejectsImplausibleVoltage(string voltage)
    {
        byte[] raw = Convert.FromHexString(voltage + IdleFrame[4..]);
        Assert.False(PinFrame.TryParse(raw, DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public void RejectsImplausibleCurrent()
    {
        byte[] raw = Convert.FromHexString(IdleFrame[..4] + "FFFF" + IdleFrame[8..]);
        Assert.False(PinFrame.TryParse(raw, DateTimeOffset.UnixEpoch, out _));
    }
}
