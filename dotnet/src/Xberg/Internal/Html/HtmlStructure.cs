namespace Xberg.Internal.Html;

/// <summary>What a structure node holds.</summary>
internal enum StructureKind
{
    /// <summary>A heading and everything under it until a heading of equal or higher rank.</summary>
    Group,
    Heading,
    Paragraph,
    List,
    ListItem,
    Table,
    Image,
    Code,
}

/// <summary>One node of a document structure, in a flat array with index-based links.</summary>
internal sealed class StructureNode
{
    public StructureKind Kind;

    /// <summary>Heading, paragraph, list-item or code text — the markdown the block rendered to.</summary>
    public string Text = "";

    public byte Level;                    // Heading
    public string? Label;                 // Group
    public bool Ordered;                  // List
    public string? Language;              // Code
    public string? Src;                   // Image
    public string? Description;           // Image
    public List<List<string>>? Cells;     // Table

    public int Parent = -1;
    public List<int> Children = new();
}

/// <summary>
/// Builds a document structure during the converter's DOM walk.
/// </summary>
/// <remarks>
/// Ports html-to-markdown's <c>StructureCollector</c>: the converter's block handlers call the
/// <c>Push…</c> methods as they emit, so a node's text is the markdown that block produced, not a
/// re-derived plain rendering. That is why a `&lt;br&gt;` reaches the structure as a markdown hard
/// break and a link inside a heading keeps its target.
/// </remarks>
internal sealed class HtmlStructureCollector
{
    private readonly List<StructureNode> _nodes = new();

    /// <summary>Open heading groups: (level, node index).</summary>
    private readonly List<(byte Level, int Index)> _sections = new();

    /// <summary>Open list containers, innermost last.</summary>
    private readonly List<int> _lists = new();

    public IReadOnlyList<StructureNode> Nodes => _nodes;

    /// <summary>
    /// Record a heading, opening the group that owns everything under it. Groups of equal or
    /// higher rank close first.
    /// </summary>
    public void PushHeading(byte level, string text)
    {
        while (_sections.Count > 0 && _sections[^1].Level >= level) _sections.RemoveAt(_sections.Count - 1);

        int group = Add(new StructureNode
        {
            Kind = StructureKind.Group,
            Label = text,
            Level = level,
        });
        _sections.Add((level, group));

        Add(new StructureNode { Kind = StructureKind.Heading, Level = level, Text = text }, group);
    }

    public void PushParagraph(string text)
    {
        if (text.Length == 0) return;
        Add(new StructureNode { Kind = StructureKind.Paragraph, Text = text });
    }

    public void PushListStart(bool ordered)
    {
        int idx = Add(new StructureNode { Kind = StructureKind.List, Ordered = ordered });
        _lists.Add(idx);
    }

    public void PushListEnd()
    {
        if (_lists.Count > 0) _lists.RemoveAt(_lists.Count - 1);
    }

    public void PushListItem(string text)
    {
        if (text.Length == 0) return;
        int parent = _lists.Count > 0 ? _lists[^1] : CurrentParent();
        Add(new StructureNode { Kind = StructureKind.ListItem, Text = text }, parent);
    }

    public void PushTable(List<List<string>> cells) =>
        Add(new StructureNode { Kind = StructureKind.Table, Cells = cells });

    public void PushImage(string? src, string? description) =>
        Add(new StructureNode
        {
            Kind = StructureKind.Image,
            Src = string.IsNullOrEmpty(src) ? null : src,
            Description = string.IsNullOrEmpty(description) ? null : description,
        });

    public void PushCode(string text, string? language) =>
        Add(new StructureNode { Kind = StructureKind.Code, Text = text, Language = language });

    /// <summary>The structural parent for a new node: the innermost open group, if any.</summary>
    private int CurrentParent() => _sections.Count > 0 ? _sections[^1].Index : -1;

    private int Add(StructureNode node, int? parentOverride = null)
    {
        int parent = parentOverride ?? CurrentParent();
        node.Parent = parent;
        int idx = _nodes.Count;
        _nodes.Add(node);
        if (parent >= 0) _nodes[parent].Children.Add(idx);
        return idx;
    }
}
