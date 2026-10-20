using OnTalk.Sidecar.Automation;

namespace OnTalk.Sidecar.Tests;

public sealed class AutoStartTests
{
    private sealed class MemoryRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public int Writes { get; private set; }
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string command) { Values[name] = command; Writes++; }
        public void Remove(string name) => Values.Remove(name);
    }

    private const string Exe = @"C:\Apps\온톡 사이드카\OnTalk.sAIde.exe";

    [Fact]
    public void 등록하면_실행_경로와_자동_실행_인자가_따옴표와_함께_들어간다()
    {
        var key = new MemoryRunKey();
        var autoStart = new AutoStart(key, Exe);

        autoStart.SetEnabled(true);

        Assert.True(autoStart.IsEnabled);
        Assert.Equal($"\"{Exe}\" --autostart", key.Values[AutoStart.ValueName]);
    }

    [Fact]
    public void 해제하면_값이_사라지고_없는_값을_해제해도_오류가_없다()
    {
        var key = new MemoryRunKey();
        var autoStart = new AutoStart(key, Exe);
        autoStart.SetEnabled(true);

        autoStart.SetEnabled(false);
        autoStart.SetEnabled(false);

        Assert.False(autoStart.IsEnabled);
        Assert.Empty(key.Values);
    }

    [Fact]
    public void 등록된_경로가_달라졌으면_지금_실행_파일로_고친다()
    {
        var key = new MemoryRunKey();
        key.Values[AutoStart.ValueName] = @"""D:\old\OnTalk.sAIde.exe"" --autostart";
        var autoStart = new AutoStart(key, Exe);

        Assert.True(autoStart.RefreshPath());

        Assert.Equal(autoStart.BuildCommand(), key.Values[AutoStart.ValueName]);
    }

    [Fact]
    public void 경로가_같으면_다시_쓰지_않고_등록하지_않은_사용자는_건드리지_않는다()
    {
        var registered = new MemoryRunKey();
        var same = new AutoStart(registered, Exe);
        same.SetEnabled(true);
        var writes = registered.Writes;
        Assert.False(same.RefreshPath());
        Assert.Equal(writes, registered.Writes);

        var empty = new MemoryRunKey();
        Assert.False(new AutoStart(empty, Exe).RefreshPath());
        Assert.Empty(empty.Values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void 실행_파일을_알_수_없거나_dotnet_호스트면_등록하지_않는다(string? path)
    {
        var key = new MemoryRunKey();
        var autoStart = new AutoStart(key, path);

        Assert.False(autoStart.CanRegister);
        Assert.Throws<InvalidOperationException>(() => autoStart.SetEnabled(true));
        Assert.Empty(key.Values);
    }

    [Fact]
    public void 시작_인자에_autostart가_있을_때만_자동_실행으로_본다()
    {
        Assert.True(AutoStart.IsAutoStartLaunch(["--AutoStart"]));
        Assert.False(AutoStart.IsAutoStartLaunch([]));
        Assert.False(AutoStart.IsAutoStartLaunch(["--quit"]));
    }
}

public sealed class OnTalkWaiterTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task 온톡이_이미_떠_있으면_기다리지_않고_안정화_시간만_둔다()
    {
        var delays = new List<TimeSpan>();
        var waiter = new OnTalkWaiter(() => true, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var ready = await waiter.WaitAsync(TimeSpan.FromMinutes(1), Second, Second * 3, CancellationToken.None);

        Assert.True(ready);
        Assert.Equal([Second * 3], delays);
    }

    [Fact]
    public async Task 온톡이_나중에_뜨면_주기마다_확인하다가_true를_돌려준다()
    {
        var checks = 0;
        var delays = new List<TimeSpan>();
        var waiter = new OnTalkWaiter(() => ++checks > 3, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var ready = await waiter.WaitAsync(TimeSpan.FromMinutes(1), Second * 2, TimeSpan.Zero, CancellationToken.None);

        Assert.True(ready);
        Assert.Equal([Second * 2, Second * 2, Second * 2], delays);
    }

    [Fact]
    public async Task 제한_시간_안에_온톡이_뜨지_않으면_false를_돌려준다()
    {
        var delays = new List<TimeSpan>();
        var waiter = new OnTalkWaiter(() => false, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var ready = await waiter.WaitAsync(Second * 5, Second * 2, Second * 3, CancellationToken.None);

        Assert.False(ready);
        Assert.Equal(Second * 5, delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d));
    }

    [Fact]
    public async Task 취소하면_기다림을_멈춘다()
    {
        using var cancel = new CancellationTokenSource();
        var waiter = new OnTalkWaiter(() => false, (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.WaitAsync(TimeSpan.FromMinutes(1), Second, Second, cancel.Token));
    }
}
