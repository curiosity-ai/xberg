//! Dump what the real `YoloModel` decodes out of a fixed model output.
//!
//! Usage: `yolo-probe <image> <variant>=<model.onnx> ...`
//! where `variant` is `doclaynet`, `docstructbench` or `yolox`.

use std::path::PathBuf;

use xberg::layout::{CustomModelVariant, LayoutEngine, LayoutEngineConfig, ModelBackend};

fn main() {
    let mut args = std::env::args().skip(1);
    let image_path = args.next().expect("usage: yolo-probe <image> <variant>=<model> ...");
    let img = image::open(&image_path).expect("open image").to_rgb8();

    let mut out = serde_json::Map::new();
    for spec in args {
        let (name, path) = spec.split_once('=').expect("expected <variant>=<model.onnx>");
        let variant = match name {
            "doclaynet" => CustomModelVariant::YoloDocLayNet,
            "docstructbench" => CustomModelVariant::YoloDocStructBench,
            "yolox" => CustomModelVariant::Yolox { input_width: 768, input_height: 1024 },
            other => panic!("unknown variant {other}"),
        };

        // `apply_heuristics: false` keeps the model's own output visible; the heuristics were
        // compared separately.
        let config = LayoutEngineConfig {
            backend: ModelBackend::Custom { path: PathBuf::from(path), variant },
            confidence_threshold: None,
            apply_heuristics: false,
            cache_dir: None,
            acceleration: None,
        };

        let mut engine = LayoutEngine::from_config(config).expect("build engine");
        let result = engine.detect(&img).expect("detect");

        let detections: Vec<serde_json::Value> = result
            .detections
            .iter()
            .map(|d| {
                serde_json::json!({
                    "class": d.class_name.as_str(),
                    "confidence": d.confidence,
                    "x1": d.bbox.x1,
                    "y1": d.bbox.y1,
                    "x2": d.bbox.x2,
                    "y2": d.bbox.y2,
                })
            })
            .collect();
        out.insert(name.to_string(), serde_json::Value::Array(detections));
    }

    println!("{}", serde_json::to_string_pretty(&out).unwrap());
}
