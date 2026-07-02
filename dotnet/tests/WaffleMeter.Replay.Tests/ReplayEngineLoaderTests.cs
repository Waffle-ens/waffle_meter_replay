using System.IO;
using WaffleMeter.Replay;
using Xunit;

namespace WaffleMeter.Replay.Tests;

/// <summary>
/// Proves the runtime discovery seam both ways: with the engine DLL present the factory loads and builds
/// a working <see cref="IReplayEngine"/>; with it absent the loader returns null so the app runs with
/// replay simply unavailable (the open-source-build path). The test's own output dir contains
/// WaffleMeter.Replay.dll (it references the concrete engine), so it doubles as the "present" fixture.
/// </summary>
public class ReplayEngineLoaderTests
{
    [Fact]
    public void Finds_and_instantiates_the_engine_when_the_dll_is_present()
    {
        IReplayEngineFactory? factory = ReplayEngineLoader.TryLoad(AppContext.BaseDirectory);

        Assert.NotNull(factory);
        IReplayEngine engine = factory!.Create(extraIdentity: null, persistDir: null);
        Assert.NotNull(engine);
        Assert.Null(engine.LastRecording); // fresh engine, nothing recorded yet
        engine.Scan(new byte[] { 0x10, 0x00, 0x37 }, at: 1); // must not throw on a hot-path call
    }

    [Fact]
    public void Returns_null_when_the_engine_dll_is_absent()
    {
        string emptyDir = Path.Combine(Path.GetTempPath(), "wm_no_engine_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            Assert.Null(ReplayEngineLoader.TryLoad(emptyDir));
            Assert.False(ReplayEngineLoader.IsAvailable(emptyDir));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }
}
