import XCTest

@testable import Xberg

final class XbergTests: XCTestCase {
  /// Round-trips the generated `CacheStats` DTO through `JSONEncoder`/`JSONDecoder`,
  /// so a broken `Codable` conformance or a field that silently stops encoding fails
  /// `swift test` immediately instead of shipping green with a suite that asserts
  /// nothing about the generated API. Create-only scaffold seed. ~keep
  func testCacheStatsRoundTripsThroughJSON() throws {
    let original = CacheStats(totalFiles: 1, totalSizeMb: 1.5, availableSpaceMb: 1.5, oldestFileAgeDays: 1.5, newestFileAgeDays: 1.5)
    let data = try JSONEncoder().encode(original)
    let decoded = try JSONDecoder().decode(CacheStats.self, from: data)
    XCTAssertEqual(decoded, original)
  }
}
