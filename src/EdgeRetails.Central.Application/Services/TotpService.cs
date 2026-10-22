using System.Security.Cryptography;
using EdgeRetails.Central.Application.Abstractions;

namespace EdgeRetails.Central.Application.Services;

public class TotpService : ITotpService
{
    private const int TimeStepSeconds = 30;
    private const int Digits = 6;
    private static readonly DateTimeOffset Epoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    public string GenerateCode(string secret, DateTimeOffset timestamp)
    {
        var timeStep = GetTimeStep(timestamp);
        return ComputeCode(secret, timeStep);
    }

    public bool VerifyCode(string secret, string code, DateTimeOffset timestamp, int toleranceSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code) || code.Length != Digits)
        {
            return false;
        }

        if (toleranceSteps < 0 || toleranceSteps > 2 || code.Any(c => c < '0' || c > '9'))
        {
            return false;
        }

        if (secret.Any(c => !"ABCDEFGHIJKLMNOPQRSTUVWXYZ234567=".Contains(char.ToUpperInvariant(c))))
        {
            return false;
        }
        var currentStep = GetTimeStep(timestamp);
        for (var stepOffset = -toleranceSteps; stepOffset <= toleranceSteps; stepOffset++)
        {
            var calculated = ComputeCode(secret, currentStep + stepOffset);
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(calculated),
                System.Text.Encoding.UTF8.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    private static long GetTimeStep(DateTimeOffset timestamp)
    {
        var seconds = (long)(timestamp.ToUniversalTime() - Epoch).TotalSeconds;
        return seconds / TimeStepSeconds;
    }

    private static string ComputeCode(string secret, long timeStep)
    {
        var key = Base32Decode(secret);
        var stepBytes = BitConverter.GetBytes(timeStep);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(stepBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(stepBytes);

        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString(new string('0', Digits));
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int currentByte = 0, currentBit = 0;

        while (currentByte < data.Length)
        {
            int val = 0;
            for (var bit = 0; bit < 5; bit++)
            {
                if (currentByte >= data.Length)
                {
                    val <<= (5 - bit);
                    break;
                }
                val = (val << 1) | ((data[currentByte] >> (7 - currentBit)) & 1);
                currentBit++;
                if (currentBit == 8)
                {
                    currentBit = 0;
                    currentByte++;
                }
            }
            result.Append(alphabet[val]);
        }

        return result.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleanInput = input.Trim().ToUpperInvariant().TrimEnd('=');
        var output = new List<byte>();

        int buffer = 0, bitsLeft = 0;
        foreach (var c in cleanInput)
        {
            var val = alphabet.IndexOf(c);
            if (val < 0)
            {
                throw new FormatException("Invalid Base32 MFA secret.");
            }
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)(buffer >> (bitsLeft - 8)));
                bitsLeft -= 8;
            }
        }

        return output.ToArray();
    }
}
