using System.Text;

namespace Dare.Arenas.Core;

public enum UploadKind { Map, Lvz }

/// <summary>Validation of owner-uploaded map and graphics files.</summary>
public static class UploadPolicy
{
    private const int MaxMapBytes = 2 * 1024 * 1024;
    private const int MaxLvzBytes = 5 * 1024 * 1024;
    private const int MaxLvzCount = 10;

    public static bool TryCheck(UploadKind kind, string fileName, ReadOnlySpan<byte> content, int existingLvzCount, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny(new[] { '/', '\\' }) >= 0
            || Path.GetFileName(fileName) != fileName)
        {
            error = "File name must not contain a directory.";
            return false;
        }

        var ext = Path.GetExtension(fileName);
        if (kind == UploadKind.Map)
        {
            if (!ext.Equals(".lvl", StringComparison.OrdinalIgnoreCase))
            {
                error = "Map files must end in .lvl.";
                return false;
            }
            if (content.Length > MaxMapBytes)
            {
                error = "Map files must be 2 MB or smaller.";
                return false;
            }
            if (!(StartsWith(content, "BM") || StartsWith(content, "elvl")
                  || (content.Length > 0 && content.Length % 4 == 0)))
            {
                error = "File does not look like a valid .lvl map.";
                return false;
            }
            return true;
        }

        if (!ext.Equals(".lvz", StringComparison.OrdinalIgnoreCase))
        {
            error = "Graphics files must end in .lvz.";
            return false;
        }
        if (content.Length > MaxLvzBytes)
        {
            error = "Graphics files must be 5 MB or smaller.";
            return false;
        }
        if (!StartsWith(content, "CONT"))
        {
            error = "File does not look like a valid .lvz file.";
            return false;
        }
        if (existingLvzCount >= MaxLvzCount)
        {
            error = "An arena can have at most 10 .lvz files.";
            return false;
        }
        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> content, string magic) =>
        content.StartsWith(Encoding.ASCII.GetBytes(magic));
}
