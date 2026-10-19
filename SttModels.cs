namespace MuhabbetKusu;

internal sealed record SttModel(string Key, string Attribution, string License, bool RequiresLicenseAcceptance = false);

internal static class SttModels
{
    public static bool IsStt(string key) => key is "duyu" or "mihu" or "uran";

    public static SttModel ForKey(string key) => key switch
    {
        "mihu" => new("mihu", "Powered by mihu", "mihu Community License", true),
        "uran" => new("uran", "Uran STT Base · Curiosity Technology", "Apache License 2.0"),
        "duyu" => new("duyu", "Duyu-small · Kaan Aydınlı", "Apache License 2.0"),
        _ => throw new ArgumentOutOfRangeException(nameof(key), "STT sekmesi seçilmedi.")
    };
}
