using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace YantraJs.Impl;

internal static class YantraJsSafeTypes
{
    internal static readonly Dictionary<Type, bool> SafeDefKnownTypes = new Dictionary<Type, bool>(34)
    {
        [typeof(byte)] = true,
        [typeof(short)] = true,
        [typeof(ushort)] = true,
        [typeof(int)] = true,
        [typeof(uint)] = true,
        [typeof(long)] = true,
        [typeof(ulong)] = true,
        [typeof(float)] = true,
        [typeof(double)] = true,
        [typeof(decimal)] = true,
        [typeof(string)] = true,
        [typeof(char)] = true,
        [typeof(bool)] = true,
        [typeof(sbyte)] = true,
        [typeof(nint)] = true,
        [typeof(nuint)] = true,
        [typeof(Guid)] = true,
        
#if YANTRA_CORE
        [typeof(Rune)] = true,
        [typeof(Half)] = true,
        [typeof(Int128)] = true,
        [typeof(UInt128)] = true,
        [typeof(Complex)] = true,
        [typeof(DateOnly)] = true,
        [typeof(TimeOnly)] = true,
        [typeof(Range)] = true,
        [typeof(Index)] = true,
#endif
        
        [typeof(TimeSpan)] = true,
        [typeof(TimeZoneInfo)] = true,
        [typeof(DateTime)] = true,
        [typeof(DateTimeOffset)] = true,
        [typeof(DBNull)] = true,
        [StringComparer.Ordinal.GetType()] = true,
        [StringComparer.OrdinalIgnoreCase.GetType()] = true,
        [StringComparer.InvariantCulture.GetType()] = true,
        [StringComparer.InvariantCultureIgnoreCase.GetType()] = true,
    };

    private static readonly ConcurrentDictionary<Type, bool> knownTypes = [];

    static YantraJsSafeTypes()
    {
        foreach (KeyValuePair<Type, bool> x in SafeDefKnownTypes)
        {
            knownTypes.TryAdd(x.Key, x.Value);
        }
        
        List<Type?> safeTypes =
        [
            Type.GetType("System.RuntimeType"),
            Type.GetType("System.RuntimeTypeHandle")
        ];

        foreach (Type x in safeTypes.OfType<Type>())
        {
            knownTypes.TryAdd(x, true);
        }
    }
    
    private static bool IsSpecialEqCmp(string fullName) => fullName switch
    {
        _ when fullName.StartsWith("System.Collections.Generic.GenericEqualityComparer`") => true,
        _ when fullName.StartsWith("System.Collections.Generic.ObjectEqualityComparer`") => true,
        _ when fullName.StartsWith("System.Collections.Generic.EnumEqualityComparer`") => true,
        _ when fullName.StartsWith("System.Collections.Generic.NullableEqualityComparer`") => true,
        "System.Collections.Generic.ByteEqualityComparer" => true,
        _ => false
    };
    
    private static class TypePrefixes
    {
        public const string SystemReflection = "System.Reflection.";
        public const string SystemRuntimeType = "System.RuntimeType";
        public const string MicrosoftExtensions = "Microsoft.Extensions.DependencyInjection.";
    }

    private static readonly Assembly propertyInfoAssembly = typeof(PropertyInfo).Assembly;
    
    private static bool IsReflectionType(Type type)
    {
        if (type == typeof(AssemblyName))
        {
            return false;
        }
    
        return type.FullName?.StartsWith(TypePrefixes.SystemReflection) is true && Equals(type.GetTypeInfo().Assembly, typeof(PropertyInfo).GetTypeInfo().Assembly);
    }

    private static IEnumerable<FieldInfo> GetAllTypeFields(Type type)
    {
        Type? currentType = type;
    
        while (currentType is not null)
        {
            foreach (FieldInfo field in currentType.GetAllFields())
            {
                yield return field;
            }
            
            currentType = currentType.BaseType();
        }
    }
    
    private static bool IsSafeSysType(Type type)
    {
        if (type.IsEnum() || type.IsPointer)
            return true;

        if (type.IsCOMObject)
            return true;

        if (type.FullName is null)
            return true;

        if (IsReflectionType(type))
            return true;

        if (type.IsSubclassOf(typeof(System.Runtime.ConstrainedExecution.CriticalFinalizerObject)))
            return true;

        if (type.FullName.StartsWith(TypePrefixes.SystemRuntimeType))
            return true;

        if (type.FullName.StartsWith(TypePrefixes.MicrosoftExtensions))
            return true;

        return type.FullName is "Microsoft.EntityFrameworkCore.Internal.ConcurrencyDetector";
    }
    
    private static bool CanReturnSameType(Type type, HashSet<Type>? processingTypes = null)
    {
        if (knownTypes.TryGetValue(type, out bool isSafe))
        {
            return isSafe;
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            knownTypes.TryAdd(type, false);
            return false;
        }
        
        if (type.FullName is null || IsSafeSysType(type) || type.FullName.Contains("EqualityComparer") && IsSpecialEqCmp(type.FullName))
        {
            knownTypes.TryAdd(type, true);
            return true;
        }

        if (!type.IsValueType())
        {
            knownTypes.TryAdd(type, false);
            return false;
        }

        processingTypes ??= [];

        if (!processingTypes.Add(type))
        {
            return true;
        }

        if (GetAllTypeFields(type).Select(fieldInfo => fieldInfo.FieldType).Where(fieldType => !processingTypes.Contains(fieldType)).Any(fieldType => !CanReturnSameType(fieldType, processingTypes)))
        {
            knownTypes.TryAdd(type, false);
            return false;
        }

        knownTypes.TryAdd(type, true);
        return true;
    }

    public static bool CanReturnSameObject(Type type) => CanReturnSameType(type);
}