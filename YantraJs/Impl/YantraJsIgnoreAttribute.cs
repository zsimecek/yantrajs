namespace YantraJs.Impl;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event)]
public class YantraJsIgnoreAttribute(bool ignored = true) : Attribute
{
    public bool Ignored { get; } = ignored;
}