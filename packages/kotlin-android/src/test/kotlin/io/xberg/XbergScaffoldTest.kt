package io.xberg

import com.fasterxml.jackson.module.kotlin.jacksonObjectMapper
import org.junit.Assert.assertEquals
import org.junit.Test

class XbergScaffoldTest {
    // Round-trips the generated `CacheStats` data class through the Jackson mapping the
    // JNI bridge marshals values with: it fails to compile if the generated constructor
    // loses a parameter or changes a type, and fails at runtime if the class stops being
    // serializable or stops rebuilding an equal value. It proves nothing about the native
    // library -- no tier here loads it, deliberately; see the note on the emitter. Seeded
    // once and never regenerated over, so replace it with a real suite. ~keep
    @Test
    fun cacheStatsRoundTripsThroughItsGeneratedJsonMapping() {
        val original = CacheStats(1L, 1.5, 1.5, 1.5, 1.5)
        val mapper = jacksonObjectMapper()
        val json = mapper.writeValueAsString(original)
        assertEquals(original, mapper.readValue(json, CacheStats::class.java))
    }
}
