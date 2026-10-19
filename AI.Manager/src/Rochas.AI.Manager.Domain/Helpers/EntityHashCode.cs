namespace Rochas.AI.Manager.Domain.Helpers;

public static class EntityHashCode
{
    public static uint FromType<T>() where T : class
    {
        var fullName = typeof(T).FullName ?? typeof(T).Name;
        return Compute(fullName);
    }

    public static uint Compute(string value)
    {
        unchecked
        {
            uint hash = 5381;
            foreach (var c in value)
            {
                hash = ((hash << 5) + hash) + c;
            }
            return hash;
        }
    }
}
