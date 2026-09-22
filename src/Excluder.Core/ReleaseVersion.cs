using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Excluder.Core;

public sealed record ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string text)
    {
        var match = Regex.Match(text, @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success) throw new InvalidDataException("Not a stable semantic version.");
        return new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }
    public int CompareTo(ReleaseVersion? other) => other == null ? 1 :
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public static class Integrity
{
    public static string ParseChecksum(string text)
    {
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !Regex.IsMatch(parts[0], "^[a-fA-F0-9]{64}$") ||
            (parts.Length == 2 && parts[1].TrimStart('*') != "143OWSwitch.exe"))
            throw new InvalidDataException("Invalid update checksum.");
        return parts[0].ToLowerInvariant();
    }
    public static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    public static void Verify(string path, string expected)
    {
        if (!string.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update verification failed.");
    }
}
