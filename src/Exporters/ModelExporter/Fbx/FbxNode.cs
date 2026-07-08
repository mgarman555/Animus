namespace GameAssetExplorer.Exporters.ModelExporter.Fbx;

/// <summary>
/// One node record in an FBX document tree. A node has a name, an ordered list of typed
/// properties, and an ordered list of child nodes — mirroring the FBX binary node record.
///
/// Properties are plain CLR values; <see cref="FbxBinaryWriter"/> maps each to its FBX
/// property type code by runtime type:
///   short→Y, bool→C, int→I, long→L, float→F, double→D, string→S, byte[]→R,
///   and the array types int[]→i, long[]→l, float[]→f, double[]→d, bool[]→b.
/// Use the exact CLR type you want emitted (e.g. an int is written as I, not L).
/// </summary>
public sealed class FbxNode
{
    public string Name { get; }
    public List<object> Properties { get; } = new();
    public List<FbxNode> Children { get; } = new();

    public FbxNode(string name) => Name = name;

    public FbxNode(string name, params object[] properties) : this(name)
        => Properties.AddRange(properties);

    /// <summary>Add a property and return this node (fluent).</summary>
    public FbxNode Prop(object value) { Properties.Add(value); return this; }

    /// <summary>Create, append and return a child node with the given name + properties.</summary>
    public FbxNode Add(string name, params object[] properties)
    {
        var child = new FbxNode(name, properties);
        Children.Add(child);
        return child;
    }

    /// <summary>Append an already-built child and return it.</summary>
    public FbxNode Add(FbxNode child) { Children.Add(child); return child; }
}
