---
id: fixture_ruby_register_ocr_backend_trait_bridge
language: ruby
target: ruby
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```ruby title="Ruby"
require "xberg"
stub_register_ocr_backend_trait_bridge = Class.new do
  def name = 'test-backend'
  def initialize
    nil
  end
  def shutdown
    nil
  end
  def version = '1.0.0'
  def process_image(image_bytes, config) = '{}'
  def supports_language(lang) = false
  def backend_type = '{}'
end.new
Xberg.register_ocr_backend(stub_register_ocr_backend_trait_bridge, 'test-backend')

```
