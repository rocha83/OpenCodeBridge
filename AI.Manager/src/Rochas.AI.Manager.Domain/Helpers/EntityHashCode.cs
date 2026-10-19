namespace Rochas.AI.Manager.Domain.Helpers;

public static class EntityHashCode
{
    public static uint FromType<T>() => (uint)typeof(T).GetHashCode();
}
