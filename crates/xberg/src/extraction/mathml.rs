//! Presentation MathML to LaTeX converter.
//!
//! Converts `<math>` (MathML) subtrees found in ODT/ODP embedded formula objects
//! and EPUB XHTML content to LaTeX notation. Modeled on the OMML converter at
//! `docx::math`: the subtree is collected into an `MmlNode` tree, then recursively
//! rendered to LaTeX. Unknown/unhandled elements degrade to their text content
//! instead of failing the whole document.
//!
//! Unlike OMML (which streams off a `quick_xml::Reader` while parsing DOCX part
//! XML), callers here already hold a parsed `roxmltree::Document` — ODT parses an
//! embedded object's `content.xml` on its own, and EPUB walks XHTML with
//! `roxmltree` already. So the converter operates directly on a `roxmltree::Node`,
//! with a `&str`-in convenience wrapper for callers that only have raw XML text.

use crate::extraction::math_symbols::render_run_text;
use crate::extractors::security::{SecurityBudget, SecurityError};
use roxmltree::Node;

/// Names of MathML elements that hold no rendered content and whose text (an
/// alternate-encoding annotation, e.g. `StarMath` or content-MathML) must never
/// leak into the LaTeX output.
const ANNOTATION_ELEMENTS: &[&str] = &["annotation", "annotation-xml"];

/// Names of MathML elements that are pure grouping/styling wrappers: their
/// children are rendered in sequence with no LaTeX markup of their own.
const TRANSPARENT_ELEMENTS: &[&str] = &["math", "mrow", "mstyle", "mpadded", "merror"];

#[cfg_attr(alef, alef(skip))]
#[derive(Debug, Clone)]
enum MmlNode {
    /// Plain text from `mi`/`mn`/`mo`/`ms`.
    Run(String),
    /// Literal text from `mtext`: rendered as `\text{...}`.
    Text(String),
    /// A single blank space from `mspace`.
    Space,
    /// Fraction: `\frac{num}{den}`.
    Frac { num: Box<MmlNode>, den: Box<MmlNode> },
    /// Superscript: `base^{sup}`.
    Sup { base: Box<MmlNode>, sup: Box<MmlNode> },
    /// Subscript: `base_{sub}`.
    Sub { base: Box<MmlNode>, sub: Box<MmlNode> },
    /// Sub-superscript: `base_{sub}^{sup}`.
    SubSup {
        base: Box<MmlNode>,
        sub: Box<MmlNode>,
        sup: Box<MmlNode>,
    },
    /// Square root: `\sqrt{body}`.
    Sqrt { body: Box<MmlNode> },
    /// N-th root: `\sqrt[index]{body}`.
    Root { body: Box<MmlNode>, index: Box<MmlNode> },
    /// Fenced group: `\left<open> a, b, ...\right<close>`.
    Fenced {
        open: String,
        close: String,
        sep: String,
        elements: Vec<MmlNode>,
    },
    /// Underscript: `\underset{under}{base}`.
    Under { base: Box<MmlNode>, under: Box<MmlNode> },
    /// Overscript: `\overset{over}{base}`.
    Over { base: Box<MmlNode>, over: Box<MmlNode> },
    /// Under+overscript: `\overset{over}{\underset{under}{base}}`.
    UnderOver {
        base: Box<MmlNode>,
        under: Box<MmlNode>,
        over: Box<MmlNode>,
    },
    /// Phantom (invisible but space-occupying): `\phantom{body}`.
    Phantom { body: Box<MmlNode> },
    /// Table: `\begin{matrix}...\end{matrix}`.
    Table { rows: Vec<Vec<MmlNode>> },
    /// Grouping container (`math`, `mrow`, `semantics` presentation branch,
    /// unknown elements) — renders its children in sequence.
    Group { children: Vec<MmlNode> },
}

/// Convert a MathML XML fragment (a full document whose root is `<math>`, or
/// `<math>` nested anywhere in the fragment) to LaTeX.
///
/// Used by callers (e.g. ODT's embedded-object formula extraction) that only
/// have the raw XML text of a formula and have not already parsed it.
pub(crate) fn convert_mathml_str_to_latex(xml: &str, budget: &mut SecurityBudget) -> Result<String, SecurityError> {
    let Ok(doc) = roxmltree::Document::parse(xml) else {
        return Ok(String::new());
    };

    let root = doc.root_element();
    let math_node = if root.tag_name().name().eq_ignore_ascii_case("math") {
        root
    } else {
        match root
            .descendants()
            .find(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("math"))
        {
            Some(node) => node,
            None => root,
        }
    };

    convert_mathml_node_to_latex(math_node, budget)
}

/// Convert an already-parsed MathML `<math>` (or presentation) node to LaTeX.
///
/// Used by callers (e.g. the EPUB XHTML walker) that already hold a
/// `roxmltree::Node` positioned at the `<math>` element.
pub(crate) fn convert_mathml_node_to_latex(node: Node, budget: &mut SecurityBudget) -> Result<String, SecurityError> {
    let collected = collect_node(node, budget)?;
    let mut out = String::new();
    render_node(&collected, &mut out);
    Ok(out)
}

/// Collect an element's children into a sequence of `MmlNode`s, dispatching each
/// child element through [`collect_node`] and each direct text node into a
/// [`MmlNode::Run`]. Whitespace-only text nodes are dropped.
fn collect_children(parent: Node, budget: &mut SecurityBudget) -> Result<Vec<MmlNode>, SecurityError> {
    let mut nodes = Vec::new();
    for child in parent.children() {
        budget.step()?;
        if child.is_element() {
            nodes.push(collect_node(child, budget)?);
        } else if child.is_text() {
            let text = child.text().unwrap_or("");
            if !text.trim().is_empty() {
                budget.check_entity(text)?;
                budget.account_text(text.len())?;
                nodes.push(MmlNode::Run(text.to_string()));
            }
        }
    }
    Ok(nodes)
}

/// Collect the Nth element child of `parent` (skipping non-element nodes) into a
/// single `MmlNode`, or an empty `Group` if fewer than `index + 1` element
/// children exist.
fn collect_nth_child(parent: Node, index: usize, budget: &mut SecurityBudget) -> Result<MmlNode, SecurityError> {
    match parent.children().filter(|c| c.is_element()).nth(index) {
        Some(child) => collect_node(child, budget),
        None => Ok(MmlNode::Group { children: Vec::new() }),
    }
}

/// Collect a single MathML element into an `MmlNode`, dispatching on tag name.
fn collect_node(node: Node, budget: &mut SecurityBudget) -> Result<MmlNode, SecurityError> {
    budget.step()?;
    budget.enter()?;
    let result = collect_node_inner(node, budget);
    budget.leave();
    result
}

fn collect_node_inner(node: Node, budget: &mut SecurityBudget) -> Result<MmlNode, SecurityError> {
    let tag = node.tag_name().name();

    if ANNOTATION_ELEMENTS.iter().any(|&s| s.eq_ignore_ascii_case(tag)) {
        return Ok(MmlNode::Group { children: Vec::new() });
    }

    match tag.to_ascii_lowercase().as_str() {
        "mi" | "mn" | "ms" | "mo" => Ok(MmlNode::Run(collect_text(node, budget)?)),
        "mtext" => Ok(MmlNode::Text(collect_text(node, budget)?)),
        "mspace" => Ok(MmlNode::Space),
        "semantics" => {
            let children = node
                .children()
                .filter(|c| {
                    c.is_element()
                        && !ANNOTATION_ELEMENTS
                            .iter()
                            .any(|&s| s.eq_ignore_ascii_case(c.tag_name().name()))
                })
                .map(|c| collect_node(c, budget))
                .collect::<Result<Vec<_>, _>>()?;
            Ok(MmlNode::Group { children })
        }
        t if TRANSPARENT_ELEMENTS.contains(&t) => Ok(MmlNode::Group {
            children: collect_children(node, budget)?,
        }),
        "mfrac" => Ok(MmlNode::Frac {
            num: Box::new(collect_nth_child(node, 0, budget)?),
            den: Box::new(collect_nth_child(node, 1, budget)?),
        }),
        "msup" => Ok(MmlNode::Sup {
            base: Box::new(collect_nth_child(node, 0, budget)?),
            sup: Box::new(collect_nth_child(node, 1, budget)?),
        }),
        "msub" => Ok(MmlNode::Sub {
            base: Box::new(collect_nth_child(node, 0, budget)?),
            sub: Box::new(collect_nth_child(node, 1, budget)?),
        }),
        "msubsup" => Ok(MmlNode::SubSup {
            base: Box::new(collect_nth_child(node, 0, budget)?),
            sub: Box::new(collect_nth_child(node, 1, budget)?),
            sup: Box::new(collect_nth_child(node, 2, budget)?),
        }),
        "msqrt" => Ok(MmlNode::Sqrt {
            body: Box::new(MmlNode::Group {
                children: collect_children(node, budget)?,
            }),
        }),
        "mroot" => Ok(MmlNode::Root {
            body: Box::new(collect_nth_child(node, 0, budget)?),
            index: Box::new(collect_nth_child(node, 1, budget)?),
        }),
        "mfenced" => collect_fenced(node, budget),
        "munder" => Ok(MmlNode::Under {
            base: Box::new(collect_nth_child(node, 0, budget)?),
            under: Box::new(collect_nth_child(node, 1, budget)?),
        }),
        "mover" => Ok(MmlNode::Over {
            base: Box::new(collect_nth_child(node, 0, budget)?),
            over: Box::new(collect_nth_child(node, 1, budget)?),
        }),
        "munderover" => Ok(MmlNode::UnderOver {
            base: Box::new(collect_nth_child(node, 0, budget)?),
            under: Box::new(collect_nth_child(node, 1, budget)?),
            over: Box::new(collect_nth_child(node, 2, budget)?),
        }),
        "mphantom" => Ok(MmlNode::Phantom {
            body: Box::new(MmlNode::Group {
                children: collect_children(node, budget)?,
            }),
        }),
        "mtable" => collect_table(node, budget),
        _ => Ok(MmlNode::Group {
            children: collect_children(node, budget)?,
        }),
    }
}

/// Collect the direct text content of a leaf element (`mi`/`mn`/`mo`/`ms`/`mtext`).
fn collect_text(node: Node, budget: &mut SecurityBudget) -> Result<String, SecurityError> {
    let mut text = String::new();
    // Only real text nodes — `Node::text()` also returns content for comment
    // nodes, and MathML fixtures commonly annotate entities with a comment
    // (e.g. `<mo>&#x222B;<!-- ∫ --></mo>`) that must not be double-counted. ~keep
    for child in node.children().filter(|c| c.is_text()) {
        if let Some(t) = child.text() {
            budget.check_entity(t)?;
            budget.account_text(t.len())?;
            text.push_str(t);
        }
    }
    Ok(text)
}

/// Collect an `mfenced` element: `open`/`close`/`separators` attributes plus
/// one element per fenced argument.
fn collect_fenced(node: Node, budget: &mut SecurityBudget) -> Result<MmlNode, SecurityError> {
    let open = node.attribute("open").unwrap_or("(").to_string();
    let close = node.attribute("close").unwrap_or(")").to_string();
    let sep = node
        .attribute("separators")
        .and_then(|s| s.chars().next())
        .unwrap_or(',')
        .to_string();

    let elements = node
        .children()
        .filter(|c| c.is_element())
        .map(|c| collect_node(c, budget))
        .collect::<Result<Vec<_>, _>>()?;

    Ok(MmlNode::Fenced {
        open,
        close,
        sep,
        elements,
    })
}

/// Collect an `mtable` element into rows of cells (`mtr` > `mtd`).
fn collect_table(node: Node, budget: &mut SecurityBudget) -> Result<MmlNode, SecurityError> {
    let mut rows = Vec::new();
    for row in node
        .children()
        .filter(|c| c.is_element() && c.tag_name().name().eq_ignore_ascii_case("mtr"))
    {
        budget.step()?;
        let cells = row
            .children()
            .filter(|c| c.is_element() && c.tag_name().name().eq_ignore_ascii_case("mtd"))
            .map(|c| {
                Ok(MmlNode::Group {
                    children: collect_children(c, budget)?,
                })
            })
            .collect::<Result<Vec<_>, SecurityError>>()?;
        rows.push(cells);
    }
    Ok(MmlNode::Table { rows })
}

/// Render a slice of `MmlNode`s to LaTeX, concatenated with no separators.
fn render_nodes(nodes: &[MmlNode]) -> String {
    let mut out = String::new();
    for node in nodes {
        render_node(node, &mut out);
    }
    out
}

/// Render a single `MmlNode` to LaTeX, appending to `out`.
fn render_node(node: &MmlNode, out: &mut String) {
    match node {
        MmlNode::Run(text) => render_run_text(text, out),
        MmlNode::Text(text) => {
            out.push_str("\\text{");
            render_run_text(text, out);
            out.push('}');
        }
        MmlNode::Space => out.push(' '),
        MmlNode::Frac { num, den } => {
            out.push_str("\\frac{");
            render_node(num, out);
            out.push_str("}{");
            render_node(den, out);
            out.push('}');
        }
        MmlNode::Sup { base, sup } => {
            render_arg(base, out);
            out.push_str("^{");
            render_node(sup, out);
            out.push('}');
        }
        MmlNode::Sub { base, sub } => {
            render_arg(base, out);
            out.push_str("_{");
            render_node(sub, out);
            out.push('}');
        }
        MmlNode::SubSup { base, sub, sup } => {
            render_arg(base, out);
            out.push_str("_{");
            render_node(sub, out);
            out.push_str("}^{");
            render_node(sup, out);
            out.push('}');
        }
        MmlNode::Sqrt { body } => {
            out.push_str("\\sqrt{");
            render_node(body, out);
            out.push('}');
        }
        MmlNode::Root { body, index } => {
            out.push_str("\\sqrt[");
            render_node(index, out);
            out.push_str("]{");
            render_node(body, out);
            out.push('}');
        }
        MmlNode::Fenced {
            open,
            close,
            sep,
            elements,
        } => {
            out.push_str("\\left");
            out.push_str(&fence_chr_to_latex(open));
            for (i, elem) in elements.iter().enumerate() {
                if i > 0 {
                    out.push_str(sep);
                }
                render_node(elem, out);
            }
            out.push_str("\\right");
            out.push_str(&fence_chr_to_latex(close));
        }
        MmlNode::Under { base, under } => {
            out.push_str("\\underset{");
            render_node(under, out);
            out.push_str("}{");
            render_node(base, out);
            out.push('}');
        }
        MmlNode::Over { base, over } => {
            out.push_str("\\overset{");
            render_node(over, out);
            out.push_str("}{");
            render_node(base, out);
            out.push('}');
        }
        MmlNode::UnderOver { base, under, over } => {
            out.push_str("\\overset{");
            render_node(over, out);
            out.push_str("}{\\underset{");
            render_node(under, out);
            out.push_str("}{");
            render_node(base, out);
            out.push_str("}}");
        }
        MmlNode::Phantom { body } => {
            out.push_str("\\phantom{");
            render_node(body, out);
            out.push('}');
        }
        MmlNode::Table { rows } => {
            out.push_str("\\begin{matrix}");
            for (i, row) in rows.iter().enumerate() {
                if i > 0 {
                    out.push_str(" \\\\ ");
                }
                for (j, cell) in row.iter().enumerate() {
                    if j > 0 {
                        out.push_str(" & ");
                    }
                    render_node(cell, out);
                }
            }
            out.push_str("\\end{matrix}");
        }
        MmlNode::Group { children } => out.push_str(&render_nodes(children)),
    }
}

/// Render an argument (sup/sub base), wrapping in braces if it renders to more
/// than one character and is not already a LaTeX command or brace group.
fn render_arg(node: &MmlNode, out: &mut String) {
    let mut rendered = String::new();
    render_node(node, &mut rendered);
    let needs_braces = rendered.chars().count() > 1 && !rendered.starts_with('\\') && !rendered.starts_with('{');
    if needs_braces {
        out.push('{');
        out.push_str(&rendered);
        out.push('}');
    } else {
        out.push_str(&rendered);
    }
}

/// Map an `mfenced` open/close character to LaTeX.
fn fence_chr_to_latex(chr: &str) -> String {
    match chr {
        "(" | ")" | "[" | "]" => chr.to_string(),
        "{" => "\\{".to_string(),
        "}" => "\\}".to_string(),
        "|" => "|".to_string(),
        "\u{2016}" => "\\|".to_string(),
        "\u{2329}" | "\u{27E8}" => "\\langle".to_string(),
        "\u{232A}" | "\u{27E9}" => "\\rangle".to_string(),
        "\u{230A}" => "\\lfloor".to_string(),
        "\u{230B}" => "\\rfloor".to_string(),
        "\u{2308}" => "\\lceil".to_string(),
        "\u{2309}" => "\\rceil".to_string(),
        "" => ".".to_string(),
        _ => chr.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Helper: parse a MathML XML fragment and return rendered LaTeX.
    fn mathml_to_latex(inner: &str) -> String {
        let xml = format!(r#"<math xmlns="http://www.w3.org/1998/Math/MathML">{}</math>"#, inner);
        let mut budget = SecurityBudget::with_defaults();
        convert_mathml_str_to_latex(&xml, &mut budget).expect("conversion ok")
    }

    #[test]
    fn test_mi_plain_text() {
        assert_eq!(mathml_to_latex("<mi>x</mi>"), "x");
    }

    #[test]
    fn test_mn_number() {
        assert_eq!(mathml_to_latex("<mn>42</mn>"), "42");
    }

    #[test]
    fn test_mo_unicode_operator() {
        assert_eq!(mathml_to_latex("<mo>\u{00D7}</mo>"), "\\times ");
    }

    #[test]
    fn test_numeric_char_ref_with_trailing_comment_is_not_duplicated() {
        // Real-world MathML (e.g. EPUB accessibility test suites) commonly
        // annotates a numeric character reference with a same-content XML
        // comment: `<mo>&#x222B;<!-- ∫ --></mo>`. The comment must not be
        // rendered a second time alongside the decoded entity. ~keep
        assert_eq!(mathml_to_latex("<mo>&#x222B;<!-- \u{222B} --></mo>"), "\\int ");
        assert_eq!(
            mathml_to_latex("<mi mathvariant=\"normal\">&#x221E;<!-- \u{221E} --></mi>"),
            "\\infty "
        );
    }

    #[test]
    fn test_mtext_wraps_in_text_command() {
        assert_eq!(mathml_to_latex("<mtext>hello world</mtext>"), "\\text{hello world}");
    }

    #[test]
    fn test_ms_string_literal() {
        assert_eq!(mathml_to_latex("<ms>abc</ms>"), "abc");
    }

    #[test]
    fn test_mrow_concatenates_children() {
        assert_eq!(mathml_to_latex("<mrow><mi>x</mi><mo>+</mo><mi>y</mi></mrow>"), "x+y");
    }

    #[test]
    fn test_mfrac() {
        assert_eq!(mathml_to_latex("<mfrac><mn>1</mn><mn>2</mn></mfrac>"), "\\frac{1}{2}");
    }

    #[test]
    fn test_msup() {
        assert_eq!(mathml_to_latex("<msup><mi>x</mi><mn>2</mn></msup>"), "x^{2}");
    }

    #[test]
    fn test_msub() {
        assert_eq!(mathml_to_latex("<msub><mi>a</mi><mi>n</mi></msub>"), "a_{n}");
    }

    #[test]
    fn test_msubsup() {
        assert_eq!(
            mathml_to_latex("<msubsup><mi>x</mi><mi>i</mi><mn>2</mn></msubsup>"),
            "x_{i}^{2}"
        );
    }

    #[test]
    fn test_msqrt_no_degree() {
        assert_eq!(mathml_to_latex("<msqrt><mi>x</mi></msqrt>"), "\\sqrt{x}");
    }

    #[test]
    fn test_mroot_with_degree() {
        assert_eq!(mathml_to_latex("<mroot><mi>x</mi><mn>3</mn></mroot>"), "\\sqrt[3]{x}");
    }

    #[test]
    fn test_mfenced_default_parens() {
        assert_eq!(mathml_to_latex("<mfenced><mi>x</mi></mfenced>"), "\\left(x\\right)");
    }

    #[test]
    fn test_mfenced_brackets_multiple_elements() {
        assert_eq!(
            mathml_to_latex(r#"<mfenced open="[" close="]"><mi>a</mi><mi>b</mi></mfenced>"#),
            "\\left[a,b\\right]"
        );
    }

    #[test]
    fn test_munder() {
        assert_eq!(
            mathml_to_latex("<munder><mi>lim</mi><mi>n</mi></munder>"),
            "\\underset{n}{lim}"
        );
    }

    #[test]
    fn test_mover() {
        assert_eq!(
            mathml_to_latex("<mover><mi>x</mi><mo>^</mo></mover>"),
            "\\overset{^}{x}"
        );
    }

    #[test]
    fn test_munderover() {
        assert_eq!(
            mathml_to_latex("<munderover><mo>\u{2211}</mo><mi>i</mi><mi>n</mi></munderover>"),
            "\\overset{n}{\\underset{i}{\\sum }}"
        );
    }

    #[test]
    fn test_mspace_renders_as_space() {
        assert_eq!(mathml_to_latex("<mrow><mi>a</mi><mspace/><mi>b</mi></mrow>"), "a b");
    }

    #[test]
    fn test_mphantom() {
        assert_eq!(mathml_to_latex("<mphantom><mi>x</mi></mphantom>"), "\\phantom{x}");
    }

    #[test]
    fn test_mtable_matrix() {
        let latex = mathml_to_latex(
            r#"<mtable>
                <mtr><mtd><mn>1</mn></mtd><mtd><mn>2</mn></mtd></mtr>
                <mtr><mtd><mn>3</mn></mtd><mtd><mn>4</mn></mtd></mtr>
            </mtable>"#,
        );
        assert_eq!(latex, "\\begin{matrix}1 & 2 \\\\ 3 & 4\\end{matrix}");
    }

    #[test]
    fn test_unknown_element_degrades_to_text_content() {
        assert_eq!(mathml_to_latex("<mlongdiv><mn>42</mn></mlongdiv>"), "42");
    }

    #[test]
    fn test_semantics_renders_presentation_branch_only() {
        let latex = mathml_to_latex(
            r#"<semantics>
                <mrow><mi>E</mi><mo>=</mo><mi>m</mi></mrow>
                <annotation encoding="StarMath 5.0">E = m</annotation>
            </semantics>"#,
        );
        assert_eq!(latex, "E=m");
    }

    #[test]
    fn test_nested_quadratic_formula() {
        // Uses a literal '±' (U+00B1) character, matching how the OMML test
        // suite embeds Unicode math symbols directly rather than as escapes
        // (escape sequences are not processed inside raw strings).
        let latex = mathml_to_latex(
            r#"<mi>x</mi><mo>=</mo>
            <mfrac>
                <mrow>
                    <mo>-</mo><mi>b</mi><mo>±</mo>
                    <msqrt>
                        <msup><mi>b</mi><mn>2</mn></msup>
                        <mo>-</mo><mn>4</mn><mi>a</mi><mi>c</mi>
                    </msqrt>
                </mrow>
                <mrow><mn>2</mn><mi>a</mi></mrow>
            </mfrac>"#,
        );
        assert_eq!(latex, "x=\\frac{-b\\pm \\sqrt{b^{2}-4ac}}{2a}");
    }

    #[test]
    fn test_formula_odt_fixture_shape() {
        // Mirrors the real embedded formula object in test_documents/odt/formula.odt:
        // E = m * c^2, wrapped in <semantics>/<annotation> with a StarMath fallback.
        let xml = r#"<?xml version="1.0" encoding="UTF-8"?>
            <math xmlns="http://www.w3.org/1998/Math/MathML">
                <semantics>
                    <mrow><mrow><mi>E</mi><mo stretchy="false">=</mo>
                    <mrow><mi>m</mi><mo stretchy="false">⋅</mo>
                    <msup><mi>c</mi><mn>2</mn></msup></mrow></mrow></mrow>
                    <annotation encoding="StarMath 5.0">E = m cdot c^2</annotation>
                </semantics>
            </math>"#;
        let mut budget = SecurityBudget::with_defaults();
        let latex = convert_mathml_str_to_latex(xml, &mut budget).expect("conversion ok");
        assert_eq!(latex, "E=m\\cdot c^{2}");
    }

    #[test]
    fn test_convert_mathml_node_to_latex_from_pre_parsed_node() {
        let xml = r#"<math xmlns="http://www.w3.org/1998/Math/MathML"><mi>x</mi></math>"#;
        let doc = roxmltree::Document::parse(xml).expect("parses");
        let mut budget = SecurityBudget::with_defaults();
        let latex = convert_mathml_node_to_latex(doc.root_element(), &mut budget).expect("conversion ok");
        assert_eq!(latex, "x");
    }
}
