using System.Security.Cryptography;
using System.Text;

namespace TaskHub.Api.Security;

/// <summary>
/// The bearer token clients must send. Generated once (256 random bits) and kept in a file in the
/// user's local app-data folder, which only that user (and admins) can read.
/// </summary>
public sealed class ApiTokenStore
{
    private byte[] _tokenBytes;

    public ApiTokenStore(string tokenFile)
    {
        TokenFile = tokenFile;
        Token = LoadOrCreate(tokenFile);
        _tokenBytes = Encoding.UTF8.GetBytes(Token);
    }

    public string TokenFile { get; }
    public string Token { get; private set; }

    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var bytes = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(bytes, Volatile.Read(ref _tokenBytes));
    }

    /// <summary>Issues a new token; existing clients must re-read the token file.</summary>
    public void Rotate()
    {
        Token = Generate();
        Write(TokenFile, Token);
        Volatile.Write(ref _tokenBytes, Encoding.UTF8.GetBytes(Token));
    }

    private static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        var token = Generate();
        Write(path, token);
        return token;
    }

    private static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Write(string path, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token);
    }
}
