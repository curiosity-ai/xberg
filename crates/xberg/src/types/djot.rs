//! Djot document types.
//!
//! This module defines types for representing Djot document structures.

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::collections::HashMap;

use super::Table;
use super::metadata::KeyValueAttribute;
use super::metadata::Metadata;

/// Attributes associated with a named Djot element.
#[derive(Debug, Clone, Default)]
pub struct DjotAttributeGroup {
    /// Element identifier used by the Djot attribute map.
    pub identifier: String,
    /// Attributes associated with the element.
    pub attributes: Attributes,
}

#[derive(Deserialize)]
#[serde(untagged)]
enum DjotAttributeGroupWire {
    Positional((String, Attributes)),
    Named { identifier: String, attributes: Attributes },
}

impl Serialize for DjotAttributeGroup {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        (&self.identifier, &self.attributes).serialize(serializer)
    }
}

impl<'de> Deserialize<'de> for DjotAttributeGroup {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        Ok(match DjotAttributeGroupWire::deserialize(deserializer)? {
            DjotAttributeGroupWire::Positional(group) => group.into(),
            DjotAttributeGroupWire::Named { identifier, attributes } => Self { identifier, attributes },
        })
    }
}

#[cfg(feature = "api")]
impl utoipa::PartialSchema for DjotAttributeGroup {
    fn schema() -> utoipa::openapi::RefOr<utoipa::openapi::schema::Schema> {
        use utoipa::openapi::schema::{ArrayBuilder, ArrayItems, Object, Schema, Type};

        ArrayBuilder::new()
            .items(ArrayItems::False)
            .prefix_items([
                Schema::Object(Object::with_type(Type::String)),
                <Attributes as utoipa::PartialSchema>::schema().into(),
            ])
            .min_items(Some(2))
            .max_items(Some(2))
            .into()
    }
}

#[cfg(feature = "api")]
impl utoipa::ToSchema for DjotAttributeGroup {
    fn schemas(schemas: &mut Vec<(String, utoipa::openapi::RefOr<utoipa::openapi::schema::Schema>)>) {
        <Attributes as utoipa::ToSchema>::schemas(schemas);
    }
}

impl From<(String, Attributes)> for DjotAttributeGroup {
    fn from((identifier, attributes): (String, Attributes)) -> Self {
        Self { identifier, attributes }
    }
}

impl From<DjotAttributeGroup> for (String, Attributes) {
    fn from(group: DjotAttributeGroup) -> Self {
        (group.identifier, group.attributes)
    }
}

/// Comprehensive Djot document structure with semantic preservation.
///
/// This type captures the full richness of Djot markup, including:
/// - Block-level structures (headings, lists, blockquotes, code blocks, etc.)
/// - Inline formatting (emphasis, strong, highlight, subscript, superscript, etc.)
/// - Attributes (classes, IDs, key-value pairs)
/// - Links, images, footnotes
/// - Math expressions (inline and display)
/// - Tables with full structure
///
/// Available when the `djot` feature is enabled.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
#[cfg_attr(feature = "api", schema(no_recursion))]
pub struct DjotContent {
    /// Plain text representation for backwards compatibility
    pub plain_text: String,

    /// Structured block-level content
    pub blocks: Vec<FormattedBlock>,

    /// Metadata from YAML frontmatter
    pub metadata: Metadata,

    /// Extracted tables as structured data
    pub tables: Vec<Table>,

    /// Extracted images with metadata
    pub images: Vec<DjotImage>,

    /// Extracted links with URLs
    pub links: Vec<DjotLink>,

    /// Footnote definitions
    pub footnotes: Vec<Footnote>,

    /// Attributes mapped by element identifier (if present)
    #[serde(skip_serializing_if = "Vec::is_empty", default)]
    pub attributes: Vec<DjotAttributeGroup>,
}

/// Block-level element in a Djot document.
///
/// Represents structural elements like headings, paragraphs, lists, code blocks, etc.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
#[cfg_attr(feature = "api", schema(no_recursion))]
pub struct FormattedBlock {
    /// Type of block element
    pub block_type: BlockType,

    /// Heading level (1-6) for headings, or nesting level for lists
    #[serde(skip_serializing_if = "Option::is_none")]
    pub level: Option<usize>,

    /// Inline content within the block
    pub inline_content: Vec<InlineElement>,

    /// Element attributes (classes, IDs, key-value pairs)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub attributes: Option<Attributes>,

    /// Language identifier for code blocks
    #[serde(skip_serializing_if = "Option::is_none")]
    pub language: Option<String>,

    /// Raw code content for code blocks
    #[serde(skip_serializing_if = "Option::is_none")]
    pub code: Option<String>,

    /// Nested blocks for containers (blockquotes, list items, divs)
    #[serde(skip_serializing_if = "Vec::is_empty", default)]
    pub children: Vec<FormattedBlock>,
}

/// Types of block-level elements in Djot.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
pub enum BlockType {
    /// Standard prose paragraph.
    Paragraph,
    /// Section heading (level stored in `FormattedBlock::level`).
    Heading,
    /// Block quotation container.
    Blockquote,
    /// Fenced or indented code block.
    CodeBlock,
    /// Individual item within a list.
    ListItem,
    /// Numbered (ordered) list container.
    OrderedList,
    /// Unnumbered (bullet) list container.
    BulletList,
    /// Task / checkbox list container.
    TaskList,
    /// Definition list container.
    DefinitionList,
    /// Term part of a definition list entry.
    DefinitionTerm,
    /// Description / definition part of a definition list entry.
    DefinitionDescription,
    /// Generic `div` container with optional attributes.
    Div,
    /// Logical section container, often associated with a heading.
    Section,
    /// Horizontal rule / thematic break.
    ThematicBreak,
    /// Raw content block in a specified format (e.g. HTML, LaTeX).
    RawBlock,
    /// Display-mode mathematical expression.
    MathDisplay,
}

/// Inline element within a block.
///
/// Represents text with formatting, links, images, etc.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
pub struct InlineElement {
    /// Type of inline element
    pub element_type: InlineType,

    /// Text content
    pub content: String,

    /// Element attributes
    #[serde(skip_serializing_if = "Option::is_none")]
    pub attributes: Option<Attributes>,

    /// Additional metadata (e.g., href for links, src/alt for images)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub metadata: Option<HashMap<String, String>>,
}

/// Types of inline elements in Djot.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
pub enum InlineType {
    /// Plain text run.
    Text,
    /// Bold / strong emphasis.
    Strong,
    /// Italic / regular emphasis.
    Emphasis,
    /// Highlighted text (marker pen).
    Highlight,
    /// Subscript text.
    Subscript,
    /// Superscript text.
    Superscript,
    /// Inserted text (tracked change).
    Insert,
    /// Deleted text (tracked change).
    Delete,
    /// Inline code span.
    Code,
    /// Hyperlink with URL.
    Link,
    /// Inline image reference.
    Image,
    /// Generic inline span with optional attributes.
    Span,
    /// Inline mathematical expression.
    Math,
    /// Raw inline content in a specified format.
    RawInline,
    /// Footnote reference marker.
    FootnoteRef,
    /// Named symbol or emoji shortcode.
    Symbol,
}

/// Element attributes in Djot.
///
/// Represents the attributes attached to elements using {.class #id key="value"} syntax.
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
pub struct Attributes {
    /// Element ID (#identifier)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,

    /// CSS classes (.class1 .class2)
    #[serde(skip_serializing_if = "Vec::is_empty", default)]
    pub classes: Vec<String>,

    /// Key-value pairs (key="value")
    #[serde(skip_serializing_if = "Vec::is_empty", default)]
    pub key_values: Vec<KeyValueAttribute>,
}

/// Image element in Djot.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
pub struct DjotImage {
    /// Image source URL or path
    pub src: String,

    /// Alternative text
    pub alt: String,

    /// Optional title
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,

    /// Element attributes
    #[serde(skip_serializing_if = "Option::is_none")]
    pub attributes: Option<Attributes>,
}

/// Link element in Djot.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
pub struct DjotLink {
    /// Link URL
    pub url: String,

    /// Link text content
    pub text: String,

    /// Optional title
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,

    /// Element attributes
    #[serde(skip_serializing_if = "Option::is_none")]
    pub attributes: Option<Attributes>,
}

/// Footnote in Djot.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[cfg_attr(feature = "api", derive(utoipa::ToSchema))]
pub struct Footnote {
    /// Footnote label
    pub label: String,

    /// Footnote content blocks
    pub content: Vec<FormattedBlock>,
}

#[cfg(test)]
mod binding_value_serde_tests {
    use super::{Attributes, DjotAttributeGroup};
    use serde_json::json;

    #[cfg(feature = "api")]
    #[test]
    fn should_describe_djot_attribute_group_as_legacy_array_schema() {
        let schema = serde_json::to_value(<DjotAttributeGroup as utoipa::PartialSchema>::schema())
            .expect("schema must serialize");
        assert_eq!(schema["type"], "array");
        assert_eq!(schema["minItems"], 2);
        assert_eq!(schema["maxItems"], 2);
        assert_eq!(schema["items"], false);
        assert_eq!(schema["prefixItems"].as_array().map(Vec::len), Some(2));
    }

    #[test]
    fn should_preserve_legacy_djot_attribute_tuple_wire_format() {
        let legacy = json!(["section", {
            "id": "intro",
            "classes": ["lead"],
            "key_values": [["role", "doc-introduction"]]
        }]);
        let group: DjotAttributeGroup =
            serde_json::from_value(legacy.clone()).expect("legacy attribute group must deserialize");
        let named: DjotAttributeGroup = serde_json::from_value(json!({
            "identifier": "section",
            "attributes": {
                "id": "intro",
                "classes": ["lead"],
                "key_values": [{"key": "role", "value": "doc-introduction"}]
            }
        }))
        .expect("named attribute group must deserialize");

        assert_eq!(group.identifier, "section");
        assert_eq!(group.attributes.id.as_deref(), Some("intro"));
        assert_eq!(group.attributes.key_values[0].key, "role");
        assert_eq!(named.identifier, group.identifier);
        assert_eq!(named.attributes.id, group.attributes.id);
        assert_eq!(named.attributes.classes, group.attributes.classes);
        assert_eq!(named.attributes.key_values, group.attributes.key_values);
        assert_eq!(
            serde_json::to_value(group).expect("attribute group must serialize"),
            legacy
        );
        assert_eq!(
            serde_json::to_value(named).expect("named attribute group must serialize"),
            legacy
        );
    }

    #[test]
    fn should_preserve_legacy_attributes_object_wire_format() {
        let legacy = json!({"key_values": [["lang", "en"]]});
        let attributes: Attributes =
            serde_json::from_value(legacy.clone()).expect("legacy attributes must deserialize");

        assert_eq!(attributes.key_values[0].value, "en");
        assert_eq!(
            serde_json::to_value(attributes).expect("attributes must serialize"),
            legacy
        );
    }
}
