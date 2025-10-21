namespace YantraJs.Tests;

[TestFixture]
public class YantraCtfTest : Base
{
    public class CtfNode
    {
        public CtfNode Parent { get; set; }
        public CtfNode Child { get; set; }
        public CtfNode Prev { get; set; }
        public CtfNode Next { get; set; }
        public List<int> Data { get; set; } = [];
    }
    
    // todo: fixme
    [Test]
    public void Ctf()
    {
        // Arrange
        CtfNode root = new CtfNode { Data = [0] };
        CtfNode current = root;
        const int nodeCount = 10_000;

        for (int i = 1; i < nodeCount; i++)
        {
            CtfNode next = new CtfNode { Data = [i], Parent = current };
            current.Child = next;
            next.Prev = current;
            current.Next = next;
            current = next;
        }
        
        current.Next = root;
        root.Prev = current;

        // Act
        CtfNode clone = root.YantraClone();

        // Assert
        Assert.That(clone, Is.Not.SameAs(root));
        
        CtfNode forward = clone;
        for (int i = 0; i < nodeCount; i++)
        {
            forward = forward.Next;
        }
        Assert.That(forward, Is.SameAs(clone));

        CtfNode backward = clone;
        for (int i = 0; i < nodeCount; i++)
        {
            backward = backward.Prev;
        }
        Assert.That(backward, Is.SameAs(clone));
        
        CtfNode originalNode = root;
        CtfNode clonedNode = clone;
        for (int i = 0; i < nodeCount / 2; i++)
        {
            originalNode = originalNode.Next;
            clonedNode = clonedNode.Next;
        }
        
        Assert.That(clonedNode, Is.Not.SameAs(originalNode));
        Assert.That(clonedNode.Data, Is.Not.SameAs(originalNode.Data));
        Assert.That(clonedNode.Data, Is.EqualTo(originalNode.Data));
        
        clonedNode.Data.Add(999);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(originalNode.Data, Has.Count.EqualTo(1));
            Assert.That(clonedNode.Data, Has.Count.EqualTo(2));
        }
        
        Assert.That(originalNode.Data[0], Is.Not.EqualTo(clonedNode.Data[1]));
    }
}