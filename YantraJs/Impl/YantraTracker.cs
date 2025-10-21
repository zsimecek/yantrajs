using System.Linq.Expressions;
using System.Reflection;

namespace YantraJs.Impl;

public abstract class YantraTracker
{
    protected abstract object DoCloneObject(object obj);

    private static readonly YantraTracker instance;

    public static object CloneObject(object obj) => instance.DoCloneObject(obj);

    static YantraTracker() => instance = new ShallowSafeObjectCloner();

    private class ShallowSafeObjectCloner : YantraTracker
    {
        private static readonly Func<object, object> cloneFunc;

        static ShallowSafeObjectCloner()
        {
            MethodInfo? methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone));
            ParameterExpression p = Expression.Parameter(typeof(object));
            MethodCallExpression mce = Expression.Call(p, methodInfo);
            cloneFunc = Expression.Lambda<Func<object, object>>(mce, p).Compile();
        }

        protected override object DoCloneObject(object obj) => cloneFunc(obj);
    }
}