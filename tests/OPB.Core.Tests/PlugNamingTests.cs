using System.Text;
using OPB.Core.Files;

namespace OPB.Core.Tests;

public class PlugNamingTests
{
    // Shape of settings/plugs.json as served by current firmware: no names, only engine and sample paths.
    const string Catalog = """
    {
        "engines": [ { "memoryslot": 27, "path": "engines/analoga.engine", "version": 2 } ],
        "plugs": [
            { "engine": "engines/analoga.engine", "flags": 20, "id": 10, "samples": "", "version": 25 },
            { "engine": "engines/reverb.engine", "flags": 0, "id": 35, "samples": "", "version": 1 },
            { "engine": "engines/tapetwo.engine", "flags": 1, "id": 38, "samples": "", "version": 1 },
            { "engine": "engines/sampleplayergain.engine", "flags": 0, "id": 140, "samples": "samplepacks/SebaKicks.aif", "version": 1 },
            { "engine": "engines/synthsampler.engine", "flags": 0, "id": 138, "samples": "samplepacks/CuckooC_keypawn.aif", "version": 1 },
            { "engine": "engines/sampleplayergain.engine", "flags": 0, "id": 150, "samples": "samplepacks/redmountKicks.aif", "version": 1 },
            { "flags": 0, "id": 147 }
        ]
    }
    """;

    [Fact]
    public void Derives_names_and_tracks_from_engine_and_sample_paths()
    {
        var plugs = PlugCatalog.Parse(Encoding.UTF8.GetBytes(Catalog)).ToDictionary(p => p.Id);

        Assert.Equal("SEBA KICKS", plugs[140].Name);
        Assert.Equal("REDMOUNT KICKS", plugs[150].Name);
        Assert.Equal("CUCKOO C KEYPAWN", plugs[138].Name);
        Assert.Equal("ANALOG", plugs[10].Name);
        Assert.Equal("REVERB", plugs[35].Name);
        Assert.Equal("TAPE", plugs[38].Name);

        Assert.True(plugs[140].Fits(0));
        Assert.False(plugs[140].Fits(4));
        Assert.True(plugs[138].Fits(5));
        Assert.True(plugs[35].Fits(8));
        Assert.False(plugs[10].Fits(0));
        Assert.DoesNotContain(147u, plugs.Keys); // engine entries and bare built-ins aren't selectable plugs
    }

    [Theory]
    [InlineData("SebaKicks", "SEBA KICKS")]
    [InlineData("zVinyl", "Z VINYL")]
    [InlineData("TeFX", "TE FX")]
    [InlineData("CuckooC_open", "CUCKOO C OPEN")]
    public void Splits_camel_case_pack_names(string stem, string expected) => Assert.Equal(expected, PlugNaming.Words(stem));
}
