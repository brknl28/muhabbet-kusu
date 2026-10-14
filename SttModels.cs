namespace MuhabbetKusu;

internal sealed record SttModel(string Key, string Attribution, string License, bool RequiresLicenseAcceptance = false);

internal static class SttModels
{
    public static SttModel ForTab(int index) => index switch
    {
        2 => new("mihu", "Powered by mihu", "mihu Community License", true),
        3 => new("uran", "Uran STT Base · Curiosity Technology", "Apache License 2.0"),
        4 => new("duyu", "Duyu-small · Kaan Aydınlı", "Apache License 2.0"),
        _ => throw new ArgumentOutOfRangeException(nameof(index), "STT sekmesi seçilmedi.")
    };
}
