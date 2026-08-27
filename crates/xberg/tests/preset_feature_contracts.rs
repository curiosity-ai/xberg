#[cfg(feature = "embedding-presets")]
#[test]
fn embedding_preset_serde_preserves_the_complete_feature_enabled_schema() {
    let preset = xberg::get_embedding_preset("arctic-embed-m-v2.0").expect("embedding preset must exist");
    let value = serde_json::to_value(&preset).expect("embedding preset must serialize");

    assert_eq!(value["name"], "arctic-embed-m-v2.0");
    assert_eq!(value["backend"], "onnx");
    assert_eq!(value["additional_files"][0], "arctic-embed-m-v2.0/model.onnx.data");
    assert_eq!(value["query_prefix"], "query: ");

    let decoded: xberg::EmbeddingPreset = serde_json::from_value(value).expect("embedding preset must deserialize");
    assert_eq!(decoded.backend, xberg::embeddings::EmbeddingBackend::Onnx);
    assert_eq!(decoded.additional_files, ["arctic-embed-m-v2.0/model.onnx.data"]);
    assert_eq!(decoded.query_prefix.as_deref(), Some("query: "));
}

#[cfg(feature = "reranker-presets")]
#[test]
fn reranker_preset_serde_preserves_the_complete_feature_enabled_schema() {
    let preset = xberg::get_reranker_preset("qwen3-reranker-0.6b").expect("reranker preset must exist");
    let value = serde_json::to_value(&preset).expect("reranker preset must serialize");

    assert_eq!(value["name"], "qwen3-reranker-0.6b");
    assert_eq!(value["additional_files"][0], "qwen3-reranker-0.6b/model.onnx.data");
    assert_eq!(value["head"], "qwen3_generative");

    let decoded: xberg::RerankerPreset = serde_json::from_value(value).expect("reranker preset must deserialize");
    assert_eq!(decoded.additional_files, ["qwen3-reranker-0.6b/model.onnx.data"]);
    assert_eq!(decoded.head, xberg::RerankerHead::Qwen3Generative);
}
