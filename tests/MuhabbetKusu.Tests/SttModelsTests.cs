namespace MuhabbetKusu.Tests;

public class SttModelsTests
{
    [Theory]
    [InlineData(2, "mihu", true)]
    [InlineData(3, "uran", false)]
    [InlineData(4, "duyu", false)]
    public void SelectedTabRoutesToCorrectEngineAndLicense(int tab, string engine, bool acceptance)
    {
        var model = SttModels.ForTab(tab);
        Assert.Equal(engine, model.Key);
        Assert.Equal(acceptance, model.RequiresLicenseAcceptance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void InvalidSttTabDoesNotFallBackToMihu(int tab)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SttModels.ForTab(tab));
    }
}
