using AgenticSdr.Radio;

namespace AgenticSdr.Tests;

/// <summary>Checks that a LimeSDR stays on a receive port and above the rate that would power the transmitter.</summary>
public class LimeTests
{
    private static IReadOnlyList<LimePlan.Port> UsbPorts { get; } =
    [
        new(0, "None", 0, 0),
        new(1, "LNAH", 2_000_000_000, 2_600_000_000),
        new(2, "LNAL", 700_000_000, 900_000_000),
        new(3, "LNAW", 700_000_000, 2_600_000_000),
        new(4, "LB1", 700_000_000, 900_000_000),
        new(5, "LB2", 700_000_000, 2_600_000_000)
    ];

    [Fact]
    public void The_voice_rate_stays_above_the_transmit_dac_workaround()
    {
        Assert.Equal(3_072_000, LimePlan.ChooseRate(2_400_000));
        Assert.Equal(8_000_000, LimePlan.ChooseRate(8_000_000));
        Assert.True(LimePlan.ChooseRate(2_400_000) >= LimePlan.MinimumReceiveHz);
    }

    [Fact]
    public void Two_meters_uses_the_low_receive_port()
    {
        Assert.Equal(2, LimePlan.ReceivePort(UsbPorts, 146_000_000));
    }

    [Fact]
    public void A_matching_port_wins_over_a_wider_one()
    {
        Assert.Equal(2, LimePlan.ReceivePort(UsbPorts, 800_000_000));
        Assert.Equal(1, LimePlan.ReceivePort(UsbPorts, 2_200_000_000));
        Assert.Equal(3, LimePlan.ReceivePort(UsbPorts, 1_500_000_000));
    }

    [Fact]
    public void Loopback_is_not_a_receive_port()
    {
        Assert.False(LimePlan.IsReceivePort("LB1"));
        Assert.False(LimePlan.IsReceivePort("LB2"));
        Assert.False(LimePlan.IsReceivePort("None"));
        Assert.NotEqual(4, LimePlan.ReceivePort(UsbPorts, 146_000_000));
        Assert.NotEqual(5, LimePlan.ReceivePort(UsbPorts, 1_500_000_000));
    }

    [Fact]
    public void Listing_lime_radios_does_not_throw()
    {
        IReadOnlyList<RadioDeviceInfo> devices = LimeDevice.TryList();
        Assert.NotNull(devices);
        Assert.All(devices, device => Assert.Equal("lime", device.Driver));
    }

}
