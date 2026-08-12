namespace Xberg.Internal.Commonmark;

/// <summary>
/// The subset of comrak's <c>NodeValue</c> variants produced by our AST builder
/// (<see cref="ComrakBridge"/>) plus a handful required by the formatters. Ported
/// from <c>comrak-0.53.0/src/nodes.rs</c>.
/// </summary>
public enum NodeType
{
    Document,
    BlockQuote,
    List,
    Item,
    TaskItem,
    Heading,
    CodeBlock,
    ThematicBreak,
    Paragraph,
    Text,
    LineBreak,
    SoftBreak,
    Code,
    Raw,
    Strong,
    Emph,
    Strikethrough,
    Highlight,
    Underline,
    Subscript,
    Superscript,
    Link,
    Image,
    Table,
    TableRow,
    TableCell,
    FootnoteDefinition,
    FootnoteReference,
    Math,
    Alert,
}

public enum ListType { Bullet, Ordered }

public enum ListDelimType { Period, Paren }

public enum TableAlignment { None, Left, Center, Right }

public enum AlertType { Note, Tip, Important, Warning, Caution }

public static class AlertTypeExtensions
{
    public static string DefaultTitle(this AlertType t) => t switch
    {
        AlertType.Note => "Note",
        AlertType.Tip => "Tip",
        AlertType.Important => "Important",
        AlertType.Warning => "Warning",
        AlertType.Caution => "Caution",
        _ => "Note",
    };

    public static string CssClass(this AlertType t) => t switch
    {
        AlertType.Note => "markdown-alert-note",
        AlertType.Tip => "markdown-alert-tip",
        AlertType.Important => "markdown-alert-important",
        AlertType.Warning => "markdown-alert-warning",
        AlertType.Caution => "markdown-alert-caution",
        _ => "markdown-alert-note",
    };
}

public struct NodeList
{
    public ListType ListType;
    public byte BulletChar;
    public int Start;
    public ListDelimType Delimiter;
    public bool Tight;
    public bool IsTaskList;
}

public struct NodeHeading
{
    public byte Level;
    public bool Setext;
    public bool Closed;
}

public struct NodeCodeBlock
{
    public bool Fenced;
    public byte FenceChar;
    public int FenceLength;
    public int FenceOffset;
    public string Info;
    public string Literal;
    public bool Closed;
}

public struct NodeCode
{
    public int NumBackticks;
    public string Literal;
}

public struct NodeLink
{
    public string Url;
    public string Title;
}

public struct NodeTable
{
    public List<TableAlignment> Alignments;
    public int NumColumns;
    public int NumRows;
    public int NumNonemptyCells;
}

public struct NodeMath
{
    public bool DollarMath;
    public bool DisplayMath;
    public string Literal;
}

public struct NodeFootnoteDefinition
{
    public string Name;
    public int TotalReferences;
}

public struct NodeFootnoteReference
{
    public string Name;
    public int RefNum;
    public int Ix;
}

public struct NodeAlert
{
    public AlertType AlertType;
    public string? Title;
    public bool Multiline;
    public int FenceLength;
    public int FenceOffset;
}

/// <summary>
/// A single AST node. Mirrors comrak's arena-allocated <c>AstNode</c> with sibling/parent
/// links so the formatters can walk the tree the same way (next/previous sibling,
/// first/last child, reverse children).
/// </summary>
public sealed class MdNode
{
    public NodeType Type;

    // Payloads (only the field matching Type is meaningful).
    public string Literal = "";           // Text, Raw
    public NodeList List;                 // List, Item, TaskItem
    public NodeHeading Heading;
    public NodeCodeBlock CodeBlock;
    public NodeCode Code;
    public NodeLink Link;                 // Link, Image
    public NodeTable Table;
    public bool TableRowHeader;           // TableRow
    public NodeMath Math;
    public NodeFootnoteDefinition FootnoteDefinition;
    public NodeFootnoteReference FootnoteReference;
    public NodeAlert Alert;
    public char? TaskSymbol;              // TaskItem

    public MdNode? Parent;
    public MdNode? FirstChild;
    public MdNode? LastChild;
    public MdNode? Prev;
    public MdNode? Next;

    public MdNode(NodeType type) => Type = type;

    public void Append(MdNode child)
    {
        child.Detach();
        child.Parent = this;
        if (LastChild is not null)
        {
            LastChild.Next = child;
            child.Prev = LastChild;
            LastChild = child;
        }
        else
        {
            FirstChild = child;
            LastChild = child;
        }
    }

    private void Detach()
    {
        if (Prev is not null) Prev.Next = Next;
        else if (Parent is not null) Parent.FirstChild = Next;
        if (Next is not null) Next.Prev = Prev;
        else if (Parent is not null) Parent.LastChild = Prev;
        Parent = null;
        Prev = null;
        Next = null;
    }

    public IEnumerable<MdNode> Children()
    {
        for (var c = FirstChild; c is not null; c = c.Next) yield return c;
    }

    public IEnumerable<MdNode> ReverseChildren()
    {
        for (var c = LastChild; c is not null; c = c.Prev) yield return c;
    }

    /// <summary>Ported from comrak <c>NodeValue::block</c>.</summary>
    public bool IsBlock => Type switch
    {
        NodeType.Document or NodeType.BlockQuote or NodeType.FootnoteDefinition
            or NodeType.List or NodeType.Item or NodeType.CodeBlock or NodeType.Paragraph
            or NodeType.Heading or NodeType.ThematicBreak or NodeType.Table
            or NodeType.TableRow or NodeType.TableCell or NodeType.TaskItem
            or NodeType.Alert => true,
        _ => false,
    };

    /// <summary>Ported from comrak <c>containing_block</c>.</summary>
    public MdNode? ContainingBlock()
    {
        for (var n = this; n is not null; n = n.Parent)
            if (n.IsBlock) return n;
        return null;
    }
}
