using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Access;

/// <summary>
/// Time-based one-time passwords (RFC 6238 over RFC 4226): HMAC-SHA1, 30-second steps, 6 digits, the format every
/// authenticator app (Google Authenticator, Aegis, 1Password, Bitwarden, Microsoft Authenticator) takes from an
/// <c>otpauth://totp/...</c> link. The owner's sign-in account on a host uses it as its second factor.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    /// <summary>A fresh 160-bit secret in Base32 (what authenticator apps take when typed in by hand).</summary>
    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    public static long Step(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for <paramref name="step"/> (RFC 4226 dynamic truncation, zero-padded).</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, step >>= 8) counter[i] = (byte)(step & 0xFF);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = (hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Code(string base32Secret, DateTimeOffset at) => Code(Base32.Decode(base32Secret), Step(at));

    /// <summary>Checks <paramref name="code"/> against the steps one before and after <paramref name="at"/> (clock drift), never
    /// at or before <paramref name="lastUsedStep"/> (each code works once). Returns the matched step, or null.</summary>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset at, long lastUsedStep)
    {
        var digits = new string((code ?? "").Where(c => c is not (' ' or '-')).ToArray());
        if (digits.Length != Digits || !digits.All(char.IsAsciiDigit)) return null;
        var secret = Base32.Decode(base32Secret);
        try
        {
            var now = Step(at);
            long? matched = null;
            for (var step = now - 1; step <= now + 1; step++)
            {
                var expected = Encoding.ASCII.GetBytes(Code(secret, step));
                if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(digits)) && step > lastUsedStep && matched is null)
                    matched = step;
            }
            return matched;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>The link an authenticator app scans or opens: otpauth://totp/Issuer:account?secret=...&amp;issuer=Issuer.</summary>
    public static string Uri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={base32Secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 Base32 without padding (A-Z, 2-7), as authenticator apps write TOTP secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var value in bytes)
        {
            buffer = buffer << 8 | value;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[buffer >> (bits - 5) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) builder.Append(Alphabet[buffer << (5 - bits) & 31]);
        return builder.ToString();
    }

    /// <summary>Decodes Base32, ignoring case, spaces, dashes and padding. Throws <see cref="FormatException"/> otherwise.</summary>
    public static byte[] Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var character in text)
        {
            if (character is ' ' or '-' or '=') continue;
            var index = Alphabet.IndexOf(char.ToUpperInvariant(character));
            if (index < 0) throw new FormatException("Not Base32.");
            buffer = buffer << 5 | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
