namespace Rochas.OpenCodeBridge.Web.Services;

// Contrato do serviço de domínio de senhas (hash + verificação).
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);
}
