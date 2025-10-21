namespace YantraJs.Impl;

internal static class YantraShallow
{
    public static T? CloneObject<T>(T obj)
    {
        if (obj is ValueType)
        {
            if (typeof(T) == obj.GetType())
                return obj;
            
            return (T)YantraTracker.CloneObject(obj);
        }

        if (ReferenceEquals(obj, null))
            return (T?)(object?)null;

        if (YantraJsSafeTypes.CanReturnSameObject(obj.GetType()))
            return obj;

        return (T)YantraTracker.CloneObject(obj);
    }
}