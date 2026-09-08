using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class WmiBrightnessControllerTests
{
    [Fact]
    public void SuccessfulDriverResult_DoesNotFailBecauseReadbackStillHasTheOldValue()
    {
        int reads = 0;
        bool success = WmiBrightnessController.ConfirmWrite(0, 70,
            () => { reads++; return 50; }, _ => throw new Exception("No wait expected"));
        Assert.True(success);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void DriverError_IsNotMaskedByAMatchingReadback()
    {
        Assert.False(WmiBrightnessController.ConfirmWrite(1, 70, () => 70,
            _ => throw new Exception("No wait expected")));
    }

    [Fact]
    public void MissingDriverResult_AllowsReadbackToSettle()
    {
        var readings = new Queue<int?>([50, 50, 70]);
        int waits = 0;
        Assert.True(WmiBrightnessController.ConfirmWrite(null, 70,
            () => readings.Dequeue(), _ => waits++));
        Assert.Equal(2, waits);
        Assert.Empty(readings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(50)]
    public void UnconfirmedWrite_HasABoundedReadbackWindow(int? staleValue)
    {
        int waits = 0;
        Assert.False(WmiBrightnessController.ConfirmWrite(null, 70,
            () => staleValue, _ => waits++));
        Assert.Equal(5, waits);
    }
}
