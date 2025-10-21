using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using YantraJs.Tests.Objects;

namespace YantraJs.Tests;

[TestFixture]
public class Tests11 : Base
{
    [Test]
    public void CaseSimpleObject()
    {
        YantraObject1 obj = new YantraObject1 { Int = 42, Byte = 42, Short = 42, Long = 42, DateTime = new DateTime(2001, 01, 01), Char = 'X', Decimal = 1.2m, Double = 1.3, Float = 1.4f, String = "test1", UInt = 42, ULong = 42, UShort = 42, Bool = true, IntPtr = new IntPtr(42), UIntPtr = new UIntPtr(42), Enum = AttributeTargets.Delegate };

        YantraObject1 cloned = obj.YantraClone();
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

    public struct S1
    {
        public int A;
    }

    public struct S2
    {
        public S3 S;
    }

    public struct S3
    {
        public bool B;
    }

    [Test(Description = "We have an special logic for simple structs, so, this test checks that this logic works correctly")]
    public void SimpleStruct()
    {
        S1 s1 = new S1 { A = 1 };
        S1 cloned = s1.YantraClone();
        Assert.That(cloned.A, Is.EqualTo(1));
    }

    [Test(Description = "We have an special logic for simple structs, so, this test checks that this logic works correctly")]
    public void Simple_Struct_With_Child()
    {
        S2 s1 = new S2 { S = new S3 { B = true } };
        S2 cloned = s1.YantraClone();
        Assert.That(cloned.S.B, Is.EqualTo(true));
    }

    public class ClassWithNullable
    {
        public int? A { get; set; }

        public long? B { get; set; }
    }

    [Test]
    public void CaseNullable_Shoild_Be_Cloned()
    {
        ClassWithNullable c = new ClassWithNullable { B = 42 };
        ClassWithNullable cloned = c.YantraClone();
        Assert.That(cloned.A, Is.Null);
        Assert.That(cloned.B, Is.EqualTo(42));
    }

    public class C1
    {
        public C2 C { get; set; }
    }

    public class C2
    {
    }

    public class C3
    {
        public string X { get; set; }
    }

    [Test]
    public void CaseClass()
    {
        C1 c1 = new C1
        {
            C = new C2()
        };
        C1 cloned = c1.YantraClone();
        Assert.That(cloned.C, Is.Not.Null);
        Assert.That(cloned.C, Is.Not.EqualTo(c1.C));
    }

    public struct S4
    {
        public C2 C;

        public int F;
    }

    [Test]
    public void CaseStructWithClass()
    {
        S4 c1 = new S4
        {
            F = 1,
            C = new C2()
        };
        S4 cloned = c1.YantraClone();
        c1.F = 2;
        Assert.That(cloned.C, Is.Not.Null);
        Assert.That(cloned.F, Is.EqualTo(1));
    }

    [Test]
    public void CasePrivitive()
    {
        Assert.That(3.YantraClone(), Is.EqualTo(3));
        Assert.That('x'.YantraClone(), Is.EqualTo('x'));
        Assert.That("xxxxxxxxxx yyyyyyyyyyyyyy".YantraClone(), Is.EqualTo("xxxxxxxxxx yyyyyyyyyyyyyy"));
        Assert.That(string.Empty.YantraClone(), Is.EqualTo(string.Empty));
        Assert.That(ReferenceEquals("y".YantraClone(), "y"), Is.True);
        Assert.That(DateTime.MinValue.YantraClone(), Is.EqualTo(DateTime.MinValue));
        Assert.That(AttributeTargets.Delegate.YantraClone(), Is.EqualTo(AttributeTargets.Delegate));
        Assert.That(((object)null).YantraClone(), Is.Null);
        object obj = new object();
        Assert.That(obj.YantraClone(), Is.Not.Null);
        Assert.That(true.YantraClone(), Is.True);
        Assert.That(((object)true).YantraClone(), Is.True);
        Assert.That(obj.YantraClone().GetType(), Is.EqualTo(typeof(object)));
        Assert.That(obj.YantraClone(), Is.Not.EqualTo(obj));
    }

    [Test]
    public void CaseGuidTest()
    {
        Guid g = Guid.NewGuid();
        Assert.That(g.YantraClone(), Is.EqualTo(g));
    }

    private class UnsafeObject
    {
        [SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1401:FieldsMustBePrivate", Justification = "Reviewed. Suppression is OK here.")]
        public unsafe void* Void;

        [SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1401:FieldsMustBePrivate", Justification = "Reviewed. Suppression is OK here.")]
        public unsafe int* Int;
    }

    [Test]
    public void CaseUnsafe()
    {
        UnsafeObject u = new UnsafeObject();
        int i = 1;
        int j = 2;
        unsafe
        {
            u.Int = &i;
            u.Void = &i;
        }

        UnsafeObject cloned = u.YantraClone();
        unsafe
        {
            u.Int = &j;
            Assert.That(cloned.Int == &i, Is.True);
            Assert.That(cloned.Void == &i, Is.True);
        }
    }

    [Test]
    public void CaseString_In_Class_Should_Not_Be_Cloned()
    {
        C3 c = new C3 { X = "aaa" };
        C3 cloned = c.YantraClone();
        Assert.That(cloned.X, Is.EqualTo(c.X));
        Assert.That(ReferenceEquals(cloned.X, c.X), Is.True);
    }

    public sealed class C6
    {
        public readonly int X = 1;

        private readonly object y = new object();

        // it is struct - and it can't be null, but it's readonly and should be copied
        // also it private to ensure it copied correctly
        #pragma warning disable 169
        private readonly StructWithObject z;
        #pragma warning restore 169

        public object GetY() => y;
    }

    public struct StructWithObject
    {
        public readonly object Z;
    }

    [Test]
    public void CaseObject_With_Readonly_Fields()
    {
        C6 c = new C6();
        C6 clone = c.YantraClone();
        Assert.That(clone, Is.Not.EqualTo(c));
        Assert.That(clone.X, Is.EqualTo(1));
        Assert.That(clone.GetY(), Is.Not.Null);
        Assert.That(clone.GetY(), Is.Not.EqualTo(c.GetY()));
        Assert.That(clone.GetY(), Is.Not.EqualTo(c.GetY()));
    }

    public class VirtualClass1
    {
        public virtual int A { get; set; }

        public virtual int B { get; set; }

        // not safe
        public object X { get; set; }
    }

    public class VirtualClass2 : VirtualClass1
    {
        public override int B { get; set; }
    }

    [Test(Description = "Nothings special, just for checking")]
    public void Class_With_Virtual_Methods()
    {
        VirtualClass2 v2 = new VirtualClass2
        {
            A = 1,
            B = 2
        };
        VirtualClass1 v1 = v2;
        v1.A = 3;
        VirtualClass2? clone = v1.YantraClone() as VirtualClass2;
        v2.B = 0;
        v2.A = 0;
        Assert.That(clone.B, Is.EqualTo(2));
        Assert.That(clone.A, Is.EqualTo(3));
    }

    [Test(Description = "DBNull is compared by value, so, we don't need to clone it")]
    public void DbNull_Should_Not_Be_Cloned()
    {
        DBNull v = DBNull.Value;
        Assert.That(v == v.YantraClone(), Is.True);
        Assert.That(v == v.YantraShallowClone(), Is.True);
    }

    public class EmptyClass {}

    [Test]
    public void CaseEmpty()
    {
        EmptyClass v = new EmptyClass();
        Assert.That(ReferenceEquals(v, v.YantraClone()), Is.False);
        Assert.That(ReferenceEquals(v, v.YantraShallowClone()), Is.False);
    }

    [Test(Description = "Reflection classes should not be cloned")]
    public void MethodInfo_Should_Not_Be_Cloned()
    {
        MethodInfo? v = GetType().GetMethod("MethodInfo_Should_Not_Be_Cloned");

        Assert.That(ReferenceEquals(v, v.YantraClone()), Is.True);
        Assert.That(ReferenceEquals(v, v.YantraShallowClone()), Is.True);
    }

    public class Readonly1
    {
        public readonly object X;

        public object Z = new object();

        public Readonly1(string x) => X = x;
    }

    [Test]
    public void CaseReadonly_Field_Should_Remain_ReadOnly()
    {
        Readonly1 c = new Readonly1("Z").YantraClone();
        Assert.That(c.X, Is.EqualTo("Z"));
        Assert.That(typeof(Readonly1).GetField("X").IsInitOnly, Is.True);
    }

    [Test]
    public void CaseSystem_Type_Should_Not_Be_Cloned()
    {
        // it used for dictionaries as key. there are no sense to copy it
        Type t = GetType(); // RuntimeType
        Type clone = t.YantraClone();
        Assert.That(ReferenceEquals(t, clone));
    }
}