using YantraJs.Impl;

namespace YantraJs;

public static class YantraJs
{
    public static T? YantraClone<T>(T? obj) => YantraJsGenerator.CloneObject(obj);
    public static TTo? YantraCloneTo<TFrom, TTo>(TFrom? objFrom, TTo? objTo) where TTo : class, TFrom => (TTo?)YantraJsGenerator.CloneObjectTo(objFrom, objTo, true);
    public static TTo? YantraShallowCloneTo<TFrom, TTo>(TFrom? objFrom, TTo? objTo) where TTo : class, TFrom => (TTo?)YantraJsGenerator.CloneObjectTo(objFrom, objTo, false);
    public static T? YantraShallowClone<T>(T? obj) => YantraShallow.CloneObject(obj);
    public static void YantraClearCache() => YantraJsCache.ClearCache();
    public static void IgnoreTypes(IEnumerable<Type> types)
    {
        foreach (Type type in types.ToList())
        {
            YantraJsCache.AlwaysIgnoredTypes.TryAdd(type, true);
        }
    }

    public static void IgnoreType(Type type)
    {
        YantraJsCache.AlwaysIgnoredTypes.TryAdd(type, true);
    }
    
    public static HashSet<Type> GetIgnoredTypes()
    {
#if YANTRA_CORE
        return YantraJsCache.AlwaysIgnoredTypes.Keys.ToHashSet();
#else
        return [..YantraJsCache.AlwaysIgnoredTypes.Keys];
#endif
    }
    
    public static bool IsTypeIgnored(Type type)
    {
        return YantraJsCache.AlwaysIgnoredTypes.TryGetValue(type, out _);
    }
    
    public static void ClearIgnoredTypes()
    {
        YantraJsCache.AlwaysIgnoredTypes.Clear();
        YantraJsCache.ClearCache(); 
    }
}