using YantraJs.Tests.Objects;

namespace YantraJs.Tests;

[TestFixture]
public class Tests12 : Base
{
    [Test]
    public void CaseSimpleObject()
    {
        YantraObject1 obj = new YantraObject1 { Int = 42, Byte = 42, Short = 42, Long = 42, DateTime = new DateTime(2001, 01, 01), Char = 'X', Decimal = 1.2m, Double = 1.3, Float = 1.4f, String = "test1", UInt = 42, ULong = 42, UShort = 42, Bool = true, IntPtr = new IntPtr(42), UIntPtr = new UIntPtr(42), Enum = AttributeTargets.Delegate };

        YantraObject1 cloned = obj.YantraShallowClone();
        Assert.That(cloned.Byte, Is.EqualTo(42));
        Assert.That(cloned.Short, Is.EqualTo(42));
        Assert.That(cloned.UShort, Is.EqualTo(42));
        Assert.That(cloned.Int, Is.EqualTo(42));
        Assert.That(cloned.UInt, Is.EqualTo(42));
        Assert.That(cloned.Long, Is.EqualTo(42));
        Assert.That(cloned.ULong, Is.EqualTo(42));
        Assert.That(cloned.Decimal, Is.EqualTo(1.2));
        Assert.That(cloned.Double, Is.EqualTo(1.3));
        Assert.That(cloned.Float, Is.EqualTo(1.4f));
        Assert.That(cloned.Char, Is.EqualTo('X'));
        Assert.That(cloned.String, Is.EqualTo("test1"));
        Assert.That(cloned.DateTime, Is.EqualTo(new DateTime(2001, 1, 1)));
        Assert.That(cloned.Bool, Is.EqualTo(true));
        Assert.That(cloned.IntPtr, Is.EqualTo(new IntPtr(42)));
        Assert.That(cloned.UIntPtr, Is.EqualTo(new UIntPtr(42)));
        Assert.That(cloned.Enum, Is.EqualTo(AttributeTargets.Delegate));
    }

    private class C1
    {
        public object X { get; set; }
    }

    [Test]
    public void CaseReference_Should_Not_Be_Copied()
    {
        C1 c1 = new C1
        {
            X = new object()
        };
        C1 clone = c1.YantraShallowClone();
        Assert.That(clone.X, Is.EqualTo(c1.X));
    }

    private struct S1 : IDisposable
    {
        public int X;

        public void Dispose()
        {
        }
    }

    [Test]
    public void CaseStruct()
    {
        S1 c1 = new S1();
        c1.X = 1;
        S1 clone = c1.YantraShallowClone();
        c1.X = 2;
        Assert.That(clone.X, Is.EqualTo(1));
    }

    [Test]
    public void CaseStruct_As_Object()
    {
        S1 c1 = new S1();
        c1.X = 1;
        S1 clone = (S1)((IDisposable)c1).YantraShallowClone();
        c1.X = 2;
        Assert.That(clone.X, Is.EqualTo(1));
    }

    [Test]
    public void CaseStruct_As_Interface()
    {
        IYantraStruct? c1 = new YantraStruct1() as IYantraStruct;
        Assert.That(c1.Do(), Is.EqualTo(1));
        Assert.That(c1.Do(), Is.EqualTo(2));
        IYantraStruct clone = c1.YantraShallowClone();
        Assert.That(c1.Do(), Is.EqualTo(3));
        Assert.That(clone.Do(), Is.EqualTo(3));
    }

    [Test]
    public void CaseStruct_As_Interface_For_DeepClone_Too()
    {
        IYantraStruct? c1 = new YantraStruct1() as IYantraStruct;
        Assert.That(c1.Do(), Is.EqualTo(1));
        Assert.That(c1.Do(), Is.EqualTo(2));
        IYantraStruct clone = c1.YantraClone();
        Assert.That(c1.Do(), Is.EqualTo(3));
        Assert.That(clone.Do(), Is.EqualTo(3));
    }

    [Test]
    public void CaseStruct_As_Interface_In_Object()
    {
        IYantraStruct? c1 = new YantraStruct1() as IYantraStruct;
        Tuple<IYantraStruct> t = new Tuple<IYantraStruct>(c1);
        Assert.That(t.Item1.Do(), Is.EqualTo(1));
        Assert.That(t.Item1.Do(), Is.EqualTo(2));
        Tuple<IYantraStruct> clone = t.YantraShallowClone();
        Assert.That(t.Item1.Do(), Is.EqualTo(3));
        // shallow clone do not copy object
        Assert.That(clone.Item1.Do(), Is.EqualTo(4));
    }

    [Test]
    public void CaseStruct_As_Interface_For_DeepClone_Too_In_Object()
    {
        IYantraStruct? c1 = new YantraStruct1() as IYantraStruct;
        Tuple<IYantraStruct> t = new Tuple<IYantraStruct>(c1);
        Assert.That(t.Item1.Do(), Is.EqualTo(1));
        Assert.That(t.Item1.Do(), Is.EqualTo(2));
        Tuple<IYantraStruct> clone = t.YantraClone();
        Assert.That(t.Item1.Do(), Is.EqualTo(3));
        // deep clone copy object
        Assert.That(clone.Item1.Do(), Is.EqualTo(3));
    }

    [Test]
    public void CasePrimitive()
    {
        Assert.That(((object)null).YantraShallowClone(), Is.Null);
        Assert.That(3.YantraShallowClone(), Is.EqualTo(3));
    }

    [Test]
    public void CaseArray()
    {
        int[] a = [3, 4];
        int[] clone = a.YantraShallowClone();
        Assert.That(clone.Length, Is.EqualTo(2));
        Assert.That(clone[0], Is.EqualTo(3));
        Assert.That(clone[1], Is.EqualTo(4));
    }
}