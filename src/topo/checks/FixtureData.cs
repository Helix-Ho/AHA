namespace Aha.Topo.Checks;

internal static class FixtureData
{
    internal static bool Recorded =>
        (AppDomain.CurrentDomain.GetData("AHA_CHECK_DATA_SCOPE") as string
            ?? Environment.GetEnvironmentVariable("AHA_CHECK_DATA_SCOPE")) == "recorded";

    internal static string DirectoryPath => Recorded
        ? Path.Combine(LocalRoot, "topo")
        : Path.Combine(Path.GetDirectoryName(typeof(FixtureData).Assembly.Location)!, "fixtures");

    private static string LocalRoot =>
        (AppDomain.CurrentDomain.GetData("AHA_CHECK_LOCAL_FIXTURES") as string
            ?? Environment.GetEnvironmentVariable("AHA_CHECK_LOCAL_FIXTURES"))
        ?? throw new FileNotFoundException("Recorded Topo fixtures were not selected by the check runner.");

    internal static string PathFor(string name) => Path.Combine(DirectoryPath, name);

    internal static System.Text.Json.JsonElement Witness(string name)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(PathFor("witnesses.json")));
        return document.RootElement.GetProperty(name).Clone();
    }
}
