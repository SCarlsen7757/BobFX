using Microsoft.JSInterop;

namespace BobFx.Core.Services;

/// <summary>
/// Service used by server-side code to request the browser to play sounds via JS interop.
/// The service keeps an IJSRuntime instance that Blazor components register with on connect.
/// It will randomly pick an audio file from the appropriate sounds subfolder.
/// </summary>
public class SoundService
{
    private readonly IWebHostEnvironment env;
    private readonly ILogger<SoundService> logger;
    private IJSRuntime? jsRuntime;

    public SoundService(IWebHostEnvironment env, ILogger<SoundService> logger)
    {
        this.env = env ?? throw new ArgumentNullException(nameof(env));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterJsRuntime(IJSRuntime runtime)
    {
        jsRuntime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public void UnregisterJsRuntime(IJSRuntime runtime)
    {
        if (jsRuntime == runtime)
            jsRuntime = null;
    }

    /// <summary>
    /// Play a sound corresponding to the logical key. The method will try to pick a random file
    /// from the appropriate subfolder under wwwroot/sounds.
    /// Supported keys: "start" (game_on), "end" (game_over).
    /// </summary>
    public async Task Play(string soundKey)
    {
        try
        {
            if (jsRuntime == null)
            {
                // Not connected to a browser client; nothing to do.
                return;
            }

            var relativeUrl = ResolveRandomSoundUrl(soundKey);
            // Fall back to sending the key and let client-side handle (legacy behavior)
            relativeUrl ??= "/sounds/" + soundKey + ".mp3";

            await jsRuntime.InvokeVoidAsync("bobfx.playSound", relativeUrl);
        }
        catch (JSException ex)
        {
            // Swallow JS exceptions to keep server resilient but log for diagnostics
            logger.LogDebug(ex, "Failed to invoke JS to play sound {SoundKey}", soundKey);
        }
        catch (Exception ex)
        {
            // Log unexpected exceptions at a higher severity
            logger.LogError(ex, "Unexpected error while trying to play sound {SoundKey}", soundKey);
            throw;
        }
    }

    private string? ResolveRandomSoundUrl(string soundKey)
    {
        try
        {
            string[] candidates = soundKey switch
            {
                "start" => ["game_on"],
                "end" => ["game_over"],
                _ => ["game_over"]
            };

            if (candidates.Length == 0)
                return null;

            string? soundsFolder = null;
            foreach (var candidate in candidates)
            {
                var folder = Path.Combine(env.WebRootPath ?? string.Empty, "sounds", candidate);
                if (Directory.Exists(folder))
                {
                    soundsFolder = folder;
                    break;
                }
            }

            if (soundsFolder == null)
            {
                logger.LogDebug("No matching sounds folder found for key {SoundKey}", soundKey);
                return null;
            }

            var files = Directory.GetFiles(soundsFolder)
            .Where(f => IsAudioFile(f))
            .ToArray();

            if (files.Length == 0)
            {
                logger.LogDebug("No audio files found in folder: {Folder}", soundsFolder);
                return null;
            }

            var chosen = files[Random.Shared.Next(files.Length)];
            // Convert to web-relative path, using forward slashes
            var webRoot = env.WebRootPath ?? string.Empty;
            var relativePath = chosen.StartsWith(webRoot) ? chosen[webRoot.Length..] : chosen;
            relativePath = relativePath.Replace("\\", "/");

            // Ensure it starts with '/'
            if (!relativePath.StartsWith('/'))
                relativePath = "/" + relativePath.TrimStart('/');

            return relativePath;
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Failed to resolve random sound for key {SoundKey}", soundKey);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Failed to resolve random sound for key {SoundKey}", soundKey);
            return null;
        }
        catch (DirectoryNotFoundException ex)
        {
            logger.LogDebug(ex, "Failed to resolve random sound for key {SoundKey}", soundKey);
            return null;
        }
    }

    private static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return ext == ".mp3" || ext == ".wav" || ext == ".ogg" || ext == ".m4a";
    }
}
