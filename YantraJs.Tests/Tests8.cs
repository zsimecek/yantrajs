namespace YantraJs.Tests;

[TestFixture]
public class Tests8 : Base
{
    [Test]
    public void CaseTuple()
    {
        Tuple<int, int> c = new Tuple<int, int>(1, 2).YantraClone();
        Assert.That(c.Item1, Is.EqualTo(1));
        Assert.That(c.Item2, Is.EqualTo(2));

        c = new Tuple<int, int>(1, 2).YantraShallowClone();
        Assert.That(c.Item1, Is.EqualTo(1));
        Assert.That(c.Item2, Is.EqualTo(2));

        Tuple<int, int, int, int, int, int, int> cc = new Tuple<int, int, int, int, int, int, int>(1, 2, 3, 4, 5, 6, 7).YantraClone();
        Assert.That(cc.Item7, Is.EqualTo(7));

        Tuple<int, Generic<object>> tuple = new Tuple<int, Generic<object>>(1, new Generic<object>());
        tuple.Item2.Value = tuple;
        Tuple<int, Generic<object>> ccc = tuple.YantraClone();
        Assert.That(ccc, Is.EqualTo(ccc.Item2.Value));
    }

    [Test]
    public void CaseGeneric2()
    {
        Generic<int> c = new Generic<int>
        {
            Value = 12
        };
        Assert.That(c.YantraClone().Value, Is.EqualTo(12));

        Generic<object> c2 = new Generic<object>
        {
            Value = 12
        };
        Assert.That(c2.YantraClone().Value, Is.EqualTo(12));
    }

    public class C1
    {
        public int X { get; set; }
    }

    public class C2 : C1
    {
        public int Y { get; set; }
    }

    public class Generic<T>
    {
        public T Value { get; set; }
    }

    [Test]
    public void CaseTupleWith_Inheritance_And_Same_Object()
    {
        C2 c2 = new C2 { X = 1, Y = 2 };
        Tuple<C1, C2> c = new Tuple<C1, C2>(c2, c2).YantraClone();
        Tuple<C1, C2> cs = new Tuple<C1, C2>(c2, c2).YantraShallowClone();
        c2.X = 42;
        c2.Y = 42;
        Assert.That(c.Item1.X, Is.EqualTo(1));
        Assert.That(c.Item2.Y, Is.EqualTo(2));
        Assert.That(c.Item2, Is.EqualTo(c.Item1));

        Assert.That(cs.Item1.X, Is.EqualTo(42));
        Assert.That(cs.Item2.Y, Is.EqualTo(42));
        Assert.That(cs.Item2, Is.EqualTo(cs.Item1));
    }
}