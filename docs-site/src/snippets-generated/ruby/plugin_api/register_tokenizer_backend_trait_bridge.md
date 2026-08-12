---
id: fixture_ruby_register_tokenizer_backend_trait_bridge
language: ruby
target: ruby
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```ruby title="Ruby"
require "xberg"
stub_register_tokenizer_backend_trait_bridge = Class.new do
  def name = 'test-tokenizer-backend'
  def initialize
    nil
  end
  def shutdown
    nil
  end
  def version = '1.0.0'
  def count_tokens(text) = 1
end.new
Xberg.register_tokenizer_backend(stub_register_tokenizer_backend_trait_bridge, 'test-tokenizer-backend')

```
