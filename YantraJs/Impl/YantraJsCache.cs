using System.Collections.Concurrent;
using System.Reflection;

namespace YantraJs.Impl;

internal static class YantraJsCache
{
    internal static readonly ConcurrentDictionary<Type, bool> AlwaysIgnoredTypes = [];

    internal static bool IsTypeIgnored(Type type)
    {
        return AlwaysIgnoredTypes.TryGetValue(type, out _);
    }
    
    private static readonly ClrCache<object?> clsCache = new ClrCache<object?>();
    private static readonly ClrCache<object?> structsCache = new ClrCache<object?>();
    private static readonly ClrCache<object> deepClsToCache = new ClrCache<object>();
    private static readonly ClrCache<object> shallowClsToCache = new ClrCache<object>();
    private static readonly ConcurrentLazyCache<object> typeConvertersCache = new ConcurrentLazyCache<object>();
    private static readonly GenericClrCache<Tuple<Type, string>, object?> fieldCache = new GenericClrCache<Tuple<Type, string>, object?>();
    private static readonly ClrCache<Dictionary<string, Type>> ignoredEventCache = new ClrCache<Dictionary<string, Type>>();
    private static readonly ClrCache<List<MemberInfo>> allMmbrsCache = new ClrCache<List<MemberInfo>>();
    private static readonly GenericClrCache<MemberInfo, bool> mmbrsIgnoreStatusCache = new GenericClrCache<MemberInfo, bool>();
    private static readonly ClrCache<bool> typeContainsIgnoredMmbrCache = new ClrCache<bool>();
    private static readonly ClrCache<object> specialTypesCache = new ClrCache<object>();

    public static object? GetOrAddField(Type type, string name, Func<Type, object?> valueFactory) => fieldCache.GetOrAdd(new Tuple<Type, string>(type, name), k => valueFactory(k.Item1));
    public static object? GetOrAddClass(Type type, Func<Type, object?> valueFactory) => clsCache.GetOrAdd(type, valueFactory);
    public static object? GetOrAddStructAsObject(Type type, Func<Type, object?> valueFactory) => structsCache.GetOrAdd(type, valueFactory);
    public static object GetOrAddDeepClassTo(Type type, Func<Type, object> valueFactory) => deepClsToCache.GetOrAdd(type, valueFactory);
    public static object GetOrAddShallowClassTo(Type type, Func<Type, object> valueFactory) => shallowClsToCache.GetOrAdd(type, valueFactory);
    public static T GetOrAddConvertor<T>(Type from, Type to, Func<Type, Type, T> valueFactory) => (T)typeConvertersCache.GetOrAdd(from, to, (f, t) => valueFactory(f, t));
    public static Dictionary<string, Type> GetOrAddIgnoredEventInfo(Type type, Func<Type, Dictionary<string, Type>> valueFactory) => ignoredEventCache.GetOrAdd(type, valueFactory);
    public static List<MemberInfo> GetOrAddAllMembers(Type type, Func<Type, List<MemberInfo>> valueFactory) => allMmbrsCache.GetOrAdd(type, valueFactory);
    public static bool GetOrAddMemberIgnoreStatus(MemberInfo memberInfo, Func<MemberInfo, bool> valueFactory) => mmbrsIgnoreStatusCache.GetOrAdd(memberInfo, valueFactory);
    public static bool GetOrAddTypeContainsIgnoredMembers(Type type, Func<Type, bool> valueFactory)
    {
        return type.IsValueType && typeContainsIgnoredMmbrCache.GetOrAdd(type, valueFactory);
    }
    public static object GetOrAddSpecialType(Type type, Func<Type, object> valueFactory) => specialTypesCache.GetOrAdd(type, valueFactory);
    
    public static void ClearCache()
    {
        clsCache.Clear();
        structsCache.Clear();
        deepClsToCache.Clear();
        shallowClsToCache.Clear();
        typeConvertersCache.Clear();
        fieldCache.Clear();
        ignoredEventCache.Clear();
        allMmbrsCache.Clear();
        mmbrsIgnoreStatusCache.Clear();
        typeContainsIgnoredMmbrCache.Clear();
        specialTypesCache.Clear();
    }
    
    private class GenericClrCache<TKey, TValue> where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, Lazy<TValue>> cache = new ConcurrentDictionary<TKey, Lazy<TValue>>();

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            Lazy<TValue> lazy = cache.GetOrAdd(key, k => new Lazy<TValue>(() => valueFactory(k), LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        public void Clear() => cache.Clear();
    }
    
    private class ClrCache<TValue>
    {
        private readonly ConcurrentDictionary<Type, Lazy<TValue>> cache = new ConcurrentDictionary<Type, Lazy<TValue>>();

        public TValue GetOrAdd(Type type, Func<Type, TValue> valueFactory)
        {
            Lazy<TValue> lazy = cache.GetOrAdd(type, t => new Lazy<TValue>(() => valueFactory(t), LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        public void Clear() => cache.Clear();
    }

    private class ConcurrentLazyCache<TValue>
    {
        private readonly ConcurrentDictionary<Tuple<Type, Type>, Lazy<TValue>> cache = [];

        public TValue GetOrAdd(Type from, Type to, Func<Type, Type, TValue> valueFactory)
        {
            Tuple<Type, Type> key = new Tuple<Type, Type>(from, to);
            Lazy<TValue> lazy = cache.GetOrAdd(key, tuple => new Lazy<TValue>(() => valueFactory(tuple.Item1, tuple.Item2), LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        public void Clear() => cache.Clear();
    }
}
