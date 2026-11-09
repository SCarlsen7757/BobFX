using Microsoft.AspNetCore.DataProtection;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BobFx.Core.Services;

/// <summary>Write-only operator credentials, separate from exported installation settings.</summary>
public sealed class ControllerCredentials
{
    private readonly string directory;
    private readonly IDataProtectionProvider provider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> revisions = new();
    public ControllerCredentials(InstallationSettingsStore settings)
    {
        directory = Path.Combine(settings.DirectoryPath, "credentials");
        var keys = Path.Combine(directory, "keys");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keys);
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        provider = DataProtectionProvider.Create(new DirectoryInfo(keys), builder => builder.SetApplicationName("BobFX.ControllerCredentials"));
    }
    private string PathFor(string id) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".protected");
    public bool HasPassword(string id) => File.Exists(PathFor(id));
    public long Revision(string id) => revisions.GetValueOrDefault(id);
    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 256 || password.Any(c => c < 32 || c > 126))
            throw new ArgumentException("Enter a device API password of 1–256 printable ASCII characters.");
    }
    internal string Read(string id)
    {
        using var stream = new FileStream(PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return provider.CreateProtector("BUSYBar", id).Unprotect(reader.ReadToEnd());
    }
    public async Task SetAsync(string id, string password, CancellationToken token = default)
    {
        ValidatePassword(password);
        await gate.WaitAsync(token);
        var temporary = PathFor(id) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var value = provider.CreateProtector("BUSYBar", id).Protect(password);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token);
                await stream.FlushAsync(token); stream.Flush(true);
            }
            File.Move(temporary, PathFor(id), true);
            revisions.AddOrUpdate(id, 1, (_, revision) => revision + 1);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
}
