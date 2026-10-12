namespace Aha.EumCrawl.Checks;

internal static class FixtureData
{
    internal static bool Recorded =>
        (AppDomain.CurrentDomain.GetData("AHA_CHECK_DATA_SCOPE") as string
            ?? Environment.GetEnvironmentVariable("AHA_CHECK_DATA_SCOPE")) == "recorded";

    internal static string DirectoryPath => Recorded
        ? Path.Combine(LocalRoot, "eum-crawl")
        : Path.Combine(Path.GetDirectoryName(typeof(FixtureData).Assembly.Location)!, "fixtures");

    private static string LocalRoot =>
        (AppDomain.CurrentDomain.GetData("AHA_CHECK_LOCAL_FIXTURES") as string
            ?? Environment.GetEnvironmentVariable("AHA_CHECK_LOCAL_FIXTURES"))
        ?? throw new FileNotFoundException("Recorded EumCrawl fixtures were not selected by the check runner.");

    internal static string FileName(string recordedName) => Recorded ? recordedName : recordedName switch
    {
        "parcel-recorded.html" => "parcel-synthetic.html",
        "ratios-recorded.html" => "ratios-synthetic.html",
        "height-recorded.html" => "height-synthetic.html",
        "setback-recorded.html" => "setback-synthetic.html",
        "ratios-calculated-ui.html" => "ratios-calculated-synthetic.html",
        _ => recordedName
    };

    internal static string PathFor(string name) => Path.Combine(DirectoryPath, FileName(name));

    internal static System.Text.Json.JsonElement Facts =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "facts.json"))).RootElement.Clone();
}
