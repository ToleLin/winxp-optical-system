using XPOptical.Core.Display;
using XPOptical.Core.Vm;

namespace XPOptical.Core.Tests;

public class StateStoreTests
{
    private static void FakeSave(StateStore store, string signature = "sig", string engine = "QEMU 1")
    {
        File.WriteAllText(store.StateTempFile, "memory");
        store.CommitSavedState(signature, engine);
    }

    [Fact]
    public void ResumeRequiresMatchingGenerationSignatureAndEngine()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        store.BeginRun();
        FakeSave(store);

        Assert.True(store.CanResume("sig", "QEMU 1"));
        Assert.False(store.CanResume("other", "QEMU 1"));
        Assert.False(store.CanResume("sig", "QEMU 2"));
    }

    [Fact]
    public void AnyNewRunInvalidatesSavedState()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        store.BeginRun();
        FakeSave(store);

        store.BeginRun();

        Assert.False(File.Exists(store.StateFile));
        Assert.Null(store.ReadSavedState());
        Assert.False(store.CanResume("sig", "QEMU 1"));
    }

    [Fact]
    public void StaleStateFromOlderGenerationIsNeverResumed()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        store.BeginRun();
        FakeSave(store);
        var stateCopy = File.ReadAllBytes(store.StateFile);
        var metaCopy = File.ReadAllText(Path.Combine(dir.Path, "state.json"));

        // 模擬刪除失敗（例如檔案被鎖住）後舊休眠檔又出現：世代不符，仍不可恢復
        store.BeginRun();
        File.WriteAllBytes(store.StateFile, stateCopy);
        File.WriteAllText(Path.Combine(dir.Path, "state.json"), metaCopy);

        Assert.False(store.CanResume("sig", "QEMU 1"));
    }

    [Fact]
    public void MetaPersistsAcrossInstances()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        store.BeginRun();
        store.BeginRun();
        store.MarkInstalled();
        File.WriteAllText(store.BaseDisk, "");

        var reopened = new StateStore(dir.Path);
        Assert.Equal(2, reopened.Meta.Generation);
        Assert.True(reopened.IsInstalled);
    }

    [Fact]
    public void RectUnion()
    {
        var u = new RfbRect(10, 10, 5, 5).Union(new RfbRect(0, 20, 2, 2));
        Assert.Equal(new RfbRect(0, 10, 15, 12), u);
        Assert.Equal(new RfbRect(1, 1, 1, 1), default(RfbRect).Union(new RfbRect(1, 1, 1, 1)));
    }
}
