namespace OPB.Core.Tests;

static class Fixtures
{
    static readonly Lazy<string> Dir = new(() =>
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures");
            if (File.Exists(Path.Combine(candidate, "bank.bin"))) return candidate;
        }
        throw new DirectoryNotFoundException("tests/fixtures not found");
    });

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Dir.Value, name));

    public static byte[] Hex(string hex) => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();
}
