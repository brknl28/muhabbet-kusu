namespace MuhabbetKusu.Tests;

public class SttModelsTests
{
    [Theory]
    [InlineData("mihu", true)]
    [InlineData("uran", false)]
    [InlineData("duyu", false)]
    public void SelectedModelRoutesToCorrectEngineAndLicense(string engine, bool acceptance)
    {
        var model = SttModels.ForKey(engine);
        Assert.Equal(engine, model.Key);
        Assert.Equal(acceptance, model.RequiresLicenseAcceptance);
        Assert.True(SttModels.IsStt(engine));
    }

    [Theory]
    [InlineData("ahenk")]
    [InlineData("antalia")]
    [InlineData("ema")]
    [InlineData("unknown")]
    public void TtsOrUnknownModelDoesNotFallBackToMihu(string model)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SttModels.ForKey(model));
        Assert.False(SttModels.IsStt(model));
    }
}
