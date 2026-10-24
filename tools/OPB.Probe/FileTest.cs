using System.Diagnostics;
using OPB.Core;
using OPB.Core.Files;

/// <summary>
/// File-service timing checks: back-to-back reads of the slot map, then an upload of the unchanged
/// slot map (the OP-Z skips files whose CRC already matches, so nothing is modified).
/// </summary>
static class FileTest
{
    public static async Task RunAsync(OpzSession session)
    {
        var client = new OpzFileClient(new SessionFileTransport(session), new FileClientOptions { HelloTimeout = TimeSpan.FromSeconds(20) });
        client.Log += m => Console.WriteLine($"     file: {m}");
        var sw = Stopwatch.StartNew();

        byte[]? slots = null;
        for (int i = 1; i <= 3; i++)
        {
            sw.Restart();
            var reader = Environment.GetCommandLineArgs().Contains("--new-server-id")
                ? new OpzFileClient(new SessionFileTransport(session), new FileClientOptions { HelloTimeout = TimeSpan.FromSeconds(20), ServerId = (ushort)(0x4300 + i) })
                : client;
            var read = await reader.DownloadAsync(FileIds.SlotConfiguration);
            Console.WriteLine($"  >> read {i}: {read.Error} {read.Message} in {sw.ElapsedMilliseconds} ms, {read.Value?[0].Length} bytes");
            slots ??= read.Value?[0];
        }
        if (slots is null) return;

        sw.Restart();
        var (write, back) = await client.UploadAndReadBackAsync([new HostedFile(FileIds.SlotConfigurationPath, slots)], [FileIds.SlotConfiguration]);
        Console.WriteLine($"  >> unchanged upload: {write.Error} {write.Message}; skipped {write.Value?.SkippedFileIds.Count}, parts sent {write.Value?.PartsSent}");
        Console.WriteLine($"  >> readback in same session: {back?.Error} {back?.Message}; identical {back?.Value?[0].AsSpan().SequenceEqual(slots)}; total {sw.ElapsedMilliseconds} ms");
    }
}
