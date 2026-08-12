---
id: fixture_ruby_register_reranker_backend_trait_bridge
language: ruby
target: ruby
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```ruby title="Ruby"
require "xberg"
stub_register_reranker_backend_trait_bridge = Class.new do
  def name = 'test-reranker-backend'
  def initialize
    nil
  end
  def shutdown
    nil
  end
  def version = '1.0.0'
  def rerank(query, documents) = []
end.new
Xberg.register_reranker_backend(stub_register_reranker_backend_trait_bridge, 'test-reranker-backend')

```
