namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);
}
