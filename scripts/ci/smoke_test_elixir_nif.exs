# Prove a built musl xberg_nif shared library actually extracts documents,
# not just that :erlang.load_nif() succeeds.
#
# This deliberately bypasses RustlerPrecompiled: it resolves its NIF file by
# downloading a matching precompiled release tarball from GitHub (or, with
# force_build, by invoking `cargo build` itself), neither of which tests the
# exact artifact this CI job just cross-compiled. Instead this script takes
# alef's generated `Xberg.Native` source, swaps the `use RustlerPrecompiled`
# block for an `@on_load` that calls :erlang.load_nif/2 on a given path, and
# compiles that -- so the loaded library is provably the one produced by
# docker/Dockerfile.musl-rustler in this job, not a separately built copy.
#
# The generated module (not a hand-written stub) is what gets patched because
# :erlang.load_nif/2 enforces two things a stub cannot satisfy: the calling
# module's name must equal the library's `rustler::init!` name, which is
# "Elixir.Xberg.Native" (packages/elixir/native/xberg_nif/src/lib.rs), and
# every NIF the library declares must have a matching exported function of the
# same arity in that module -- 325 of them. A four-line stub named
# XbergNifSmoke failed the first check outright with
# `{:bad_lib, "Library module name 'Elixir.Xberg.Native' does not match
# calling module ''Elixir.XbergNifSmoke'''"}`, and would have failed the
# second immediately after. ~keep
#
# A load-only check would still be weak: :erlang.load_nif/2 resolves the
# shared object and binds every declared NIF by arity, which already catches
# a missing/wrong-arch .so or an ABI mismatch in exported symbol count -- but
# it does not prove a call into the library actually runs correctly (a panic
# inside Rust, or a subtly broken vendored shared-lib closure such as ONNX
# Runtime or libheif that resolves at load time but misbehaves at call time).
# This script therefore runs a real extraction against a known fixture and
# asserts the exact expected text comes back.
#
# Usage: elixir smoke_test_elixir_nif.exs <fixture-path> <expected-substring>
# Requires env vars:
#   XBERG_NIF_PATH  absolute path to the built .so, WITHOUT the .so extension
#                   (the convention :erlang.load_nif/2 expects)
#   XBERG_NATIVE_EX absolute path to packages/elixir/lib/xberg/native.ex
# Exit code is non-zero, with a message on stderr, on any failure: NIF load
# failure, extract_async returning {:error, _}, an empty result, or a content
# mismatch.

defmodule Loader do
  @moduledoc false

  # Matches from the `use RustlerPrecompiled,` line through the end of the
  # `force_build:` line that closes its keyword list. Anchored on both so a
  # future option added between them is swallowed too, rather than left behind
  # as a dangling keyword list.
  #
  # The tail is `[^\n]*`, not `.*`: under `/s` a greedy `.*` runs to the end of
  # the file and then backtracks to the LAST position where `$` matches, which
  # deleted every one of the module's 325 stubs and left a `defmodule` with no
  # `end`. Only the span between the two anchors may cross newlines. ~keep
  @use_block ~r/^  use RustlerPrecompiled,.*?force_build:[^\n]*$/ms

  @loader """
    @on_load :load_xberg_nif

    def load_xberg_nif do
      path =
        System.get_env("XBERG_NIF_PATH") ||
          raise "XBERG_NIF_PATH env var not set (absolute path to the .so, without extension)"

      case :erlang.load_nif(String.to_charlist(path), 0) do
        :ok -> :ok
        {:error, reason} -> raise "failed to load NIF at \#{path}: \#{inspect(reason)}"
      end
    end
  """

  def compile_native! do
    source_path =
      System.get_env("XBERG_NATIVE_EX") ||
        raise "XBERG_NATIVE_EX env var not set (absolute path to packages/elixir/lib/xberg/native.ex)"

    source = File.read!(source_path)

    unless Regex.match?(@use_block, source) do
      raise "no `use RustlerPrecompiled` block found in #{source_path}; the generated module changed shape"
    end

    patched = Regex.replace(@use_block, source, @loader, global: false)
    Code.compile_string(patched, source_path)
  end
end

defmodule Runner do
  @moduledoc false

  def fail(message) do
    IO.puts(:stderr, "SMOKE TEST FAILED: #{message}")
    System.halt(1)
  end

  def run([fixture_path, expected_substring]) do
    Loader.compile_native!()

    input_json = ~s({"kind":"uri","uri":#{Kernel.inspect(fixture_path)}})

    case apply(Xberg.Native, :extract_async, [input_json, nil]) do
      {:ok, %{results: [first | _]}} ->
        content = Map.get(first, :content)

        if is_binary(content) and String.contains?(content, expected_substring) do
          IO.puts("SMOKE TEST PASSED: found #{inspect(expected_substring)} in extracted content")
        else
          fail(
            "expected substring #{inspect(expected_substring)} not found in extracted content " <>
              "for fixture #{fixture_path}; got: #{inspect(content)}"
          )
        end

      {:ok, %{results: []}} ->
        fail("extract_async returned zero results for fixture #{fixture_path}")

      {:error, reason} ->
        fail("extract_async returned an error for fixture #{fixture_path}: #{inspect(reason)}")
    end
  end

  def run(_args) do
    fail("usage: elixir smoke_test_elixir_nif.exs <fixture-path> <expected-substring>")
  end
end

Runner.run(System.argv())
