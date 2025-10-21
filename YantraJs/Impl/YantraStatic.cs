using System.Reflection;

namespace YantraJs.Impl;

internal static class YantraStatic
{
    internal static class DeepCloneStateMethods
    {
        internal static MethodInfo AddKnownRef { get; } = typeof(YantraJsState).GetMethod(nameof(YantraJsState.AddKnownRef))!;
    }

    internal static class DeepClonerGeneratorMethods
    {
        internal static MethodInfo CloneStructInternal { get; } =
            typeof(YantraJsGenerator).GetMethod(nameof(YantraJsGenerator.CloneStructInternal),
                                                  BindingFlags.NonPublic | BindingFlags.Static)!;
        internal static MethodInfo CloneClassInternal { get; } =
            typeof(YantraJsGenerator).GetMethod(nameof(YantraJsGenerator.CloneClassInternal),
                                                  BindingFlags.NonPublic | BindingFlags.Static)!;

        internal static MethodInfo MakeFieldCloneMethodInfo(Type fieldType) =>
            fieldType.IsValueType
                ? CloneStructInternal.MakeGenericMethod(fieldType)
                : CloneClassInternal;

        internal static MethodInfo GetClonerForValueType { get; } =
            typeof(YantraJsGenerator).GetMethod(nameof(YantraJsGenerator.GetClonerForValueType),
                                                  BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}