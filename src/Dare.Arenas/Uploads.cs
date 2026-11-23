using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Map (.lvl) and graphics (.lvz) uploads for owned arenas. StoreUpload is the shared code path
// for the in-game commands (Continuum file transfer) and the web upload (Task 7).
public sealed partial class ArenaOwnership
{
    internal const int MaxLevelFiles = 10;
    private const long MaxTransferBytes = 5 * 1024 * 1024; // the larger of the two upload limits

    /// <summary>
    /// Checks and stores an uploaded file in <c>arenas/&lt;arenaName&gt;/</c> and updates the arena's
    /// config. <paramref name="arenaName"/> must come from the store; <paramref name="fileName"/> is
    /// the uploader's plain file name (no directory). Map → <c>&lt;arena&gt;.lvl</c> (replaces) and
    /// <c>General:Map</c>; lvz → keeps its name (never replaces) and is appended to <c>General:LevelFiles</c>.
    /// Messages are short and contain no server paths; details go to the log. Mainloop thread.
    /// </summary>
    internal Result StoreUpload(ConfigHandle cfg, string arenaName, string actor, UploadKind kind, string fileName, ReadOnlySpan<byte> content)
    {
        if (kind == UploadKind.Lvz && !LevelFiles.IsSafeFileName(fileName))
            return new Result(false, $"Graphics file names may use only letters, digits, '.', '_' and '-' (up to {LevelFiles.MaxFileNameLength} characters).");

        var currentLevelFiles = _configManager.GetStr(cfg, "General", "LevelFiles");
        var lvzCount = LevelFiles.Parse(currentLevelFiles).Count;
        if (!UploadPolicy.TryCheck(kind, fileName, content, lvzCount, out var error))
            return new Result(false, error);

        string updatedLevelFiles = "";
        if (kind == UploadKind.Lvz && !LevelFiles.Add(currentLevelFiles, fileName, MaxLevelFiles, out updatedLevelFiles, out error))
            return new Result(false, error);

        var storedName = kind == UploadKind.Map ? arenaName + ".lvl" : fileName;
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
        if (kind == UploadKind.Lvz && File.Exists(target))
            return new Result(false, $"{storedName} already exists in your arena; use a different file name.");

        try
        {
            File.WriteAllBytes(temp, content.ToArray());
            File.Move(temp, target, overwrite: kind == UploadKind.Map);
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
            return new Result(true, $"Map saved as {storedName} ({content.Length} bytes) and set as this arena's map. Players get it on their next entry.");
        }

        WriteArenaSetting(cfg, arenaName, "General", "LevelFiles", updatedLevelFiles, $"{actor} uploaded {storedName} with Dare.Arenas");
        Log(LogLevel.Info, $"{actor} uploaded graphics '{storedName}' ({content.Length} bytes) to [{arenaName}]; LevelFiles = {updatedLevelFiles}.");
        return new Result(true, $"Graphics {storedName} saved ({content.Length} bytes); {LevelFiles.Parse(updatedLevelFiles).Count}/{MaxLevelFiles} files.");
    }

    /// <summary>
    /// ?arenamap / ?putlvz: asks the Continuum client for the file, then runs <see cref="StoreUpload"/>.
    /// The role is re-checked after the transfer, and the server's temp file is always deleted.
    /// </summary>
    private async void RequestUpload(Player player, string arenaName, UploadKind kind, string clientPath, string fileName)
    {
        var playerName = player.Name!;
        string? tempFile = null;
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
                || Store.RoleIn(arenaName, playerName) != "owner"
                || arena.Cfg is null)
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
            var result = StoreUpload(arena.Cfg, arenaName, playerName, kind, fileName, bytes);
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
