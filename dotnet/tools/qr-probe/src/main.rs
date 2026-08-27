//! Run the real `rqrr` over image files and print what it found as JSON.
//!
//! Usage: `qr-probe <image> [<image>...]`
//!
//! Each entry reports the decoded payloads and their axis-aligned pixel bounding boxes, in the
//! order rqrr yields them, so a diff against the port pins both detection and decoding.

use serde_json::json;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut out = serde_json::Map::new();

    for path in &args {
        let bytes = std::fs::read(path).unwrap_or_default();
        let mut codes = Vec::new();

        if !bytes.is_empty()
            && let Ok(dynamic) = image::load_from_memory(&bytes)
        {
            let luma = dynamic.to_luma8();
            let (width, height) = luma.dimensions();
            let raw = luma.into_raw();
            let mut prepared = rqrr::PreparedImage::prepare_from_greyscale(
                width as usize,
                height as usize,
                |x, y| raw[y * width as usize + x],
            );
            for grid in prepared.detect_grids() {
                let mut payload: Vec<u8> = Vec::new();
                if grid.decode_to(&mut payload).is_ok() {
                    let text = String::from_utf8_lossy(&payload).into_owned();
                    let xs = [
                        grid.bounds[0].x,
                        grid.bounds[1].x,
                        grid.bounds[2].x,
                        grid.bounds[3].x,
                    ];
                    let ys = [
                        grid.bounds[0].y,
                        grid.bounds[1].y,
                        grid.bounds[2].y,
                        grid.bounds[3].y,
                    ];
                    let min_x = *xs.iter().min().unwrap();
                    let max_x = *xs.iter().max().unwrap();
                    let min_y = *ys.iter().min().unwrap();
                    let max_y = *ys.iter().max().unwrap();
                    codes.push(json!({
                        "payload": text,
                        "x": min_x.max(0),
                        "y": min_y.max(0),
                        "width": (max_x - min_x).max(0),
                        "height": (max_y - min_y).max(0),
                    }));
                }
            }
        }

        out.insert(path.clone(), json!(codes));
    }

    println!("{}", serde_json::to_string_pretty(&out).unwrap());
}
