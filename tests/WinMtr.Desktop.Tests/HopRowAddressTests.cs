using WinMtr.Desktop.ViewModels;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The Host cell's identity text: the optional second line shows the numeric
/// address under the displayed label only when name resolution gave a
/// different label, and the hover text carries the same pair. Resolution off
/// and silent hops keep the single line, and a silent hop has no hover text.
/// No Avalonia dependency.
/// </summary>
public class HopRowAddressTests
{
    [Theory]
    [InlineData("router.example", "192.0.2.1", "router.example")]
    [InlineData("", "192.0.2.1", "192.0.2.1")]
    [InlineData("192.0.2.1", "192.0.2.1", "192.0.2.1")]
    [InlineData("", "", "")]
    public void HostLabel_UsesTheName_ThenTheRespondingAddress(string host, string address, string expected)
    {
        // Pending and failed lookup both have an empty name and a known address.
        var row = new HopRowViewModel { Host = host, Address = address };

        Assert.Equal(expected, row.HostLabel);
    }

    [Fact]
    public void HostLabel_Notifies_WhenEitherIdentityPartChanges()
    {
        var row = new HopRowViewModel();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Address = "192.0.2.1";
        Assert.Contains(nameof(HopRowViewModel.HostLabel), raised);
        Assert.Equal("192.0.2.1", row.HostLabel);

        raised.Clear();
        row.Host = "router.example";
        Assert.Contains(nameof(HopRowViewModel.HostLabel), raised);
        Assert.Equal("router.example", row.HostLabel);
    }

    [Fact]
    public void ShowAddress_IsTrue_WhenA_ResolvedNameDiffersFromTheAddress()
    {
        var row = new HopRowViewModel { Host = "router.example", Address = "192.0.2.1" };

        Assert.True(row.ShowAddress);
    }

    [Fact]
    public void ShowAddress_IsFalse_WhenResolutionIsOff()
    {
        // With resolution off the label is the address itself, so a second
        // line would only repeat the first.
        var row = new HopRowViewModel { Host = "192.0.2.1", Address = "192.0.2.1" };

        Assert.False(row.ShowAddress);
    }

    [Fact]
    public void ShowAddress_IsFalse_ForASilentHop()
    {
        // A silent hop has no known address, so there is nothing to show.
        var row = new HopRowViewModel { Host = string.Empty, Address = string.Empty };

        Assert.False(row.ShowAddress);
    }

    [Theory]
    [InlineData(nameof(HopRowViewModel.Host))]
    [InlineData(nameof(HopRowViewModel.Address))]
    public void ShowAddress_Notifies_WhenAnInputChanges(string changedProperty)
    {
        var row = new HopRowViewModel();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        switch (changedProperty)
        {
            case nameof(HopRowViewModel.Host):
                row.Host = "router.example";
                break;
            case nameof(HopRowViewModel.Address):
                row.Address = "192.0.2.1";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedProperty));
        }

        Assert.Contains(nameof(HopRowViewModel.ShowAddress), raised);
    }

    [Fact]
    public void HostToolTip_NamesBothParts_WhenTheAddressDiffers()
    {
        var row = new HopRowViewModel { Host = "router.example", Address = "192.0.2.1" };

        Assert.Equal("router.example (192.0.2.1)", row.HostToolTip);
    }

    [Fact]
    public void HostToolTip_IsTheLabelAlone_WhenResolutionIsOff()
    {
        // The label is the address itself, so the tip must not repeat it.
        var row = new HopRowViewModel { Host = "192.0.2.1", Address = "192.0.2.1" };

        Assert.Equal("192.0.2.1", row.HostToolTip);
    }

    [Fact]
    public void HostToolTip_IsNull_ForASilentHop()
    {
        // A null tip is the only value that opens no tooltip.
        var row = new HopRowViewModel();

        Assert.Null(row.HostToolTip);
    }

    [Theory]
    [InlineData(nameof(HopRowViewModel.Host))]
    [InlineData(nameof(HopRowViewModel.Address))]
    public void HostToolTip_Notifies_WhenAnInputChanges(string changedProperty)
    {
        var row = new HopRowViewModel();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        switch (changedProperty)
        {
            case nameof(HopRowViewModel.Host):
                row.Host = "router.example";
                break;
            case nameof(HopRowViewModel.Address):
                row.Address = "192.0.2.1";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedProperty));
        }

        Assert.Contains(nameof(HopRowViewModel.HostToolTip), raised);
    }
}
