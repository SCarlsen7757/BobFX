using BobFx.Core.Services;

namespace BobFx.Core.Tests;

public sealed class ControllerCredentialsTests
{
    private const string Version = """{"api_semver":"27.5.0"}""";

    [Fact]
    public async Task PasswordsPersistProtectedAndAreSeparateFromExportedSettings()
    {
        await using var h = new TestHarness();
        var (settings, credentials) = (h.Store, h.Credentials);
        var bar = new BusyBarSettings { Id = "remote", Address = "192.168.1.30" };
        await settings.SaveAsync(new() { Controllers = [bar] }, TestContext.Current.CancellationToken);
        var password = Guid.NewGuid().ToString("N");
        Assert.False(credentials.HasPassword(bar.Id));
        await credentials.SetAsync(bar.Id, password, TestContext.Current.CancellationToken);
        Assert.True(credentials.HasPassword(bar.Id));
        Assert.Equal(1, credentials.Revision(bar.Id));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(Path.Combine(h.DataPath, "settings.json"), TestContext.Current.CancellationToken));
        var protectedFile = Assert.Single(Directory.GetFiles(Path.Combine(h.DataPath, "credentials"), "*.protected"));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(protectedFile, TestContext.Current.CancellationToken));
        var reopened = new ControllerCredentials(TestData.Store(h.DataPath));
        Assert.Equal(password, reopened.Read(bar.Id));
        Assert.Throws<FileNotFoundException>(() => reopened.Read("other-controller"));
        await credentials.SetAsync(bar.Id, password + "-replacement", TestContext.Current.CancellationToken);
        Assert.Equal(2, credentials.Revision(bar.Id));
        Assert.Equal(password + "-replacement", credentials.Read(bar.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => credentials.SetAsync(bar.Id, "bad\r\nheader", TestContext.Current.CancellationToken));
        Assert.Equal(password + "-replacement", credentials.Read(bar.Id));
    }
    [Fact]
    public async Task LocalHttpConnectionTestUsesSavedPasswordWithoutEnablingDevice()
    {
        await using var h = new TestHarness();
        var bar = new BusyBarSettings { Id = "remote", Address = "192.168.1.30" };
        var password = Guid.NewGuid().ToString("N");
        await h.Credentials.SetAsync(bar.Id, password, TestContext.Current.CancellationToken);
        h.Http.Respond = _ => FakeHttp.Json(Version);
        Assert.Equal("27.5.0", await h.BusyBars.TestConnectionAsync(bar, token: TestContext.Current.CancellationToken));
        var request = Assert.Single(h.Http.Requests);
        Assert.Equal((HttpMethod.Get, "http://192.168.1.30/api/version", password), (request.Method, request.Uri.AbsoluteUri, request.ApiToken));
        Assert.Equal(GamePhase.Idle, h.Control.Phase);
        Assert.False(bar.Enabled);
    }
    [Fact]
    public async Task EnabledControllerReconnectsWhenItsSavedPasswordChanges()
    {
        // On real time: the service looks for changed settings and passwords once a second.
        await using var h = new TestHarness(TimeProvider.System);
        var bar = new BusyBarSettings { Id = "remote", Address = "192.168.1.30", Enabled = true };
        await h.Store.SaveAsync(new() { Controllers = [bar] }, TestContext.Current.CancellationToken);
        var first = Guid.NewGuid().ToString("N"); var replacement = Guid.NewGuid().ToString("N");
        await h.Credentials.SetAsync(bar.Id, first, TestContext.Current.CancellationToken);
        h.Http.Respond = request => FakeHttp.Json(request.Path == "/api/version" ? Version : "{}");
        bool Authenticated(string password) => h.Http.Requests.Any(request => request.Path == "/api/version" && request.ApiToken == password);
        await h.BusyBars.StartAsync(CancellationToken.None);
        await Wait.Until(() => Authenticated(first));
        await h.Credentials.SetAsync(bar.Id, replacement, TestContext.Current.CancellationToken);
        await Wait.Until(() => Authenticated(replacement));
        Assert.Equal(GamePhase.Idle, h.Control.Phase);
    }
}
