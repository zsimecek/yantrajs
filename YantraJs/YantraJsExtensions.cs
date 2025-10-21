using YantraJs.Impl;

namespace YantraJs;

internal static class YantraJsExtensions
{
    extension<T>(T obj)
    {
        public T YantraClone() => YantraJsGenerator.CloneObject(obj);
        public TTo YantraCloneTo<TTo>(TTo objTo) where TTo : class, T => (TTo)YantraJsGenerator.CloneObjectTo(obj, objTo, true);
        public TTo YantraShallowCloneTo<TTo>(TTo objTo) where TTo : class, T => (TTo)YantraJsGenerator.CloneObjectTo(obj, objTo, false);
        public T YantraShallowClone() => YantraShallow.CloneObject(obj);
    }
}