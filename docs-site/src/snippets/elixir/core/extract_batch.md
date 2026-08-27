```elixir title="Elixir"
inputs = [
  %{"kind" => "uri", "uri" => "report.pdf"},
  %{"kind" => "uri", "uri" => "notes.txt"}
]

case Xberg.extract_batch(inputs: inputs) do
  {:ok, output} -> Enum.each(output.results, &IO.puts(&1.content))
  {:error, reason} -> IO.puts(:stderr, "Extraction failed: #{inspect(reason)}")
end
```
