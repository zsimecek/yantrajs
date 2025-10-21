using System.Reflection;
using System.Runtime.CompilerServices;

namespace YantraJs.Impl;

internal static class YantraJsGenerator
{
    public static T? CloneObject<T>(T? obj)
    {
        if (obj is null)
        {
            return default;
        }
        
        Type concreteTypeOfObj = obj.GetType();
        Type typeOfT = typeof(T);
        
        if (YantraJsCache.AlwaysIgnoredTypes.ContainsKey(concreteTypeOfObj))
        {
            return default;
        }
        
        if (YantraJsSafeTypes.SafeDefKnownTypes.TryGetValue(concreteTypeOfObj, out _))
        {
            return obj;
        }
        
        switch (obj)
        {
            case ValueType:
            {
                Type type = obj.GetType();
                
                if (typeOfT == type)
                {
                    bool hasIgnoredMembers = YantraJsCache.GetOrAddTypeContainsIgnoredMembers(type, YantraJsExprGen.CalcTypeContainsIgnoredMembers);
                    
                    if (hasIgnoredMembers || !YantraJsSafeTypes.CanReturnSameObject(type))
                    {
                        return CloneStructInternal(obj, new YantraJsState());
                    }
                    
                    return obj;
                }

                break;
            }
            case Delegate del:
            {
                Type? targetType = del.Target?.GetType();
            
                if (targetType?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                {
                    return (T?)CloneClassRoot(obj);
                }
            
                return obj;
            }
        }

        return (T?)CloneClassRoot(obj);
    }

    private static object? CloneClassRoot(object? obj)
    {
        if (obj == null)
            return null;

        Func<object, YantraJsState, object>? cloner = (Func<object, YantraJsState, object>?)YantraJsCache.GetOrAddClass(obj.GetType(), t => GenerateCloner(t, true));

        if (cloner is null)
        {
            return obj;
        }

        YantraJsState state = new YantraJsState();
        object result = cloner(obj, state);

        if (state.IterativeMode)
        {
            while (state.TryDequeueWork(out (object from, object to) item))
            {
                Type t = item.from.GetType();
                var clonerTo = (Func<object, object, YantraJsState, object>)YantraJsCache.GetOrAddDeepClassTo(t, tp => YantraExprGen.GenClonerInternal(tp, true));
                clonerTo(item.from, item.to, state);
            }
        }

        return result;
    }

    internal static object? CloneClassInternal(object? obj, YantraJsState jsState)
    {
        if (obj is null)
        {
            return null;
        }
        
        Type objType = obj.GetType();
        
        if (YantraJsCache.IsTypeIgnored(objType))
        {
            return null;
        }

        if (jsState.IterativeMode)
        {
            object? known = jsState.GetKnownRef(obj);
            if (known is not null)
            {
                return known;
            }

            object? shallow = CloneClassShallowAndTrack(obj, jsState);
            if (shallow is not null)
            {
                jsState.EnqueueWork(obj, shallow);
            }
            return shallow;
        }

        Func<object, YantraJsState, object>? cloner = (Func<object, YantraJsState, object>?)YantraJsCache.GetOrAddClass(objType, t => GenerateCloner(t, true));

        if (cloner is null)
        {
            return obj;
        }

        object? knownRef = jsState.GetKnownRef(obj);
        return knownRef ?? cloner(obj, jsState);
    }
    
    internal static object? CloneClassShallowAndTrack(object? obj, YantraJsState jsState)
    {
        if (obj is null)
        {
            return null;
        }

        Type objType = obj.GetType();

        if (YantraJsCache.IsTypeIgnored(objType))
        {
            return null;
        }
        

        if (YantraJsSafeTypes.CanReturnSameObject(objType) && !objType.IsValueType())
        {
            return obj;
        }

        object? knownRef = jsState.GetKnownRef(obj);
        if (knownRef is not null)
        {
            return knownRef;
        }
        
        if (RequiresSpecializedCloner(objType))
        {
            Func<object, YantraJsState, object>? specialCloner = (Func<object, YantraJsState, object>?)YantraJsCache.GetOrAddClass(objType, t => GenerateCloner(t, true));
            if (specialCloner is not null)
            {
                return specialCloner(obj, jsState);
            }
        }
        
        MethodInfo methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone))!;
        object? shallow = methodInfo.Invoke(obj, null);
        jsState.AddKnownRef(obj, shallow);
        return shallow;
    }
    
    private static bool RequiresSpecializedCloner(Type type)
    {
        return type.IsArray || 
               YantraJsExprGen.IsDictType(type) || 
               YantraJsExprGen.IsSetType(type);
    }

    internal static T CloneStructInternal<T>(T obj, YantraJsState jsState)
    {
        Type typeT = typeof(T);
        Type? underlyingTypeT = Nullable.GetUnderlyingType(typeT);
        
        if (YantraJsCache.AlwaysIgnoredTypes.ContainsKey(typeT) || (underlyingTypeT != null && YantraJsCache.AlwaysIgnoredTypes.ContainsKey(underlyingTypeT)))
        {
            return default;
        }
        
        Func<T, YantraJsState, T>? cloner = GetClonerForValueType<T>();
        return cloner is null ? obj : cloner(obj, jsState);
    }
    
    internal static T[] Clone1DimArraySafeInternal<T>(T[] obj, YantraJsState jsState)
    {
        int l = obj.Length;
        T[] outArray = new T[l];
        jsState.AddKnownRef(obj, outArray);
        Array.Copy(obj, outArray, obj.Length);
        return outArray;
    }

    internal static T[]? Clone1DimArrayStructInternal<T>(T[]? obj, YantraJsState jsState)
    {
        if (obj == null) return null;
        int l = obj.Length;
        T[] outArray = new T[l];
        jsState.AddKnownRef(obj, outArray);
        Func<T, YantraJsState, T> cloner = GetClonerForValueType<T>();
        for (int i = 0; i < l; i++)
            outArray[i] = cloner(obj[i], jsState);

        return outArray;
    }

    internal static T[]? Clone1DimArrayClassInternal<T>(T[]? obj, YantraJsState jsState)
    {
        if (obj == null) return null;
        int l = obj.Length;
        T[] outArray = new T[l];
        jsState.AddKnownRef(obj, outArray);
        for (int i = 0; i < l; i++)
            outArray[i] = (T)CloneClassInternal(obj[i], jsState);

        return outArray;
    }
    
    internal static T[,]? Clone2DimArrayInternal<T>(T[,]? obj, YantraJsState jsState)
    {
        if (obj is null)
        {
            return null;
        }
        
        int lb1 = obj.GetLowerBound(0);
        int lb2 = obj.GetLowerBound(1);
        if (lb1 != 0 || lb2 != 0)
            return (T[,]) CloneAbstractArrayInternal(obj, jsState);

        int l1 = obj.GetLength(0);
        int l2 = obj.GetLength(1);
        T[,] outArray = new T[l1, l2];
        jsState.AddKnownRef(obj, outArray);
        if (YantraJsSafeTypes.CanReturnSameObject(typeof(T)))
        {
            Array.Copy(obj, outArray, obj.Length);
            return outArray;
        }

        if (typeof(T).IsValueType())
        {
            Func<T, YantraJsState, T> cloner = GetClonerForValueType<T>();
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    outArray[i, k] = cloner(obj[i, k], jsState);
        }
        else
        {
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    outArray[i, k] = (T)CloneClassInternal(obj[i, k], jsState);
        }

        return outArray;
    }
    
    internal static Array? CloneAbstractArrayInternal(Array? obj, YantraJsState jsState)
    {
        if (obj == null) return null;
        int rank = obj.Rank;

        int[] lengths = Enumerable.Range(0, rank).Select(obj.GetLength).ToArray();

        int[] lowerBounds = Enumerable.Range(0, rank).Select(obj.GetLowerBound).ToArray();
        int[] idxes = Enumerable.Range(0, rank).Select(obj.GetLowerBound).ToArray();

        Type? elementType = obj.GetType().GetElementType();
        Array outArray = Array.CreateInstance(elementType, lengths, lowerBounds);

        jsState.AddKnownRef(obj, outArray);
        
        if (lengths.Any(x => x == 0))
            return outArray;

        if (YantraJsSafeTypes.CanReturnSameObject(elementType))
        {
            Array.Copy(obj, outArray, obj.Length);
            return outArray;
        }

        int ofs = rank - 1;
        while (true)
        {
            outArray.SetValue(CloneClassInternal(obj.GetValue(idxes), jsState), idxes);
            idxes[ofs]++;

            if (idxes[ofs] >= lowerBounds[ofs] + lengths[ofs])
            {
                do
                {
                    if (ofs == 0) return outArray;
                    idxes[ofs] = lowerBounds[ofs];
                    ofs--;
                    idxes[ofs]++;
                } while (idxes[ofs] >= lowerBounds[ofs] + lengths[ofs]);

                ofs = rank - 1;
            }
        }
    }

    internal static Func<T, YantraJsState, T>? GetClonerForValueType<T>() => (Func<T, YantraJsState, T>?)YantraJsCache.GetOrAddStructAsObject(typeof(T), t => GenerateCloner(t, false));

    private static object? GenerateCloner(Type t, bool asObject)
    {
        if (YantraJsSafeTypes.CanReturnSameObject(t) && asObject && !t.IsValueType())
            return null;

        return YantraJsExprGen.GenClonerInternal(t, asObject);
    }

    public static object? CloneObjectTo(object? objFrom, object? objTo, bool isDeep)
    {
        if (objTo == null) return null;

        if (objFrom == null)
            throw new ArgumentNullException(nameof(objFrom), "Cannot copy null");
        Type type = objFrom.GetType();
        if (!type.IsInstanceOfType(objTo))
            throw new InvalidOperationException("From should be derived from From object, but From object has type " + objFrom.GetType().FullName + " and to " + objTo.GetType().FullName);
        if (objFrom is string)
            throw new InvalidOperationException("Forbidden to clone strings");
        Func<object, object, YantraJsState, object>? cloner = (Func<object, object, YantraJsState, object>?)(isDeep
            ? YantraJsCache.GetOrAddDeepClassTo(type, t => YantraExprGen.GenClonerInternal(t, true))
            : YantraJsCache.GetOrAddShallowClassTo(type, t => YantraExprGen.GenClonerInternal(t, false)));
        return cloner is null ? objTo : cloner(objFrom, objTo, new YantraJsState());
    }
}