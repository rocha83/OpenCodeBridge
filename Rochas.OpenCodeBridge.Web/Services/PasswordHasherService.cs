namespace Rochas.OpenCodeBridge.Web.Services;

// Serviço de senhas (PBKDF2; delega às primitivas estáticas puras).
public sealed class PasswordHasherService : IPasswordHasher
{
    public string Hash(string password) => PasswordHasher.Hash(password);
    public bool Verify(string password, string stored) => PasswordHasher.Verify(password, stored);
}
