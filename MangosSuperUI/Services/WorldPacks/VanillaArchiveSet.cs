using MangosSuperUI.Services.Mpq;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// Read-only view of the STOCK 1.12 archives — the baseline every World Content Pack is built on.
/// Only the archives shipped with the game are opened (numbered patches above patch-2 are other
/// SuperUI lanes or the pack patch itself and must never feed back into a pack build). Precedence
/// matches the client: patch-2 > patch > base data archives.
/// </summary>
public sealed class VanillaArchiveSet : IDisposable
{
    private static readonly string[] Stock =
    {
        "patch-2.MPQ", "patch.MPQ", "terrain.MPQ", "model.MPQ", "wmo.MPQ", "texture.MPQ",
        "dbc.MPQ", "misc.MPQ", "base.MPQ", "interface.MPQ", "sound.MPQ", "speech.MPQ",
        "fonts.MPQ", "backup.MPQ",
    };

    private readonly List<MpqArchive> _archives = new();

    public string DataDirectory { get; }

    public VanillaArchiveSet(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        foreach (var name in Stock)
        {
            var path = Path.Combine(dataDirectory, name);
            if (!File.Exists(path)) continue;
            var a = MpqArchive.Open(path);
            if (a != null) _archives.Add(a);
        }
        if (_archives.Count == 0)
            throw new DirectoryNotFoundException($"No stock MPQ archives in {dataDirectory}");
    }

    public byte[]? ReadFile(string path)
    {
        foreach (var a in _archives)
        {
            var bytes = a.ReadFile(path);
            if (bytes != null) return bytes;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var a in _archives) a.Dispose();
        _archives.Clear();
    }
}
