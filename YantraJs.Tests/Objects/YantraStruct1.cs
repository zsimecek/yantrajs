namespace YantraJs.Tests.Objects;

public struct YantraStruct1 : IYantraStruct
{
    public int X;

    public int Do() => ++X;
}