using System.Linq.Expressions;
using System.Reflection;

namespace YantraJs.Impl;

internal static class YantraExprGen
{
    internal static object GenClonerInternal(Type realType, bool isDeepClone)
    {
        return realType.IsValueType() ? throw new InvalidOperationException("Valid only for reference types") : GenProcessMethod(realType, isDeepClone);
    }

    private static object GenProcessMethod(Type type, bool isDeepClone)
    {
        if (type.IsArray)
        {
            return GenProcessArrayMethod(type, isDeepClone);
        }

        Type methodType = typeof(object);

        List<Expression> expressionList = [];

        ParameterExpression from = Expression.Parameter(methodType);
        ParameterExpression fromLocal = from;
        ParameterExpression to = Expression.Parameter(methodType);
        ParameterExpression toLocal = to;
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));
        
        {
            fromLocal = Expression.Variable(type);
            toLocal = Expression.Variable(type);
            expressionList.Add(Expression.Assign(fromLocal, Expression.Convert(from, type)));
            expressionList.Add(Expression.Assign(toLocal, Expression.Convert(to, type)));

            if (isDeepClone)
            {
                expressionList.Add(Expression.Call(state, typeof(YantraJsState).GetMethod(nameof(YantraJsState.AddKnownRef))!, from, to));
            }
        }

        List<FieldInfo> fi = [];
        Type? tp = type;
        do
        {
            if (tp == typeof(ContextBoundObject))
            {
                break;
            }

            fi.AddRange(tp.GetDeclaredFields());
            tp = tp.BaseType();
        }
        while (tp != null);

        foreach (FieldInfo fieldInfo in fi)
        {
            if (isDeepClone && !YantraJsSafeTypes.CanReturnSameObject(fieldInfo.FieldType))
            {
                MethodInfo methodInfo = fieldInfo.FieldType.IsValueType()
                    ? YantraStatic.DeepClonerGeneratorMethods.CloneStructInternal.MakeGenericMethod(fieldInfo.FieldType)
                    : YantraStatic.DeepClonerGeneratorMethods.CloneClassInternal;

                MemberExpression get = Expression.Field(fromLocal, fieldInfo);
                
                Expression call = Expression.Call(methodInfo, get, state);
                if (!fieldInfo.FieldType.IsValueType())
                    call = Expression.Convert(call, fieldInfo.FieldType);

                if (fieldInfo.IsInitOnly)
                {
                    ConstantExpression setter = Expression.Constant(FieldGen.GetFieldSetter(fieldInfo));
                    expressionList.Add(
                        Expression.Invoke(
                            setter,
                            Expression.Convert(toLocal, typeof(object)),
                            Expression.Convert(call, typeof(object))
                        )
                    );
                }
                else
                {
                    expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), call));
                }
            }
            else
            {
                Expression sourceValue = Expression.Field(fromLocal, fieldInfo);
                if (fieldInfo.IsInitOnly)
                {
                    ConstantExpression setter = Expression.Constant(FieldGen.GetFieldSetter(fieldInfo));
                    expressionList.Add(
                        Expression.Invoke(
                            setter,
                            Expression.Convert(toLocal, typeof(object)),
                            Expression.Convert(sourceValue, typeof(object))
                        )
                    );
                }
                else
                {
                    expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), sourceValue));
                }
            }
        }

        expressionList.Add(Expression.Convert(toLocal, methodType));

        Type funcType = typeof(Func<,,,>).MakeGenericType(methodType, methodType, typeof(YantraJsState), methodType);

        List<ParameterExpression> blockParams = [];
        if (from != fromLocal) blockParams.Add(fromLocal);
        if (to != toLocal) blockParams.Add(toLocal);

        return Expression.Lambda(funcType, Expression.Block(blockParams, expressionList), from, to, state).Compile();
    }

    private static object GenProcessArrayMethod(Type type, bool isDeep)
    {
        Type elementType = type.GetElementType()!;
        int rank = type.GetArrayRank();

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression to = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(YantraJsState));

        Type funcType = typeof(Func<,,,>).MakeGenericType(typeof(object), typeof(object), typeof(YantraJsState), typeof(object));

        if (rank == 1 && type == elementType.MakeArrayType())
        {
            if (!isDeep)
            {
                MethodCallExpression callS = Expression.Call(
                    typeof(YantraExprGen).GetPrivateStaticMethod(nameof(ShallowClone1DimArraySafeInternal))!
                                                                                    .MakeGenericMethod(elementType), Expression.Convert(from, type), Expression.Convert(to, type));
                return Expression.Lambda(funcType, callS, from, to, state).Compile();
            }
            else
            {
                string methodName = nameof(Clone1DimArrayClassInternal);
                if (YantraJsSafeTypes.CanReturnSameObject(elementType)) methodName = nameof(Clone1DimArraySafeInternal);
                else if (elementType.IsValueType()) methodName = nameof(Clone1DimArrayStructInternal);
                MethodInfo methodInfo = typeof(YantraExprGen).GetPrivateStaticMethod(methodName)!.MakeGenericMethod(elementType);
                MethodCallExpression callS = Expression.Call(methodInfo, Expression.Convert(from, type), Expression.Convert(to, type), state);
                return Expression.Lambda(funcType, callS, from, to, state).Compile();
            }
        }

        {
            MethodInfo methodInfo;
            if (rank == 2 && type == elementType.MakeArrayType(2))
                methodInfo = typeof(YantraExprGen).GetPrivateStaticMethod(nameof(Clone2DimArrayInternal))!.MakeGenericMethod(elementType);
            else
                methodInfo = typeof(YantraExprGen).GetPrivateStaticMethod(nameof(CloneAbstractArrayInternal))!;

            MethodCallExpression callS = Expression.Call(methodInfo, Expression.Convert(from, type), Expression.Convert(to, type), state, Expression.Constant(isDeep));
            return Expression.Lambda(funcType, callS, from, to, state).Compile();
        }
    }
    
    internal static T[] ShallowClone1DimArraySafeInternal<T>(T[] objFrom, T[] objTo)
    {
        int l = Math.Min(objFrom.Length, objTo.Length);
        Array.Copy(objFrom, objTo, l);
        return objTo;
    }

    internal static T[] Clone1DimArraySafeInternal<T>(T[] objFrom, T[] objTo, YantraJsState jsState)
    {
        int l = Math.Min(objFrom.Length, objTo.Length);
        jsState.AddKnownRef(objFrom, objTo);
        Array.Copy(objFrom, objTo, l);
        return objTo;
    }

    internal static T[]? Clone1DimArrayStructInternal<T>(T[]? objFrom, T[]? objTo, YantraJsState jsState)
    {
        if (objFrom == null || objTo == null) return null;
        int l = Math.Min(objFrom.Length, objTo.Length);
        jsState.AddKnownRef(objFrom, objTo);
        Func<T, YantraJsState, T>? cloner = YantraJsGenerator.GetClonerForValueType<T>();

        if (cloner is not null)
        {
            for (int i = 0; i < l; i++)
            {
                objTo[i] = cloner(objTo[i], jsState);
            }   
        }

        return objTo;
    }

    internal static T[]? Clone1DimArrayClassInternal<T>(T[]? objFrom, T[]? objTo, YantraJsState jsState)
    {
        if (objFrom == null || objTo == null) return null;
        int l = Math.Min(objFrom.Length, objTo.Length);
        jsState.AddKnownRef(objFrom, objTo);
        for (int i = 0; i < l; i++)
            objTo[i] = (T)YantraJsGenerator.CloneClassInternal(objFrom[i], jsState)!;

        return objTo;
    }

    internal static T[,]? Clone2DimArrayInternal<T>(T[,]? objFrom, T[,]? objTo, YantraJsState jsState, bool isDeep)
    {
        if (objFrom == null || objTo == null) return null;
        if (objFrom.GetLowerBound(0) != 0 || objFrom.GetLowerBound(1) != 0
                                          || objTo.GetLowerBound(0) != 0 || objTo.GetLowerBound(1) != 0)
            return (T[,]?) CloneAbstractArrayInternal(objFrom, objTo, jsState, isDeep);

        int l1 = Math.Min(objFrom.GetLength(0), objTo.GetLength(0));
        int l2 = Math.Min(objFrom.GetLength(1), objTo.GetLength(1));
        jsState.AddKnownRef(objFrom, objTo);
        if ((!isDeep || YantraJsSafeTypes.CanReturnSameObject(typeof(T)))
            && objFrom.GetLength(0) == objTo.GetLength(0)
            && objFrom.GetLength(1) == objTo.GetLength(1))
        {
            Array.Copy(objFrom, objTo, objFrom.Length);
            return objTo;
        }

        if (!isDeep)
        {
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    objTo[i, k] = objFrom[i, k];
            return objTo;
        }

        if (typeof(T).IsValueType())
        {
            Func<T, YantraJsState, T>? cloner = YantraJsGenerator.GetClonerForValueType<T>();

            if (cloner is null) return objTo;
            for (int i = 0; i < l1; i++)
            for (int k = 0; k < l2; k++)
                objTo[i, k] = cloner(objFrom[i, k], jsState);
        }
        else
        {
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    objTo[i, k] = (T)YantraJsGenerator.CloneClassInternal(objFrom[i, k], jsState)!;
        }

        return objTo;
    }
    
    internal static Array? CloneAbstractArrayInternal(Array? objFrom, Array? objTo, YantraJsState jsState, bool isDeep)
    {
        if (objFrom == null || objTo == null) return null;
        int rank = objFrom.Rank;

        if (objTo.Rank != rank)
            throw new InvalidOperationException("Invalid rank of target array");
        int[] lowerBoundsFrom = Enumerable.Range(0, rank).Select(objFrom.GetLowerBound).ToArray();
        int[] lowerBoundsTo = Enumerable.Range(0, rank).Select(objTo.GetLowerBound).ToArray();
        int[] lengths = Enumerable.Range(0, rank).Select(x => Math.Min(objFrom.GetLength(x), objTo.GetLength(x))).ToArray();
        int[] idxesFrom = Enumerable.Range(0, rank).Select(objFrom.GetLowerBound).ToArray();
        int[] idxesTo = Enumerable.Range(0, rank).Select(objTo.GetLowerBound).ToArray();

        jsState.AddKnownRef(objFrom, objTo);
        
        if (lengths.Any(x => x == 0))
            return objTo;

        while (true)
        {
            objTo.SetValue(
                isDeep
                    ? YantraJsGenerator.CloneClassInternal(
                        objFrom.GetValue(idxesFrom),
                        jsState)
                    : objFrom.GetValue(idxesFrom), idxesTo);
            int ofs = rank - 1;
            while (true)
            {
                idxesFrom[ofs]++;
                idxesTo[ofs]++;
                if (idxesFrom[ofs] >= lowerBoundsFrom[ofs] + lengths[ofs])
                {
                    idxesFrom[ofs] = lowerBoundsFrom[ofs];
                    idxesTo[ofs] = lowerBoundsTo[ofs];
                    ofs--;
                    if (ofs < 0) return objTo;
                }
                else
                    break;
            }
        }
    }
}