# frozen_string_literal: true

require_relative "../lib/xberg"

RSpec.describe Xberg do
  # No generated function is safe to call with no arguments, so this exercises the
  # binding through the generated `CacheStats` class instead: the `require_relative`
  # above dlopens the compiled extension (LoadError when missing), the keyword
  # constructor registered by Magnus is invoked, and every field is read back through its
  # generated accessor. A dropped or renamed field fails here, because the constructor
  # ignores unknown keys and the accessor would return the field's default instead of the
  # value passed in. It proves nothing beyond field storage. Create-only scaffold seed:
  # alef never regenerates over this file, so replace it with a real suite. ~keep
  it "constructs the generated `CacheStats` class from keyword arguments" do
    instance = described_class::CacheStats.new(
      total_files: 1,
      total_size_mb: 1.5,
      available_space_mb: 1.5,
      oldest_file_age_days: 1.5,
      newest_file_age_days: 1.5
    )
    expect(
      [
        instance.total_files,
        instance.total_size_mb,
        instance.available_space_mb,
        instance.oldest_file_age_days,
        instance.newest_file_age_days
      ]
    )
      .to(eq([1, 1.5, 1.5, 1.5, 1.5]))
  end
end
