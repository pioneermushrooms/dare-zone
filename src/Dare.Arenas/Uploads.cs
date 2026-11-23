using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Map (.lvl) and graphics (.lvz) uploads for owned arenas. StoreUpload and RemoveLvz are the
// shared code paths for the in-game commands (Continuum file transfer, ?rmlvz) and the web page.
public sealed partial class ArenaOwnership
{
    internal const int MaxLevelFiles = 10;

    // The server reads the graphics list and files when an arena starts (MapData/MapNewsDownload).
    private const string GraphicsTakeEffect = "It takes effect after ?recyclearena or when the arena next starts.";
    private const long MaxTransferBytes = 5 * 1024 * 1024; // the larger of the two upload limits

    /// <summary>
    /// Checks and stores an uploaded file in <c>arenas/&lt;arenaName&gt;/</c> and updates the arena's
    /// config. <paramref name="cfg"/> is the handle from <see cref="OpenArenaConfigAsync"/>;
    /// <paramref name="arenaName"/> must come from the store; <paramref name="fileName"/> is the
    /// uploader's plain file name (no directory). Map → <c>&lt;arena&gt;.lvl</c> (replaces) and
    /// <c>General:Map</c>; lvz → keeps its name and is appended to <c>General:LevelFiles</c>, or, when
    /// that name is already listed, replaces the file in the same slot. Messages are short and
    /// contain no server paths; details go to the log. Mainloop thread.
    /// </summary>
    internal Result StoreUpload(ConfigHandle cfg, string arenaName, string actor, UploadKind kind, string fileName, ReadOnlySpan<byte> content)
    {
        if (kind == UploadKind.Lvz && !LevelFiles.IsSafeFileName(fileName))
            return new Result(false, $"Graphics file names may use only letters, digits, '.', '_' and '-' (up to {LevelFiles.MaxFileNameLength} characters).");

        var currentLevelFiles = _configManager.GetStr(cfg, "General", "LevelFiles");
        var existingEntry = kind == UploadKind.Lvz ? LevelFiles.Find(currentLevelFiles, fileName) : null;
        var lvzCount = LevelFiles.Parse(currentLevelFiles).Count;
        // a replacement takes the slot it already has
        if (!UploadPolicy.TryCheck(kind, fileName, content, existingEntry is null ? lvzCount : lvzCount - 1, out var error))
            return new Result(false, error);

        string updatedLevelFiles = currentLevelFiles ?? "";
        if (kind == UploadKind.Lvz && existingEntry is null
            && !LevelFiles.Add(currentLevelFiles, fileName, MaxLevelFiles, out updatedLevelFiles, out error))
            return new Result(false, error);

        // a replacement keeps the listed spelling, so the list and the file name match on Linux
        var storedName = kind == UploadKind.Map ? arenaName + ".lvl" : existingEntry ?? fileName;
        var target = ArenaPaths.Resolve(ArenasDir, arenaName, storedName);
        var temp = ArenaPaths.Resolve(ArenasDir, arenaName, $".upload-{Guid.NewGuid():N}.tmp");
        if (target is null || temp is null)
        {
            Log(LogLevel.Warn, $"{actor}: upload '{fileName}' to [{arenaName}] refused: path did not resolve inside the arena folder.");
            return new Result(false, "That file name can't be used.");
        }
        if (!Directory.Exists(Path.GetDirectoryName(target)))
        {
            Log(LogLevel.Error, $"{actor}: upload to [{arenaName}] failed: arena folder '{Path.GetDirectoryName(target)}' is missing.");
            return new Result(false, "Your arena's folder is missing; ask staff.");
        }
        try
        {
            File.WriteAllBytes(temp, content.ToArray());
            File.Move(temp, target, overwrite: true); // map: always <arena>.lvl; lvz: same name = replace
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            Log(LogLevel.Error, $"{actor}: storing upload '{fileName}' as '{target}' failed: {ex.Message}");
            return new Result(false, "The file could not be saved; staff can see the details in the log.");
        }

        if (kind == UploadKind.Map)
        {
            WriteArenaSetting(cfg, arenaName, "General", "Map", storedName, $"{actor} uploaded a map with Dare.Arenas");
            Log(LogLevel.Info, $"{actor} uploaded map '{fileName}' ({content.Length} bytes) to [{arenaName}] as {storedName}.");
            return new Result(true, $"Map saved as {storedName} ({content.Length} bytes) and set as this arena's map; it takes effect after ?recyclearena or when the arena next starts.");
        }

        if (existingEntry is not null)
        {
            Log(LogLevel.Info, $"{actor} replaced graphics '{storedName}' ({content.Length} bytes) in [{arenaName}]; LevelFiles unchanged.");
            return new Result(true, $"Graphics {storedName} replaced ({content.Length} bytes); {LevelFiles.Parse(updatedLevelFiles).Count}/{MaxLevelFiles} files. {GraphicsTakeEffect}");
        }

        WriteArenaSetting(cfg, arenaName, "General", "LevelFiles", updatedLevelFiles, $"{actor} uploaded {storedName} with Dare.Arenas");
        Log(LogLevel.Info, $"{actor} uploaded graphics '{storedName}' ({content.Length} bytes) to [{arenaName}]; LevelFiles = {updatedLevelFiles}.");
        return new Result(true, $"Graphics {storedName} saved ({content.Length} bytes); {LevelFiles.Parse(updatedLevelFiles).Count}/{MaxLevelFiles} files. {GraphicsTakeEffect}");
    }

    /// <summary>
    /// Removes a graphics file: takes it out of <c>General:LevelFiles</c> (written to
    /// <paramref name="cfg"/>, the arena's own arena.conf) and deletes it from the arena folder. The
    /// shared path for ?rmlvz and the web page. Mainloop thread.
    /// </summary>
    internal Result RemoveLvz(ConfigHandle cfg, string arenaName, string actor, string fileName)
    {
        var current = _configManager.GetStr(cfg, "General", "LevelFiles");
        if (!LevelFiles.Remove(current, fileName, out var updated, out var removed, out var error))
            return new Result(false, error);

        WriteArenaSetting(cfg, arenaName, "General", "LevelFiles", updated, $"{actor} removed {removed} with Dare.Arenas");
        Log(LogLevel.Info, $"{actor} removed graphics '{removed}' from [{arenaName}]; LevelFiles = {updated}.");

        // Only files of the safe shape that resolve inside the arena folder are deleted.
        var path = LevelFiles.IsSafeFileName(removed) ? ArenaPaths.Resolve(ArenasDir, arenaName, removed) : null;
        if (path is not null && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warn, $"{actor}: could not delete '{path}' after removing it from LevelFiles: {ex.Message}");
            }
        }
        return new Result(true, $"Graphics {removed} removed; {LevelFiles.Parse(updated).Count}/{MaxLevelFiles} files. {GraphicsTakeEffect}");
    }

    /// <summary>
    /// ?arenamap / ?putlvz: asks the Continuum client for the file, then runs <see cref="StoreUpload"/>
    /// on the arena's own arena.conf. The role is re-checked after the transfer, and the server's
    /// temp file is always deleted.
    /// </summary>
    private async void RequestUpload(Player player, string arenaName, UploadKind kind, string clientPath, string fileName)
    {
        var playerName = player.Name!;
        string? tempFile = null;
        ConfigHandle? cfg = null;
        try
        {
            // ConfigureAwait(true): continue on the mainloop (the server installs a SynchronizationContext).
            tempFile = await _fileTransfer.RequestFileAsync(player, clientPath.AsMemory()).ConfigureAwait(true);

            if (!ReferenceEquals(_playerData.FindPlayer(playerName), player))
                return; // left while uploading

            if (tempFile is null)
            {
                Reply(player, $"The transfer of '{fileName}' failed or was cancelled (does the file exist?).");
                return;
            }

            var arena = player.Arena;
            if (arena is null
                || !string.Equals(arena.BaseName, arenaName, StringComparison.OrdinalIgnoreCase)
                || Store.RoleIn(arenaName, playerName) != "owner")
            {
                Reply(player, "Upload discarded: you must stay in your own arena, as its owner, until it finishes.");
                return;
            }

            var length = new FileInfo(tempFile).Length;
            if (length > MaxTransferBytes)
            {
                Reply(player, kind == UploadKind.Map ? "Map files must be 2 MB or smaller." : "Graphics files must be 5 MB or smaller.");
                return;
            }

            var bytes = await File.ReadAllBytesAsync(tempFile).ConfigureAwait(true);
            cfg = await OpenArenaConfigAsync(arenaName).ConfigureAwait(true);
            if (!ReferenceEquals(_playerData.FindPlayer(playerName), player))
                return;
            if (cfg is null)
            {
                Log(LogLevel.Warn, $"{playerName}: upload to [{arenaName}] refused: arenas/{arenaName}/{ArenaConfigFile.FileName} is missing.");
                Reply(player, ConfigMissingMessage);
                return;
            }
            if (Store.RoleIn(arenaName, playerName) != "owner")
            {
                Reply(player, "Upload discarded: you are no longer this arena's owner.");
                return;
            }
            var result = StoreUpload(cfg, arenaName, playerName, kind, fileName, bytes);
            Reply(player, result.Message);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Upload of '{fileName}' by {playerName} to [{arenaName}] failed: {ex}");
            if (ReferenceEquals(_playerData.FindPlayer(playerName), player))
                Reply(player, "The upload failed; staff can see the details in the log.");
        }
        finally
        {
            if (cfg is not null)
                CloseArenaConfig(cfg);
            if (tempFile is not null)
                TryDelete(tempFile);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"Could not delete temp file '{path}': {ex.Message}");
        }
    }
}
