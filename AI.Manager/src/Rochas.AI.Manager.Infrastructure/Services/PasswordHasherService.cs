using System.Security.Cryptography;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Infrastructure.Services;

public sealed class PasswordHasherService : IPasswordHasher
{
    const int Iterations = 100_000;
    public string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
    public bool Verify(string password, string stored)
    {
        try
        {
            string[] parts = stored.Split('.');
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[0]), HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}
