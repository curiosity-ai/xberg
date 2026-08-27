use xberg::ExtractionConfig;
use xberg::cancellation::CancellationToken;
#[cfg(feature = "tokio-runtime")]
use xberg::{ExtractInput, XbergError, extract, extract_batch};

#[test]
fn rust_callers_can_request_and_observe_cooperative_cancellation() {
    let caller_token = CancellationToken::new();
    let extraction_token = caller_token.clone();

    let config = ExtractionConfig {
        cancel_token: Some(extraction_token),
        ..Default::default()
    };

    assert!(
        !caller_token.is_cancelled(),
        "a newly created token must not contain a cancellation request"
    );

    caller_token.cancel();

    assert!(
        caller_token.is_cancelled(),
        "the caller's token must observe its cancellation request"
    );
    assert!(
        config
            .cancel_token
            .as_ref()
            .expect("the extraction config should retain its token")
            .is_cancelled(),
        "the token stored in ExtractionConfig must share the same cancellation state"
    );
}

#[cfg(feature = "tokio-runtime")]
#[tokio::test]
async fn pre_cancelled_extraction_returns_cancelled_error() {
    let token = CancellationToken::new();
    token.cancel();
    let config = ExtractionConfig {
        cancel_token: Some(token),
        extraction_timeout_secs: None,
        ..Default::default()
    };

    let input = ExtractInput::from_bytes(b"cancel me".to_vec(), "text/plain", None);
    let result = extract(input, &config).await;

    assert!(
        matches!(result, Err(XbergError::Cancelled)),
        "a pre-cancelled extraction must return XbergError::Cancelled, got {result:?}"
    );
}

#[cfg(feature = "tokio-runtime")]
#[tokio::test]
async fn pre_cancelled_batch_returns_cancelled_error() {
    let token = CancellationToken::new();
    token.cancel();
    let config = ExtractionConfig {
        cancel_token: Some(token),
        extraction_timeout_secs: None,
        ..Default::default()
    };
    let inputs = vec![ExtractInput::from_bytes(b"cancel me".to_vec(), "text/plain", None)];

    let result = extract_batch(inputs, &config).await;

    assert!(
        matches!(result, Err(XbergError::Cancelled)),
        "a pre-cancelled batch must return XbergError::Cancelled, got {result:?}"
    );
}
