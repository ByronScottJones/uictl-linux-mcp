namespace UICtl.Core.Tests;

public class DisplayConfigTests
{
    [Fact]
    public void StableConnectorId_IsDeterministicForSameInput()
    {
        Assert.Equal(DisplayConfig.StableConnectorId("HDMI-1"), DisplayConfig.StableConnectorId("HDMI-1"));
    }

    [Fact]
    public void StableConnectorId_DiffersForDifferentConnectors()
    {
        Assert.NotEqual(DisplayConfig.StableConnectorId("HDMI-1"), DisplayConfig.StableConnectorId("Virtual-1"));
    }

    [Fact]
    public void StableConnectorId_HandlesEmptyString()
    {
        // "" is the connector value used for a logical monitor with no
        // matching physical monitor's spec - see DisplayConfig.List()'s
        // lookup miss handling. Should hash cleanly, not throw.
        var id = DisplayConfig.StableConnectorId("");
        Assert.Equal(id, DisplayConfig.StableConnectorId(""));
    }
}
