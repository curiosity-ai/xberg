//! Region kinds shared by layout extraction and image captioning.

/// Classification of a detected layout region that warrants VLM extraction.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum RegionKind {
    /// A figure, diagram, chart, or image region.
    Figure,
    /// A densely formatted or complex table.
    DenseTable,
    /// A region with complex or mixed layout.
    ComplexLayout,
    /// A standalone image to caption.
    Caption,
}

impl RegionKind {
    /// Return the default prompt template for this region kind.
    pub fn default_prompt(self) -> &'static str {
        match self {
            Self::Figure => REGION_FIGURE_TEMPLATE,
            Self::DenseTable => REGION_DENSE_TABLE_TEMPLATE,
            Self::ComplexLayout => REGION_COMPLEX_LAYOUT_TEMPLATE,
            Self::Caption => REGION_CAPTION_TEMPLATE,
        }
    }
}

impl TryFrom<&str> for RegionKind {
    type Error = crate::XbergError;

    fn try_from(value: &str) -> Result<Self, Self::Error> {
        match value {
            "Figure" => Ok(Self::Figure),
            "DenseTable" => Ok(Self::DenseTable),
            "ComplexLayout" => Ok(Self::ComplexLayout),
            "Caption" => Ok(Self::Caption),
            _ => Err(crate::XbergError::validation(format!(
                "invalid RegionKind value `{value}`; expected one of: Figure, DenseTable, ComplexLayout, Caption"
            ))),
        }
    }
}

impl std::str::FromStr for RegionKind {
    type Err = crate::XbergError;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        Self::try_from(value)
    }
}

const REGION_FIGURE_TEMPLATE: &str = "\
Describe this figure or diagram in detail. Include:
- The type of figure (chart, graph, diagram, photo, illustration, etc.)
- All text visible in the figure (labels, titles, legends, axis names, annotations)
- The key data or relationships the figure conveys
- Any embedded numeric values, percentages, or measurements

Return the description as concise markdown. Do not add headings — return only \
a paragraph or a short bulleted list if appropriate. If the figure contains no \
meaningful content, return an empty string.";

const REGION_DENSE_TABLE_TEMPLATE: &str = "\
Extract the table from this image as GitHub-Flavoured Markdown.
- Preserve all columns and rows exactly as they appear.
- Use `|` column separators and a `---` separator row after the header.
- If the table has no visible header, create a row of empty header cells.
- Do not add explanatory text — return only the Markdown table.
- If the image does not contain a table, return an empty string.";

const REGION_COMPLEX_LAYOUT_TEMPLATE: &str = "\
Extract all text and structured content from this image region as Markdown.
- Preserve the original reading order (top to bottom, left to right).
- Use appropriate Markdown elements: paragraphs, lists, code blocks, tables.
- Do not add commentary or explanations beyond what the image contains.
- If the region contains no meaningful text, return an empty string.";

const REGION_CAPTION_TEMPLATE: &str = "\
Write a concise, factual caption for this image suitable for use as alt text \
or a search-index entry.
- One or two sentences at most.
- Describe what is visible: subject, action, setting, notable text.
- Do not speculate about intent, mood, or context that is not visible.
- Do not start the caption with phrases like \"This image shows\" or \
\"A picture of\" — lead with the subject.
- If the image has no recognisable content, return an empty string.";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_prompts_are_available_without_an_llm_runtime() {
        for kind in [
            RegionKind::Figure,
            RegionKind::DenseTable,
            RegionKind::ComplexLayout,
            RegionKind::Caption,
        ] {
            assert!(!kind.default_prompt().is_empty());
        }
    }

    #[test]
    fn region_kind_parsing_accepts_every_wire_value() {
        for (value, expected) in [
            ("Figure", RegionKind::Figure),
            ("DenseTable", RegionKind::DenseTable),
            ("ComplexLayout", RegionKind::ComplexLayout),
            ("Caption", RegionKind::Caption),
        ] {
            assert_eq!(
                value.parse::<RegionKind>().expect("known region kinds must parse"),
                expected
            );
        }
    }

    #[test]
    fn region_kind_parsing_rejects_unknown_values() {
        let error = "Photograph"
            .parse::<RegionKind>()
            .expect_err("unknown region kinds must be rejected");

        assert_eq!(
            error.to_string(),
            "Validation error: invalid RegionKind value `Photograph`; expected one of: Figure, DenseTable, ComplexLayout, Caption"
        );
    }

    #[test]
    fn region_kind_deserialization_rejects_unknown_values() {
        let error = serde_json::from_str::<RegionKind>(r#""Photograph""#)
            .expect_err("unknown region kinds must not cross the JSON boundary");

        assert!(error.to_string().contains("unknown variant `Photograph`"));
    }
}
