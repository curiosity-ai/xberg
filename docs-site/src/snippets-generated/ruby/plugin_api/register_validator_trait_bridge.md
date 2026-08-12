---
id: fixture_ruby_register_validator_trait_bridge
language: ruby
target: ruby
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```ruby title="Ruby"
require "xberg"
stub_register_validator_trait_bridge = Class.new do
  def name = 'test-validator'
  def initialize
    nil
  end
  def shutdown
    nil
  end
  def version = '1.0.0'
  def validate(result, config) = nil
end.new
Xberg.register_validator(stub_register_validator_trait_bridge, 'test-validator')

```
