mod common;

mod tests {
    use xberg_native_pdf::PdfDocument;
    use xberg_native_pdf::rendering::{ImageFormat, RenderOptions, render_page};

    use crate::common;

    fn create_test_pdf(text: &str) -> Vec<u8> {
        let content = common::text_run_op(text, 72.0, 700.0, "Helvetica", 12.0);
        common::build_pdf_with_standard_fonts(content.as_bytes(), b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
    }

    #[test]
    fn test_render_page_high_level_api() {
        let doc = PdfDocument::from_bytes(create_test_pdf("Hello World")).unwrap();

        let options = RenderOptions::default();
        let image = render_page(&doc, 0, &options).unwrap();

        assert!(image.width > 0);
        assert!(image.height > 0);
        assert_eq!(image.format, ImageFormat::Png);
        assert!(!image.data.is_empty());
        assert!(image.data.starts_with(b"\x89PNG"));
    }

    #[test]
    fn test_render_page_jpeg_format() {
        let doc = PdfDocument::from_bytes(create_test_pdf("Hello JPEG")).unwrap();

        let options = RenderOptions::with_dpi(72).as_jpeg(80);
        let image = render_page(&doc, 0, &options).unwrap();

        assert_eq!(image.format, ImageFormat::Jpeg);
        assert!(!image.data.is_empty());
        assert_eq!(image.data[0], 0xFF);
        assert_eq!(image.data[1], 0xD8);
    }
}
