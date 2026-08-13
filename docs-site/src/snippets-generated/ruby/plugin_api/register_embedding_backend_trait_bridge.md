---
id: fixture_ruby_register_embedding_backend_trait_bridge
language: ruby
target: ruby
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```ruby title="Ruby"
require "xberg"
stub_register_embedding_backend_trait_bridge = Class.new do
  def name = 'test-embedding-backend'
  def initialize
    nil
  end
  def shutdown
    nil
  end
  def version = '1.0.0'
  def dimensions = 1
  def embed(texts) = []
end.new
Xberg.register_embedding_backend(stub_register_embedding_backend_trait_bridge, 'test-embedding-backend')

```
