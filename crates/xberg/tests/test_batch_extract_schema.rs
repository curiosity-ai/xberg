//! Issue #877: the generated JSON schema for `ExtractBatchParams` must declare
//! `inputs.items` as an object (not boolean `true`) and document the `kind`
//! discriminator, since Moonshot AI rejects boolean `items` schemas.

#![cfg(feature = "mcp")]

use rmcp::schemars::schema_for;
use xberg::mcp::ExtractBatchParams;

#[test]
fn test_inputs_items_is_object_not_boolean() {
    let schema = schema_for!(ExtractBatchParams);
    let schema_value = serde_json::to_value(&schema).unwrap();

    let items = &schema_value["properties"]["inputs"]["items"];
    assert!(
        items.is_object(),
        "inputs items must be an object, got: {items} — \
         Moonshot AI rejects boolean `items: true` (issue #877)"
    );
}

#[test]
fn test_inputs_items_describes_the_input_envelope() {
    let schema = schema_for!(ExtractBatchParams);
    let schema_value = serde_json::to_value(&schema).unwrap();

    let item_props = &schema_value["properties"]["inputs"]["items"]["properties"];
    assert!(
        item_props["kind"].is_object(),
        "inputs items must document the `kind` discriminator, got: {item_props}"
    );
}

#[test]
fn test_inputs_is_required() {
    let schema = schema_for!(ExtractBatchParams);
    let schema_value = serde_json::to_value(&schema).unwrap();

    let is_required = schema_value["required"]
        .as_array()
        .map(|r| r.iter().any(|f| f.as_str() == Some("inputs")))
        .unwrap_or(false);

    assert!(is_required, "inputs must be a required field on ExtractBatchParams");
}
