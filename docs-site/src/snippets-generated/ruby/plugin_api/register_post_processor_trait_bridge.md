---
id: fixture_ruby_register_post_processor_trait_bridge
language: ruby
target: ruby
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```ruby title="Ruby"
require "xberg"
stub_register_post_processor_trait_bridge = Class.new do
  def name = 'test-processor'
  def initialize
    nil
  end
  def shutdown
    nil
  end
  def version = '1.0.0'
  def process(result, config) = nil
  def processing_stage = '{}'
end.new
Xberg.register_post_processor(stub_register_post_processor_trait_bridge, 'test-processor')

```
