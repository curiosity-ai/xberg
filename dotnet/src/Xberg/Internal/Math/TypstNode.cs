// Derived from `typst-syntax` 0.15.1 (Copyright The Typst Project Developers,
// https://github.com/typst/typst), licensed under the Apache License 2.0. This file is a
// modified translation of that crate's syntax tree into C#; see ../../../../THIRD_PARTY_NOTICES.md
// and ../../../../third_party/LICENSE-Apache-2.0.txt.
//
// Modified: spans, incremental reparsing, hints and the numbering machinery are dropped. The math
// converter only ever walks the tree the parser just built, so a node needs its kind, its text if
// it is a leaf, and its children.
using System.Collections.Generic;
using System.Text;

namespace Xberg.Internal.MathMarkup;

/// <summary>A node in the Typst syntax tree: a leaf, an error, or an inner node.</summary>
internal sealed class TypstNode
{
    private TypstKind _kind;
    private string _text;
    private List<TypstNode>? _children;

    private TypstNode(TypstKind kind, string text, List<TypstNode>? children)
    {
        _kind = kind;
        _text = text;
        _children = children;
    }

    public static TypstNode Leaf(TypstKind kind, string text) => new(kind, text, null);

    /// <summary>An error node carries the offending text, which the converter still renders.</summary>
    public static TypstNode Error(string text) => new(TypstKind.Error, text, null);

    public static TypstNode Inner(TypstKind kind, List<TypstNode> children) => new(kind, "", children);

    public TypstKind Kind => _kind;

    /// <summary>The text of a leaf or error node; the empty string for an inner node.</summary>
    public string LeafText => _children is null ? _text : "";

    public IReadOnlyList<TypstNode> Children => (IReadOnlyList<TypstNode>?)_children ?? System.Array.Empty<TypstNode>();

    public bool IsLeaf => _children is null;

    /// <summary>Re-label a node in place, as the parser does when a delimiter turns out to be text.</summary>
    public void ConvertToKind(TypstKind kind) => _kind = kind;

    /// <summary>
    /// Collapse a node into an error leaf carrying the text it covered, which is what the parser
    /// does when a construct turns out not to parse.
    /// </summary>
    public void ConvertToError()
    {
        if (_kind == TypstKind.Error) return;
        _text = FullText;
        _children = null;
        _kind = TypstKind.Error;
    }

    /// <summary>The source text this node covers, rebuilt from its leaves.</summary>
    public string FullText
    {
        get
        {
            if (_children is null) return _text;
            var sb = new StringBuilder();
            Collect(this, sb);
            return sb.ToString();

            static void Collect(TypstNode node, StringBuilder sb)
            {
                if (node._children is null) { sb.Append(node._text); return; }
                foreach (var child in node._children) Collect(child, sb);
            }
        }
    }
}
