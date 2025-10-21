using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
#if NET8_0_OR_GREATER
using System.Collections.Frozen;
#endif

namespace YantraJs.Impl;

internal static class YantraJsExprGen
{
    internal static readonly ConcurrentDictionary<Type, Func<Type, bool, ExpressionPosition, object>> typeHandlers = [];
    private static readonly ConcurrentDictionary<FieldInfo, bool> readonlyFields = new ConcurrentDictionary<FieldInfo, bool>();
    private static readonly MethodInfo fieldSetMethod;
    private static readonly Lazy<MethodInfo> typeIgnoredInfo = new Lazy<MethodInfo>(() => typeof(YantraJsCache).GetMethod(nameof(YantraJsCache.IsTypeIgnored), BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, [typeof(Type)], null)!, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static MethodInfo IsTypeIgnoredMethodInfo => typeIgnoredInfo.Value;

    static YantraJsExprGen()
    {
        fieldSetMethod = typeof(FieldInfo).GetMethod(nameof(FieldInfo.SetValue), [typeof(object), typeof(object)])!;
    }

    internal static object? GenClonerInternal(Type realType, bool asObject) => GenProcessMethod(realType, asObject && realType.IsValueType());

    private static bool MemberIsIgnored(MemberInfo memberInfo)
    {
        return YantraJsCache.GetOrAddMemberIgnoreStatus(memberInfo, mi =>
        {
            YantraJsIgnoreAttribute? fcIgnored = mi.GetCustomAttribute<YantraJsIgnoreAttribute>();
            NonSerializedAttribute? nonSerialized = mi.GetCustomAttribute<NonSerializedAttribute>();
            return fcIgnored?.Ignored ?? nonSerialized is not null;
        });
    }

    internal static bool CalcTypeContainsIgnoredMembers(Type type)
    {
        IEnumerable<MemberInfo> members = YantraJsCache.GetOrAddAllMembers(type, GetAllMembers);
        return members.Any(MemberIsIgnored);
    }

    internal static void ForceSetField(FieldInfo field, object obj, object value)
    {
        field.SetValue(obj, value);
    }

#if YANTRA_CORE
    internal readonly record struct ExpressionPosition(int Depth, int Index)
    {
        public ExpressionPosition Next() => this with { Index = Index + 1 };
        public ExpressionPosition Nested() => new ExpressionPosition(Depth + 1, 0);
    }
#else
    internal readonly struct ExpressionPosition : IEquatable<ExpressionPosition>
    {
        public int Depth { get; }
        public int Index { get; }

        public ExpressionPosition(int depth, int index)
        {
            Depth = depth;
            Index = index;
        }

        public ExpressionPosition Next() => new ExpressionPosition(Depth, Index + 1);
        public ExpressionPosition Nested() => new ExpressionPosition(Depth + 1, 0);

        public bool Equals(ExpressionPosition other)
        {
            return Depth == other.Depth && Index == other.Index;
        }

        public override bool Equals(object obj)
        {
            return obj is ExpressionPosition other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Depth * 397) ^ Index;
            }
        }

        public static bool operator ==(ExpressionPosition left, ExpressionPosition right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ExpressionPosition left, ExpressionPosition right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"ExpressionPosition {{ Depth = {Depth}, Index = {Index} }}";
        }
    }
#endif

    private static LabelTarget CreateLoopWithLabel(ExpressionPosition position)
    {
        string str = $"Loop_{position.Depth}_{position.Index}";
        return Expression.Label(str);
    }

    internal static object? GenProcessMethod(Type realType, bool asObject) => GenProcessMethod(realType, asObject && realType.IsValueType(), new ExpressionPosition(0, 0));
    public static bool IsSetType(Type type) => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISet<>));
    public static bool IsDictType(Type type) => typeof(IDictionary).IsAssignableFrom(type) || type.GetInterfaces().Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private readonly struct ConstructorInfoEx
    {
        public ConstructorInfo Constructor { get; }
        public int ParameterCount { get; }
        public bool HasOptionalParameters { get; }

        public ConstructorInfoEx(ConstructorInfo constructor)
        {
            Constructor = constructor;
            ParameterInfo[] parameters = constructor.GetParameters();
            ParameterCount = parameters.Length;
            HasOptionalParameters = ParameterCount > 0 && parameters.All(p => p.HasDefaultValue);
        }
    }

    private static ConstructorInfoEx? FindCallableCtor(Type type)
    {
        ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
        return ctor != null ? new ConstructorInfoEx(ctor) : null;
    }

    private static NewExpression CreateNewExpressionWithCtor(ConstructorInfoEx ctorInfoEx)
    {
        if (ctorInfoEx.ParameterCount == 0)
        {
            return Expression.New(ctorInfoEx.Constructor);
        }
        
        Expression[] arguments = ctorInfoEx.Constructor.GetParameters()
            .Select(p => Expression.Constant(p.HasDefaultValue ? p.DefaultValue : GetDefaultValue(p.ParameterType), p.ParameterType))
            .ToArray<Expression>();

        return Expression.New(ctorInfoEx.Constructor, arguments);
    }

    private static object? GetDefaultValue(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static void AddExpressions(
        List<Expression> expressionList,
        ParameterExpression fromLocal,
        ParameterExpression toLocal,
        ParameterExpression state,
        Type type)
    {
        ExpressionPosition currentPosition = new ExpressionPosition(0, 0);
        IEnumerable<MemberInfo> members = YantraJsCache.GetOrAddAllMembers(type, GetAllMembers);
        Dictionary<string, Type> ignoredEventDetails = YantraJsCache.GetOrAddIgnoredEventInfo(type, t =>
        {
            Dictionary<string, Type> details = new Dictionary<string, Type>();
            EventInfo[] events = t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (EventInfo evtInfo in events)
            {
                if (MemberIsIgnored(evtInfo))
                {
                    details[evtInfo.Name] = evtInfo.EventHandlerType;
                }
            }

            return details;
        });

        foreach (MemberInfo member in members)
        {
            Type memberType = member switch
            {
                FieldInfo fi => fi.FieldType,
                PropertyInfo pi => pi.PropertyType,
                _ => throw new ArgumentException($"Unsupported member type: {member.GetType()}")
            };
            
            bool canAssignDirect = member switch
            {
                FieldInfo fi => !fi.IsInitOnly,
                PropertyInfo pi => pi.CanWrite,
                _ => false
            };

            if (MemberIsIgnored(member))
            {
                if (canAssignDirect)
                {
                    expressionList.Add(Expression.Assign(
                        Expression.MakeMemberAccess(toLocal, member),
                        Expression.Default(memberType)
                    ));
                }

                continue;
            }

            if (member is FieldInfo fieldInfoForEventCheck && ignoredEventDetails.TryGetValue(fieldInfoForEventCheck.Name, out Type? evtType))
            {
                if (evtType == memberType)
                {
                    if (canAssignDirect)
                    {
                        expressionList.Add(Expression.Assign(
                            Expression.MakeMemberAccess(toLocal, member),
                            Expression.Default(memberType)
                        ));
                    }

                    continue;
                }
            }

            if (YantraJsCache.IsTypeIgnored(memberType))
            {
                if (canAssignDirect)
                {
                    expressionList.Add(Expression.Assign(
                        Expression.MakeMemberAccess(toLocal, member),
                        Expression.Default(memberType)
                    ));
                }

                continue;
            }

            if (member is PropertyInfo piLocal)
            {
                if (piLocal.CanWrite && MemberIsIgnored(piLocal))
                {
                    expressionList.Add(Expression.Assign(
                        Expression.Property(toLocal, piLocal),
                        Expression.Default(piLocal.PropertyType)
                    ));
                }

                continue;
            }

            if (!YantraJsSafeTypes.CanReturnSameObject(memberType))
            {
                bool shouldBeIgnored = false;

                if (MemberIsIgnored(member))
                {
                    shouldBeIgnored = true;
                }
                else if (member is FieldInfo fi)
                {
                    if (ignoredEventDetails.TryGetValue(fi.Name, out Type? eventHandlerTypeFromCache))
                    {
                        if (eventHandlerTypeFromCache == fi.FieldType)
                        {
                            shouldBeIgnored = true;
                        }
                    }
                }

                if (shouldBeIgnored)
                {
                    if (canAssignDirect)
                    {
                        expressionList.Add(Expression.Assign(
                            Expression.MakeMemberAccess(toLocal, member),
                            Expression.Default(memberType)
                        ));
                    }

                    continue;
                }

                MethodInfo cloneMethodInfo = memberType.IsValueType()
                    ? typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneStructInternal))!.MakeGenericMethod(memberType)
                    : typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneClassInternal))!;
                MemberExpression getMemberValue = Expression.MakeMemberAccess(fromLocal, member);
                Expression originalMemberValue = getMemberValue;

                Expression callClone = Expression.Call(cloneMethodInfo, originalMemberValue, state);
                
                if (!memberType.IsValueType())
                {
                    callClone = Expression.Convert(callClone, memberType);
                }

                Expression clonedValueExpression = callClone;
                
                switch (member)
                {
                    case FieldInfo fieldInfo:
                    {
                        bool isReadonly = readonlyFields.GetOrAdd(fieldInfo, f => f.IsInitOnly);
                        if (isReadonly)
                        {
                            expressionList.Add(Expression.Call(
                                Expression.Constant(fieldInfo),
                                fieldSetMethod,
                                Expression.Convert(toLocal, typeof(object)),
                                Expression.Convert(clonedValueExpression, typeof(object))));
                        }
                        else
                        {
                            expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), clonedValueExpression));
                        }

                        break;
                    }
                    case PropertyInfo { CanWrite: true }:
                    {
                        expressionList.Add(Expression.Assign(Expression.MakeMemberAccess(toLocal, member), clonedValueExpression));
                        break;
                    }
                }

                currentPosition = currentPosition.Next();
            }
        }
    }

    private static object GenMemberwiseCloner(Type type, ExpressionPosition position)
    {
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        ParameterExpression toLocal = Expression.Variable(type);
        ParameterExpression fromLocal = Expression.Variable(type);
        List<Expression> expressionList = [];

        if (!type.IsValueType())
        {
            MethodInfo methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone))!;
            expressionList.Add(Expression.Assign(toLocal, Expression.Convert(Expression.Call(from, methodInfo), type)));
            expressionList.Add(Expression.Assign(fromLocal, Expression.Convert(from, type)));
            expressionList.Add(Expression.Call(state, YantraStatic.DeepCloneStateMethods.AddKnownRef, from, toLocal));
        }
        else
        {
            expressionList.Add(Expression.Assign(toLocal, Expression.Unbox(from, type)));
            expressionList.Add(Expression.Assign(fromLocal, toLocal));
        }

        AddExpressions(expressionList, fromLocal, toLocal, state, type);
        expressionList.Add(Expression.Convert(toLocal, typeof(object)));
        List<ParameterExpression> blockParams = [fromLocal, toLocal];
        return Expression.Lambda<Func<object, YantraJsState, object>>(
            Expression.Block(blockParams, expressionList),
            from,
            state
        ).Compile();
    }

    private delegate object ProcessMethodDelegate(Type type, bool unboxStruct, ExpressionPosition position);

#if YANTRA_CORE
    private static readonly FrozenDictionary<Type, ProcessMethodDelegate> knownTypeProcessors =
        new Dictionary<Type, ProcessMethodDelegate>
        {
            [typeof(ExpandoObject)] = (_, _, position) => GenExpandoObjectProcessor(position),
            [typeof(HttpRequestOptions)] = (_, _, position) => GenHttpRequestOptionsProcessor(position),
            [typeof(Array)] = (type, _, _) => GenProcessArrayMethod(type),
            [typeof(System.Text.Json.Nodes.JsonNode)] = (_, _, position) => GenJsonNodeProcessorModern(position),
            [typeof(System.Text.Json.Nodes.JsonObject)] = (_, _, position) => GenJsonNodeProcessorModern(position),
            [typeof(System.Text.Json.Nodes.JsonArray)] = (_, _, position) => GenJsonNodeProcessorModern(position),
            [typeof(System.Text.Json.Nodes.JsonValue)] = (_, _, position) => GenJsonNodeProcessorModern(position),
        }.ToFrozenDictionary();
#else
    private static readonly Dictionary<Type, ProcessMethodDelegate> knownTypeProcessors =
        new Dictionary<Type, ProcessMethodDelegate>
        {
            [typeof(ExpandoObject)] = (_, _, position) => GenExpandoObjectProcessor(position),
            [typeof(Array)] = (type, _, _) => GenProcessArrayMethod(type),
        };
#endif

    private static readonly YantraAc AdversaryTypes = new YantraAc([
        "Castle.Proxies.",
        "System.Data.Entity.DynamicProxies.",
        "NHibernate.Proxy."
    ]);
    
    private static readonly Dictionary<string, Func<Type, object?>> knownNamespaces = new Dictionary<string, Func<Type, object?>> 
    {
        { "System.Drawing", CloneIClonable },
        { "System.Globalization", CloneIClonable }
    };
    
    private static bool IsCloneable(Type type)
    {
        if (type.FullName is null)
        {
            return false;
        }
        
        return !AdversaryTypes.ContainsAnyPattern(type.FullName);
    }
    
    private static List<MemberInfo> GetAllMembers(Type type)
    {
        List<MemberInfo> members = [];
        Type? currentType = type;
        
        while (currentType != null && currentType != typeof(ContextBoundObject))
        {
            members.AddRange(currentType.GetDeclaredFields());
            members.AddRange(currentType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)); // Exclude indexers
            currentType = currentType.BaseType;
        }

        return members;
    }

    private static object? CloneIClonable(Type type)
    {
        if (typeof(ICloneable).IsAssignableFrom(type))
        {
            return (Func<object, YantraJsState, object>)((obj, state) =>
            {
                object result = ((ICloneable)obj).Clone();
                state.AddKnownRef(obj, result);
                return result;
            });
        }

        return null;
    }
    
    private static object? GenProcessMethod(Type type, bool unboxStruct, ExpressionPosition position)
    {
        if (!IsCloneable(type))
        {
            return null;
        }
        
        Type methodType = unboxStruct || type.IsClass() ? typeof(object) : type;
        
        if (YantraJsCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(methodType, "fromParam");
            ParameterExpression pState = Expression.Parameter(typeof(YantraJsState), "stateParam");
            Type funcGenericType = typeof(Func<,,>).MakeGenericType(methodType, typeof(YantraJsState), methodType);
            Expression resultExpression;
            
            if (type.IsValueType && !unboxStruct)
            {
                resultExpression = Expression.Default(type);
            }
            else
            {
                resultExpression = Expression.Constant(null, methodType);
            }

            return Expression.Lambda(funcGenericType, resultExpression, pFrom, pState).Compile();
        }
        
        if (knownTypeProcessors.TryGetValue(type, out ProcessMethodDelegate? handler))
        {
            return handler.Invoke(type, unboxStruct, position);
        }

        if (typeHandlers.TryGetValue(type, out Func<Type, bool, ExpressionPosition, object>? contribHandler))
        {
            return contribHandler.Invoke(type, unboxStruct, position);
        }

        if (type.Namespace is not null && knownNamespaces.TryGetValue(type.Namespace, out Func<Type, object?>? cloneMethod))
        {
            object? special = cloneMethod.Invoke(type);

            if (special is not null)
            {
                return special;
            }
        }
        
        if (IsDictType(type))
        {
            return GenProcessDictionaryMethod(type, position);
        }

        if (IsSetType(type))
        {
            return GenProcessSetMethod(type, position);
        }

        if (type.IsArray)
        {
            return GenProcessArrayMethod(type);
        }

        if (type.FullName is not null && type.FullName.StartsWith("System.Tuple`"))
        {
            Type[] genericArguments = type.GenericArguments();

            if (genericArguments.Length < 10 && genericArguments.All(YantraJsSafeTypes.CanReturnSameObject))
            {
                return GenTuple(type);
            }
        }

#if !YANTRA_CORE
        if (type.FullName != null && type.FullName.StartsWith("System.Text.Json.Nodes."))
        {
             return GenJsonNodeProcessorNetstandard(type, position);
        }
#endif
        
        List<Expression> expressionList = [];
        ParameterExpression from = Expression.Parameter(methodType);
        ParameterExpression fromLocal = from;
        ParameterExpression toLocal = Expression.Variable(type);
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));

        if (!type.IsValueType())
        {
            MethodInfo methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone))!;
            
            expressionList.Add(Expression.Assign(toLocal, Expression.Convert(Expression.Call(from, methodInfo), type)));

            fromLocal = Expression.Variable(type);
            expressionList.Add(Expression.Assign(fromLocal, Expression.Convert(from, type)));
            expressionList.Add(Expression.Call(state, YantraStatic.DeepCloneStateMethods.AddKnownRef, from, toLocal));
        }
        else
        {
            if (unboxStruct)
            {
                expressionList.Add(Expression.Assign(toLocal, Expression.Unbox(from, type)));
                fromLocal = Expression.Variable(type);
                expressionList.Add(Expression.Assign(fromLocal, toLocal));
            }
            else
            {
                expressionList.Add(Expression.Assign(toLocal, from));
            }
        }

        AddExpressions(expressionList, fromLocal, toLocal, state, type);
        expressionList.Add(Expression.Convert(toLocal, methodType));

        Type funcType = typeof(Func<,,>).MakeGenericType(methodType, typeof(YantraJsState), methodType);
        List<ParameterExpression> blockParams = [];
        
        if (from != fromLocal)
        {
            blockParams.Add(fromLocal);
        }
        
        blockParams.Add(toLocal);

        return Expression.Lambda(funcType, Expression.Block(blockParams, expressionList), from, state).Compile();
    }
    
    #if YANTRA_CORE
    private static object GenHttpRequestOptionsProcessor(ExpressionPosition position)
    {
        if (YantraJsCache.IsTypeIgnored(typeof(HttpRequestOptions)))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(YantraJsState));
            return Expression.Lambda<Func<object, YantraJsState, object>>(pFrom, pFrom, pState).Compile();
        }
        
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        ParameterExpression result = Expression.Variable(typeof(HttpRequestOptions));
        ParameterExpression tempMessage = Expression.Variable(typeof(HttpRequestMessage));
        ParameterExpression fromOptions = Expression.Variable(typeof(HttpRequestOptions));
        
        ConstructorInfo constructor = typeof(HttpRequestMessage).GetConstructor(Type.EmptyTypes)!;

        BlockExpression block = Expression.Block(
            [result, tempMessage, fromOptions],
            Expression.Assign(fromOptions, Expression.Convert(from, typeof(HttpRequestOptions))),
            Expression.Assign(tempMessage, Expression.New(constructor)),
            Expression.Assign(result, Expression.Property(tempMessage, "Options")),
            Expression.Call(state, YantraStatic.DeepCloneStateMethods.AddKnownRef, from, result),
            Expression.Assign(result, Expression.Convert(
                Expression.Call(fromOptions, typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!),
                typeof(HttpRequestOptions)
            )),
            Expression.Call(tempMessage, typeof(IDisposable).GetMethod("Dispose")!),
            result
        );

        return Expression.Lambda<Func<object, YantraJsState, object>>(block, from, state).Compile();
    }
    #endif
    
    private static object GenExpandoObjectProcessor(ExpressionPosition position)
    {
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        ParameterExpression result = Expression.Variable(typeof(ExpandoObject));
        
        BinaryExpression createNew = Expression.Assign(result, Expression.New(typeof(ExpandoObject)));
        
        ParameterExpression fromDict = Expression.Variable(typeof(IDictionary<string, object>));
        ParameterExpression resultDict = Expression.Variable(typeof(IDictionary<string, object>));
        
        BlockExpression block = Expression.Block(
            [result, fromDict, resultDict],
            createNew,
            Expression.Assign(fromDict, Expression.Convert(from, typeof(IDictionary<string, object>))),
            Expression.Assign(resultDict, Expression.Convert(result, typeof(IDictionary<string, object>))),
            Expression.Call(state, YantraStatic.DeepCloneStateMethods.AddKnownRef, from, result),
            GenExpandoObjectCopyLoop(fromDict, resultDict, state, position),
            Expression.Convert(result, typeof(object))
        );

        return Expression.Lambda<Func<object, YantraJsState, object>>(block, from, state).Compile();
    }

    private static BlockExpression GenExpandoObjectCopyLoop(ParameterExpression fromDict, ParameterExpression resultDict, ParameterExpression state, ExpressionPosition position)
    {
        ParameterExpression enumerator = Expression.Variable(typeof(IEnumerator<KeyValuePair<string, object>>));
        ParameterExpression kvp = Expression.Variable(typeof(KeyValuePair<string, object>));
        ParameterExpression valueToClone = Expression.Variable(typeof(object));
        LabelTarget breakLabel = CreateLoopWithLabel(position);
        
        Expression clonedLogic = Expression.Call(
            typeof(YantraJsGenerator).GetMethod("CloneClassInternal", BindingFlags.NonPublic | BindingFlags.Static)!,
            valueToClone,
            state
        );
        
        Expression typeOfValue = Expression.Call(valueToClone, typeof(object).GetMethod(nameof(GetType))!);
        Expression isValueIgnored = Expression.Call(null, IsTypeIgnoredMethodInfo, typeOfValue);
        Expression isValueNull = Expression.Equal(valueToClone, Expression.Constant(null, typeof(object)));

        Expression valueIfNotDelegate =
            Expression.Condition(
            isValueNull,
            Expression.Constant(null, typeof(object)),
            Expression.Condition(
                isValueIgnored,
                Expression.Constant(null, typeof(object)),
                clonedLogic
            )
        );

        Expression finalValueExpression = Expression.Condition(
            Expression.TypeIs(valueToClone, typeof(Delegate)),
            valueToClone,
            valueIfNotDelegate
        );
        
        return Expression.Block(
            [enumerator, kvp, valueToClone],
            Expression.Assign(
                enumerator,
                Expression.Call(
                    fromDict,
                    typeof(IEnumerable<KeyValuePair<string, object>>).GetMethod("GetEnumerator")!
                )
            ),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.Call(enumerator, typeof(IEnumerator).GetMethod("MoveNext")!),
                    Expression.Block(
                        Expression.Assign(kvp, Expression.Property(enumerator, "Current")),
                        Expression.Assign(valueToClone, Expression.Property(kvp, "Value")),
                        Expression.Call(
                            resultDict,
                            typeof(IDictionary<string, object>).GetMethod("Add")!,
                            Expression.Property(kvp, "Key"),
                            finalValueExpression 
                        )
                    ),
                    Expression.Break(breakLabel)
                ),
                breakLabel
            )
        );
    }

    private static object GenProcessDictionaryMethod(Type type, ExpressionPosition position)
    {
        Type[] genericArguments = type.GenericArguments();
        
        return genericArguments.Length switch
        {
            0 => GenNonGenericDictionaryProcessor(type, position),
            1 => HandleOneGenericArgument(type, genericArguments[0], position),
            2 => GenDictionaryProcessor(type, genericArguments[0], genericArguments[1], true, position),
            _ => throw new ArgumentException($"Unexpected number of generic arguments: {genericArguments.Length}")
        };
    }

    private static object GenNonGenericDictionaryProcessor(Type type, ExpressionPosition position)
    {
        Type? dictionaryInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && 
                                 (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || 
                                  i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
            
        if (dictionaryInterface is not null)
        {
            Type[] interfaceArgs = dictionaryInterface.GetGenericArguments();
            return GenDictionaryProcessor(type, interfaceArgs[0], interfaceArgs[1], true, position);
        }
            
        return GenDictionaryProcessor(type, typeof(object), typeof(object), false, position);
    }

    private static object HandleOneGenericArgument(Type type, Type genericArg, ExpressionPosition position)
    {
        if (genericArg.IsGenericType && genericArg.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            Type[] kvpArguments = genericArg.GetGenericArguments();
            return GenDictionaryProcessor(type, kvpArguments[0], kvpArguments[1], true, position);
        }

        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            return GenDictionaryProcessor(type, typeof(object), genericArg, true, position);
        }

        throw new ArgumentException($"Unsupported dictionary type with single generic argument: {type.FullName}");
    }

    private static object GenDictionaryProcessor(Type dictType, Type keyType, Type valueType, bool isGeneric, ExpressionPosition position)
    {
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        LabelTarget returnNullLabel = Expression.Label(typeof(object));
        ConditionalExpression nullCheck = Expression.IfThen(
            Expression.Equal(from, Expression.Constant(null)),
            Expression.Return(returnNullLabel, Expression.Constant(null))
        );
        
        bool isImmutable = IsImmutable(dictType);
        
        if (isImmutable)
        {
            return GenImmutableDictionaryProcessor(dictType, keyType, valueType, from, state, returnNullLabel, nullCheck);
        }
        
        #if YANTRA_CORE
        bool isReadOnly = dictType.Name.Contains("ReadOnly", StringComparison.InvariantCultureIgnoreCase) || 
                          (dictType.IsGenericType && dictType.GetGenericTypeDefinition() == typeof(ReadOnlyDictionary<,>));
        #else 
        bool isReadOnly = dictType.Name.IndexOf("ReadOnly", StringComparison.InvariantCultureIgnoreCase) >= 0 || 
                          (dictType.IsGenericType && dictType.GetGenericTypeDefinition() == typeof(ReadOnlyDictionary<,>));
        #endif

        Type innerDictType = isReadOnly
            ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType)
            : dictType;

        ConstructorInfoEx? ctorInfo = isReadOnly 
            ? dictType.GetConstructor([innerDictType]) is { } ctor ? new ConstructorInfoEx(ctor) : null
            : FindCallableCtor(dictType);
        
        if (ctorInfo is null)
        {
            return GenMemberwiseCloner(dictType, position);
        }

        ParameterExpression result = Expression.Variable(dictType);
        ParameterExpression innerDict = isReadOnly 
            ? Expression.Variable(innerDictType)
            : result;
        
        ConstructorInfoEx? innerDictCtorInfo = FindCallableCtor(innerDictType);
        
        if (innerDictCtorInfo is null)
        {
            return GenMemberwiseCloner(dictType, position);
        }

        ConstructorInfoEx ci = innerDictCtorInfo.GetValueOrDefault();
        
        BinaryExpression createInnerDict = Expression.Assign(
            innerDict,
            CreateNewExpressionWithCtor(ci)
        );
        
        Expression createResult = isReadOnly
            ? Expression.Assign(
                result,
                Expression.New(
                    dictType.GetConstructor([innerDictType])!,
                    innerDict
                )
            )
            : Expression.Assign(
                result,
                CreateNewExpressionWithCtor(ci)
            );
        
        Expression addRef = Expression.Call(
            state,
            YantraStatic.DeepCloneStateMethods.AddKnownRef,
            from,
            result
        );
        
        Type methodSourceType = isReadOnly ? innerDictType : dictType;
        MethodInfo? addMethod = methodSourceType.GetMethod("Add", [keyType, valueType])
                                ?? methodSourceType.GetMethods()
                                    .FirstOrDefault(m => m.Name == "TryAdd" &&
                                                         m.GetParameters().Length is 2 &&
                                                         m.GetParameters()[0].ParameterType == keyType &&
                                                         m.GetParameters()[1].ParameterType == valueType);

        if (addMethod is null)
        {
            return GenMemberwiseCloner(dictType, position);
        }
        
        Type enumeratorType = isGeneric
            ? typeof(IEnumerator<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType))
            : typeof(IDictionaryEnumerator);

        ParameterExpression enumerator = Expression.Variable(enumeratorType);
        
        MethodInfo keyCloneMethod = keyType.IsValueType()
            ? typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneStructInternal))!.MakeGenericMethod(keyType)
            : typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneClassInternal))!;

        MethodInfo valueCloneMethod = valueType.IsValueType()
            ? typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneStructInternal))!.MakeGenericMethod(valueType)
            : typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneClassInternal))!;
        
        BlockExpression iterationBlock = isGeneric
            ? GenGenericDictionaryIteration(enumerator, keyType, valueType, keyCloneMethod, valueCloneMethod, innerDict, addMethod, state, position)
            : GenNonGenericDictionaryIteration(enumerator, keyCloneMethod, valueCloneMethod, innerDict, addMethod, state, position);
        
        Type enumerableType = isGeneric
            ? typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType))
            : typeof(IDictionary);

        MethodInfo? getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");
        
        if (getEnumeratorMethod is null)
        {
            throw new InvalidOperationException($"Cannot find GetEnumerator method for type {enumerableType.FullName}");
        }
        
        Expression getEnumerator = Expression.Assign(
            enumerator,
            Expression.Convert(
                Expression.Call(
                    Expression.Convert(from, enumerableType),
                    getEnumeratorMethod
                ),
                enumeratorType
            )
        );
        
        BlockExpression block = Expression.Block(
            isReadOnly ? [result, innerDict, enumerator] : new[] { result, enumerator },
            nullCheck,
            createInnerDict,
            createResult,
            addRef,
            getEnumerator,
            iterationBlock,
            Expression.Label(returnNullLabel, Expression.Convert(result, typeof(object)))
        );
        
        return Expression.Lambda<Func<object, YantraJsState, object>>(
            block,
            from,
            state
        ).Compile();
    }
    
    private static object GenImmutableDictionaryProcessor(Type dictType, Type keyType, Type valueType, ParameterExpression from, ParameterExpression state, LabelTarget returnNullLabel, Expression nullCheck)
    {
        ParameterExpression typedFrom = Expression.Variable(dictType);
        ParameterExpression result = Expression.Variable(dictType);
        BinaryExpression castFrom = Expression.Assign(
            typedFrom,
            Expression.Convert(from, dictType)
        );
        
        MethodInfo? addMethod = dictType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Add" && m.ReturnType == dictType && 
                               m.GetParameters().Length == 2 &&
                               m.GetParameters()[0].ParameterType == keyType &&
                               m.GetParameters()[1].ParameterType == valueType);
        
        if (addMethod == null)
        {
            return Expression.Lambda<Func<object, YantraJsState, object>>(
                from,
                from, 
                state
            ).Compile();
        }
        
        MethodInfo? emptyMethod = dictType.GetMethod("Empty", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        MethodInfo? createMethod = dictType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        Expression createEmpty;
        
        if (emptyMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, emptyMethod)
            );
        }
        else if (createMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, createMethod)
            );
        }
        else
        {
            MethodInfo? clearMethod = dictType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
            
            if (clearMethod != null && clearMethod.ReturnType == dictType)
            {
                createEmpty = Expression.Assign(
                    result,
                    Expression.Call(typedFrom, clearMethod)
                );
            }
            else
            {
                return Expression.Lambda<Func<object, YantraJsState, object>>(
                    from,
                    from, 
                    state
                ).Compile();
            }
        }
 
        MethodInfo keyCloneMethod = keyType.IsValueType()
            ? typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneStructInternal))!.MakeGenericMethod(keyType)
            : typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneClassInternal))!;

        MethodInfo valueCloneMethod = valueType.IsValueType()
            ? typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneStructInternal))!.MakeGenericMethod(valueType)
            : typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneClassInternal))!;

        Type enumeratorType = typeof(IEnumerator<>).MakeGenericType(
            typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType));
        ParameterExpression enumerator = Expression.Variable(enumeratorType);
   
        Type enumerableType = typeof(IEnumerable<>).MakeGenericType(
            typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType));
        MethodInfo? getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");

        BinaryExpression assignEnumerator = Expression.Assign(
            enumerator,
            Expression.Call(
                Expression.Convert(typedFrom, enumerableType),
                getEnumeratorMethod
            )
        );

        PropertyInfo? current = enumeratorType.GetProperty("Current");
        Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
        ParameterExpression kvp = Expression.Variable(kvpType);
        ParameterExpression key = Expression.Variable(keyType);
        ParameterExpression value = Expression.Variable(valueType);
        
        LabelTarget breakLabel = Expression.Label("LoopBreak");
        
        Expression originalKeyProperty = Expression.Property(kvp, "Key");
        Expression clonedKeyCall = Expression.Call(keyCloneMethod, originalKeyProperty, state);
        
        if (!keyType.IsValueType())
        {
            clonedKeyCall = Expression.Convert(clonedKeyCall, keyType);
        }

        Expression keyForAdd = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(keyType, typeof(Type))),
            originalKeyProperty,
            clonedKeyCall
        );


        Expression originalValueProperty = Expression.Property(kvp, "Value");
        Expression clonedValueCall = Expression.Call(valueCloneMethod, originalValueProperty, state);
        
        if (!valueType.IsValueType())
        {
            clonedValueCall = Expression.Convert(clonedValueCall, valueType);
        }

        Expression valueForAdd = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(valueType, typeof(Type))),
            originalValueProperty,
            clonedValueCall
        );
        
        BlockExpression loopBody = Expression.Block(
            [kvp, key, value],
            Expression.Assign(kvp, Expression.Property(enumerator, current)),
            Expression.Assign(
                key,
                keyForAdd
            ),
            Expression.Assign(
                value,
                valueForAdd
            ),
            Expression.Assign(
                result,
                Expression.Call(result, addMethod, key, value)
            )
        );
        
        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, typeof(IEnumerator).GetMethod("MoveNext")),
                loopBody,
                Expression.Break(breakLabel)
            ),
            breakLabel
        );
        
        MethodCallExpression addRef = Expression.Call(
            state,
            YantraStatic.DeepCloneStateMethods.AddKnownRef,
            from,
            result
        );
        
        BlockExpression block = Expression.Block(
            [typedFrom, result, enumerator],
            nullCheck,
            castFrom,
            createEmpty,
            assignEnumerator,
            loop,
            addRef,
            Expression.Label(returnNullLabel, Expression.Convert(result, typeof(object)))
        );
        
        return Expression.Lambda<Func<object, YantraJsState, object>>(
            block,
            from,
            state
        ).Compile();
    }
    
    private static bool IsImmutable(Type type)
    {
        if (type.Namespace == "System.Collections.Immutable")
        {
            return true;
        }
        
        if (type.GetInterfaces().Any(x => x.Namespace == "System.Collections.Immutable"))
        {
            return true;
        }
        
        Attribute? immutableAttr = type.GetCustomAttributes().FirstOrDefault(attr => attr.GetType().Name.Contains("Immutable"));
        return immutableAttr is not null || type.Name.Contains("Immutable");
    }


    private static BlockExpression GenGenericDictionaryIteration(ParameterExpression enumerator, Type keyType, Type valueType, MethodInfo keyCloneMethod, MethodInfo valueCloneMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state, ExpressionPosition position)
    {
        PropertyInfo current = enumerator.Type.GetProperty(nameof(IEnumerator<object>.Current))!;
        LabelTarget breakLabel = CreateLoopWithLabel(position);
        Type dictionaryType = local.Type;
        bool isSingleGenericParameter = dictionaryType.GetGenericArguments().Length is 1;

        if (isSingleGenericParameter)
        {
            Type singleGenericType = dictionaryType.GetGenericArguments()[0];
            
            if (singleGenericType.IsGenericType && singleGenericType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                Type[] kvpTypes = singleGenericType.GetGenericArguments();
                ParameterExpression kvp = Expression.Variable(singleGenericType);
                BinaryExpression assignKvp = Expression.Assign(kvp, Expression.Property(enumerator, current));
                
                Expression originalSingleKey = Expression.Property(kvp, "Key"); 
                Expression clonedSingleKeyCall = Expression.Call(keyCloneMethod, originalSingleKey, state);
                
                if (!kvpTypes[0].IsValueType())
                {
                    clonedSingleKeyCall = Expression.Convert(clonedSingleKeyCall, kvpTypes[0]);
                }
                
                Expression keyForNewKvp = Expression.Condition(
                    Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(kvpTypes[0], typeof(Type))),
                    originalSingleKey,
                    clonedSingleKeyCall
                );
                
                Expression originalSingleValue = Expression.Property(kvp, "Value"); 
                Expression clonedSingleValueCall = Expression.Call(valueCloneMethod, originalSingleValue, state);
                if (!kvpTypes[1].IsValueType()) clonedSingleValueCall = Expression.Convert(clonedSingleValueCall, kvpTypes[1]);
                
                Expression valueForNewKvp = Expression.Condition(
                    Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(kvpTypes[1], typeof(Type))),
                    originalSingleValue,
                    clonedSingleValueCall
                );
                
                NewExpression newKvp = Expression.New(
                    singleGenericType.GetConstructor([kvpTypes[0], kvpTypes[1]])!,
                    Expression.Convert(keyForNewKvp, kvpTypes[0]), 
                    Expression.Convert(valueForNewKvp, kvpTypes[1])  
                );
                MethodInfo collectionAddMethod = dictionaryType.GetMethod("Add", [singleGenericType])!;
                MethodCallExpression addKvp = Expression.Call(local, collectionAddMethod, newKvp);

                LoopExpression loop = Expression.Loop(
                    Expression.IfThenElse(
                        Expression.Call(enumerator, typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!),
                        Expression.Block([kvp], assignKvp, addKvp),
                        Expression.Break(breakLabel)),
                    breakLabel);

                return Expression.Block(loop);
            }
        }
        
        {
            Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
            ParameterExpression kvp = Expression.Variable(kvpType);
            ParameterExpression key = Expression.Variable(keyType);
            ParameterExpression value = Expression.Variable(valueType);

            BinaryExpression assignKvp = Expression.Assign(kvp, Expression.Property(enumerator, current));
            
            Expression originalKeyProperty = Expression.Property(kvp, "Key");
            Expression clonedKeyCall = Expression.Call(keyCloneMethod, originalKeyProperty, state);
            
            if (!keyType.IsValueType())
            {
                clonedKeyCall = Expression.Convert(clonedKeyCall, keyType);
            }

            Expression keyToAssign = Expression.Condition(
                Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(keyType, typeof(Type))),
                originalKeyProperty,
                clonedKeyCall
            );
            
            Expression originalValueProperty = Expression.Property(kvp, "Value");
            Expression clonedValueCall = Expression.Call(valueCloneMethod, originalValueProperty, state);
            
            if (!valueType.IsValueType())
            {
                clonedValueCall = Expression.Convert(clonedValueCall, valueType);
            }
            
            Expression valueToAssign = Expression.Condition(
                Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(valueType, typeof(Type))),
                valueType.IsValueType
                    ? Expression.Default(valueType)
                    : Expression.Constant(null, valueType),
                clonedValueCall
            );
            
            BinaryExpression assignKey = Expression.Assign(key, keyToAssign);
            BinaryExpression assignValue = Expression.Assign(value, valueToAssign);
            MethodCallExpression addKvp = Expression.Call(local, addMethod, key, value);

            LoopExpression loop = Expression.Loop(
                Expression.IfThenElse(
                    Expression.Call(enumerator, typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!),
                    Expression.Block([kvp, key, value],
                        assignKvp,
                        assignKey,
                        assignValue,
                        addKvp),
                    Expression.Break(breakLabel)),
                breakLabel);

            return Expression.Block(loop);
        }
    }

    private static BlockExpression GenNonGenericDictionaryIteration(ParameterExpression enumerator, MethodInfo keyCloneMethod, MethodInfo valueCloneMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state, ExpressionPosition position)
    {
        MemberExpression current = Expression.Property(enumerator, nameof(IDictionaryEnumerator.Entry));
        ParameterExpression key = Expression.Variable(typeof(object));
        ParameterExpression value = Expression.Variable(typeof(object));

        Expression originalKeyObject = Expression.Property(current, "Key"); 
        Expression clonedKeyCall = Expression.Call(keyCloneMethod, originalKeyObject, state); 
        
        Expression keyRuntimeType = Expression.Call(originalKeyObject, typeof(object).GetMethod(nameof(GetType))!);
        Expression isKeyIgnored = Expression.Call(null, IsTypeIgnoredMethodInfo, keyRuntimeType);
        
        Expression keyToAssign = Expression.Condition(
            Expression.OrElse(
                Expression.Equal(originalKeyObject, Expression.Constant(null, typeof(object))),
                isKeyIgnored
            ),
            originalKeyObject,
            clonedKeyCall
        );
        
        Expression originalValueObject = Expression.Property(current, "Value"); 
        Expression clonedValueCall = Expression.Call(valueCloneMethod, originalValueObject, state); 
        
        Expression valueRuntimeType = Expression.Call(originalValueObject, typeof(object).GetMethod(nameof(GetType))!);
        Expression isValueIgnored = Expression.Call(null, IsTypeIgnoredMethodInfo, valueRuntimeType);
        
        Expression valueToAssign = Expression.Condition(
            Expression.OrElse(
                Expression.Equal(originalValueObject, Expression.Constant(null, typeof(object))),
                isValueIgnored
            ),
            originalValueObject,
            clonedValueCall
        );
        
        BinaryExpression assignKey = Expression.Assign(key, keyToAssign);
        BinaryExpression assignValue = Expression.Assign(value, valueToAssign);
        MethodCallExpression addEntry = Expression.Call(local, addMethod, key, value);
        
        LabelTarget breakLabel = CreateLoopWithLabel(position);

        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!),
                Expression.Block(
                    [key, value],
                    assignKey,
                    assignValue,
                    addEntry),
                Expression.Break(breakLabel)),
            breakLabel);

        return Expression.Block(loop);
    }

    private static object GenProcessSetMethod(Type type, ExpressionPosition position)
    {
        if (YantraJsCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(YantraJsState));
            return Expression.Lambda<Func<object, YantraJsState, object>>(pFrom, pFrom, pState).Compile();
        }
        
        Type elementType = type.GenericArguments()[0];

        MethodInfo cloneElementMethod = elementType.IsValueType()
            ? typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneStructInternal))!.MakeGenericMethod(elementType)
            : typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneClassInternal))!;

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        
        bool isImmutable = IsImmutable(type);
    
        if (isImmutable)
        {
            return GenImmutableSetProcessor(type, elementType, from, state, position);
        }
        
        ParameterExpression local = Expression.Variable(type);
        
        #if YANTRA_CORE
        bool isReadOnly = type.Name.Contains("ReadOnly", StringComparison.InvariantCultureIgnoreCase);
        #else
        bool isReadOnly = type.Name.IndexOf("ReadOnly", StringComparison.InvariantCultureIgnoreCase) >= 0;
        #endif
        
        Type innerSetType = isReadOnly 
            ? typeof(HashSet<>).MakeGenericType(elementType)
            : type;

        ParameterExpression innerSet = isReadOnly 
            ? Expression.Variable(innerSetType)
            : local;
        
        BinaryExpression assignInnerSet = Expression.Assign(
            innerSet, 
            Expression.New(innerSetType.GetConstructor(Type.EmptyTypes)!)
        );
        
        MethodInfo? addMethod = innerSetType.GetMethod("Add", [elementType]) ?? 
                               typeof(ISet<>).MakeGenericType(elementType).GetMethod("Add") ?? 
                               innerSetType.GetMethod("Add") ?? 
                               innerSetType.GetMethod("TryAdd");

        if (addMethod == null)
        {
            return GenMemberwiseCloner(type, position);
        }
        
        BlockExpression foreachBlock = GenForeach(
            from, 
            elementType, 
            null, 
            cloneElementMethod, 
            null, 
            innerSet, 
            addMethod, 
            state, 
            position
        );
        
        Expression createMutableColl = assignInnerSet; 
        Expression createFinalCollShell = isReadOnly ?
            Expression.Assign(local, Expression.New(type.GetConstructor([innerSetType])!, innerSet)) :
            Expression.Empty();
        
        Type funcType = typeof(Func<object, YantraJsState, object>);

        return Expression.Lambda(
            funcType, 
            Expression.Block(
                isReadOnly ? [local, innerSet] : new[] { local },
                createMutableColl,
                createFinalCollShell, 
                Expression.Call(state, YantraStatic.DeepCloneStateMethods.AddKnownRef, from, local), 
                foreachBlock, 
                local
            ), 
            from, 
            state
        ).Compile();
    }
    
    private static object GenImmutableSetProcessor(Type setType, Type elementType, ParameterExpression from, ParameterExpression state, ExpressionPosition position)
    {
        ParameterExpression typedFrom = Expression.Variable(setType);
        ParameterExpression result = Expression.Variable(setType);
        LabelTarget returnNullLabel = Expression.Label(typeof(object));
        
        Expression nullCheck = Expression.IfThen(
            Expression.Equal(from, Expression.Constant(null)),
            Expression.Return(returnNullLabel, Expression.Constant(null))
        );
        
        BinaryExpression castFrom = Expression.Assign(
            typedFrom,
            Expression.Convert(from, setType)
        );
        
        MethodInfo? addMethod = setType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Add" && m.ReturnType == setType && 
                               m.GetParameters().Length == 1 &&
                               m.GetParameters()[0].ParameterType == elementType);
        
        if (addMethod == null)
        {
            addMethod = setType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Union" && m.ReturnType == setType);
                
            if (addMethod == null)
            {
                return GenMemberwiseCloner(setType, position);
            }
        }

        MethodInfo? emptyMethod = setType.GetMethod("Empty", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        MethodInfo? createMethod = setType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        Expression createEmpty;
        
        if (emptyMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, emptyMethod)
            );
        }
        else if (createMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, createMethod)
            );
        }
        else
        {
            MethodInfo? clearMethod = setType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
            
            if (clearMethod != null && clearMethod.ReturnType == setType)
            {
                createEmpty = Expression.Assign(
                    result,
                    Expression.Call(typedFrom, clearMethod)
                );
            }
            else
            {
                return Expression.Lambda<Func<object, YantraJsState, object>>(
                    from,
                    from, 
                    state
                ).Compile();
            }
        }
        
        Type enumeratorType = typeof(IEnumerator<>).MakeGenericType(elementType);
        ParameterExpression enumerator = Expression.Variable(enumeratorType);
        
        Type enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
        MethodInfo? getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");
        
        BinaryExpression assignEnumerator = Expression.Assign(
            enumerator,
            Expression.Call(
                Expression.Convert(typedFrom, enumerableType),
                getEnumeratorMethod
            )
        );
        
        PropertyInfo? current = enumeratorType.GetProperty("Current");
        ParameterExpression element = Expression.Variable(elementType);
        
        LabelTarget breakLabel = Expression.Label("LoopBreak");

        MethodInfo elementCloneMethod = elementType.IsValueType()
            ? typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneStructInternal))!.MakeGenericMethod(elementType)
            : typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneClassInternal))!;
        
        Expression originalElementProperty = Expression.Property(enumerator, current!);
        Expression clonedElementCall = Expression.Call(elementCloneMethod, originalElementProperty, state);
        
        if (!elementType.IsValueType())
        {
            clonedElementCall = Expression.Convert(clonedElementCall, elementType);
        }
        
        Expression elementForAdd = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(elementType, typeof(Type))),
            elementType.IsValueType
                ? Expression.Default(elementType)
                : Expression.Constant(null, elementType),
            clonedElementCall
        );
        
        BlockExpression loopBody = Expression.Block(
            [element],
            Expression.Assign(element, elementForAdd), 
            Expression.Assign( 
                result,
                Expression.Call(result, addMethod, element)
            )
        );
        
        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, typeof(IEnumerator).GetMethod("MoveNext")!),
                loopBody,
                Expression.Break(breakLabel)
            ),
            breakLabel
        );
        
        BlockExpression block = Expression.Block(
            [typedFrom, result, enumerator],
            nullCheck,
            castFrom,
            createEmpty, 
            Expression.Call(state, YantraStatic.DeepCloneStateMethods.AddKnownRef, from, result), 
            assignEnumerator,
            loop, 
            Expression.Label(returnNullLabel, Expression.Convert(result, typeof(object)))
        );

        return Expression.Lambda<Func<object, YantraJsState, object>>(
            block,
            from,
            state
        ).Compile();
    }
    
    private static object GenProcessArrayMethod(Type type)
    {
        if (YantraJsCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(YantraJsState));
            Type ft = typeof(Func<,,>).MakeGenericType(typeof(object), typeof(YantraJsState), typeof(object));
            return Expression.Lambda(ft, pFrom, pFrom, pState).Compile();
        }
        
        Type? elementType = type.GetElementType();
        int rank = type.GetArrayRank();

        MethodInfo methodInfo;
        
        if (rank != 1 || type != elementType?.MakeArrayType())
        {
            if (rank == 2 && type == elementType?.MakeArrayType(2))
            {
                methodInfo = typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.Clone2DimArrayInternal))!.MakeGenericMethod(elementType);
            }
            else
            {
                methodInfo = typeof(YantraJsGenerator).GetPrivateStaticMethod(nameof(YantraJsGenerator.CloneAbstractArrayInternal))!;
            }
        }
        else
        {
            string methodName;

            if (YantraJsCache.IsTypeIgnored(elementType))
            {
                methodName = elementType.IsValueType ? nameof(YantraJsGenerator.Clone1DimArrayStructInternal) : nameof(YantraJsGenerator.Clone1DimArrayClassInternal);
            }
            else if (YantraJsSafeTypes.CanReturnSameObject(elementType))
            {
                methodName = nameof(YantraJsGenerator.Clone1DimArraySafeInternal);
            }
            else if (elementType.IsValueType())
            {
                methodName = nameof(YantraJsGenerator.Clone1DimArrayStructInternal);
            }
            else
            {
                methodName = nameof(YantraJsGenerator.Clone1DimArrayClassInternal);
            }
        
            methodInfo = typeof(YantraJsGenerator).GetPrivateStaticMethod(methodName)!.MakeGenericMethod(elementType);
        }

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        MethodCallExpression call = Expression.Call(methodInfo, Expression.Convert(from, type), state);

        Type funcType = typeof(Func<,,>).MakeGenericType(typeof(object), typeof(YantraJsState), typeof(object));

        return Expression.Lambda(funcType, call, from, state).Compile();
    }

    private static BlockExpression GenForeach(ParameterExpression from, Type keyType, Type? valueType, MethodInfo cloneKeyMethod, MethodInfo? cloneValueMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state, ExpressionPosition position)
    {
        Type enumeratorType = typeof(IEnumerator<>).MakeGenericType(valueType == null ? keyType : typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType));

        ParameterExpression enumerator = Expression.Variable(enumeratorType);
        MethodInfo moveNext = typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!;
        PropertyInfo current = enumeratorType.GetProperty(nameof(IEnumerator.Current))!;

        MethodInfo getEnumerator = typeof(IEnumerable).GetMethod(nameof(IEnumerable.GetEnumerator))!;

        LabelTarget breakLabel = CreateLoopWithLabel(position);

        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, moveNext),
                Expression.Block(
                    valueType is null
                        ? GenSetAdd(enumerator, current, keyType, cloneKeyMethod, local, addMethod, state)
                        : GenDictAdd(enumerator, current, keyType, valueType, cloneKeyMethod, cloneValueMethod!, local, addMethod, state)),
                Expression.Break(breakLabel)),
            breakLabel);

        return Expression.Block(
            [enumerator],
            Expression.Assign(
                enumerator,
                Expression.Convert(
                    Expression.Call(Expression.Convert(from, typeof(IEnumerable)), getEnumerator),
                    enumeratorType)),
            loop);
    }

    private static BlockExpression GenSetAdd(ParameterExpression enumerator, PropertyInfo current, Type elementType, MethodInfo cloneElementMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state)
    {
        ParameterExpression elementVar = Expression.Variable(elementType); 
        Expression originalElement = Expression.Property(enumerator, current);
        Expression clonedElementCall = Expression.Call(cloneElementMethod, originalElement, state);
        
        if (!elementType.IsValueType())
        {
            clonedElementCall = Expression.Convert(clonedElementCall, elementType);
        }
        
        Expression elementToAssign = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(elementType, typeof(Type))),
            originalElement,
            clonedElementCall
        );

        BinaryExpression assignElement = Expression.Assign(elementVar, elementToAssign);
        MethodCallExpression addElement = Expression.Call(local, addMethod, elementVar);

        return Expression.Block([elementVar], assignElement, addElement);
    }
    
    private static BlockExpression GenDictAdd(ParameterExpression enumerator, PropertyInfo current, Type keyType, Type valueType, MethodInfo cloneKeyMethod, MethodInfo cloneValueMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state)
    {
        Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
        ParameterExpression kvp = Expression.Variable(kvpType);
        BinaryExpression assignKvp = Expression.Assign(kvp, Expression.Property(enumerator, current));

        ParameterExpression keyVar = Expression.Variable(keyType); 
        ParameterExpression valueVar = Expression.Variable(valueType); 

        Expression originalKeyProperty = Expression.Property(kvp, "Key");
        Expression clonedKeyCall = Expression.Call(cloneKeyMethod, originalKeyProperty, state);
        
        if (!keyType.IsValueType())
        {
            clonedKeyCall = Expression.Convert(clonedKeyCall, keyType);
        }

        Expression keyToAssign = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(keyType, typeof(Type))),
            originalKeyProperty,
            clonedKeyCall
        );

        Expression originalValueProperty = Expression.Property(kvp, "Value");
        Expression clonedValueCall = Expression.Call(cloneValueMethod, originalValueProperty, state);
        
        if (!valueType.IsValueType())
        {
            clonedValueCall = Expression.Convert(clonedValueCall, valueType);
        }

        Expression valueToAssign = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(valueType, typeof(Type))),
            originalValueProperty,
            clonedValueCall
        );

        BinaryExpression assignKey = Expression.Assign(keyVar, keyToAssign);
        BinaryExpression assignValue = Expression.Assign(valueVar, valueToAssign);

        MethodCallExpression addKvp = Expression.Call(local, addMethod, keyVar, valueVar);

        return Expression.Block([kvp, keyVar, valueVar], assignKvp, assignKey, assignValue, addKvp);
    }

    private static object GenTuple(Type type)
    {
        if (YantraJsCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(YantraJsState));
            Type ft = typeof(Func<,,>).MakeGenericType(typeof(object), typeof(YantraJsState), typeof(object));
            return Expression.Lambda(ft, pFrom, pFrom, pState).Compile();
        }
        
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));

        ParameterExpression local = Expression.Variable(type);
        BinaryExpression assign = Expression.Assign(local, Expression.Convert(from, type));

        Type funcType = typeof(Func<object, YantraJsState, object>);

        int tupleLength = type.GenericArguments().Length;

        BinaryExpression constructor = Expression.Assign(
            local,
            Expression.New(type.GetPublicConstructors().First(x => x.GetParameters().Length == tupleLength),
                           type.GetPublicProperties().OrderBy(x => x.Name)
                               .Where(x => x.CanRead && x.Name.StartsWith("Item") && char.IsDigit(x.Name[4]))
                               .Select(x => Expression.Property(local, x.Name))));

        return Expression.Lambda(
            funcType,
            Expression.Block(
                [local],
                assign, 
                constructor, 
                Expression.Call(state, YantraStatic.DeepCloneStateMethods.AddKnownRef, from, local),
                Expression.Convert(local, typeof(object)) 
            ),
            from, state).Compile();
    }

#if YANTRA_CORE
    private static readonly Lazy<MethodInfo> jsonNodeDeepCloneMethod = new Lazy<MethodInfo>(
        () => typeof(System.Text.Json.Nodes.JsonNode).GetMethod("YantraClone")!, 
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static object GenJsonNodeProcessorModern(ExpressionPosition position)
    {
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        Expression castToJsonNode = Expression.Convert(from, typeof(System.Text.Json.Nodes.JsonNode));
        Expression deepCloneCall = Expression.Call(castToJsonNode, jsonNodeDeepCloneMethod.Value);
        Expression result = Expression.Convert(deepCloneCall, typeof(object));
        
        return Expression.Lambda<Func<object, YantraJsState, object>>(result, from, state).Compile();
    }
#else
    private static object GenJsonNodeProcessorNetstandard(Type type, ExpressionPosition position)
    {
        return YantraJsCache.GetOrAddSpecialType(type, t =>
        {
            ParameterExpression from = Expression.Parameter(typeof(object));
            ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
            MethodInfo? deepCloneMethod = t.GetMethod("YantraClone", Type.EmptyTypes);
            
            if (deepCloneMethod is null)
            {
                return Expression.Lambda<Func<object, YantraJsState, object>>(from, from, state).Compile();
            }
            
            Expression castToType = Expression.Convert(from, t);
            Expression deepCloneCall = Expression.Call(castToType, deepCloneMethod);
            Expression result = Expression.Convert(deepCloneCall, typeof(object));
            
            return Expression.Lambda<Func<object, YantraJsState, object>>(result, from, state).Compile();
        });
    }
#endif
}