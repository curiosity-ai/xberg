package io.xberg;

import com.fasterxml.jackson.databind.ObjectMapper;
import java.util.List;

/**
 * Test factory that parses per-document JSON into real {@link
 * ExtractedDocument} instances and wraps them in an {@link ExtractionResult}
 * envelope, mirroring what {@code Xberg.extract} and {@code Xberg.extractBatch}
 * return.
 *
 * <p>
 * The Xberg DTOs use {@code @Nullable} accessors (not {@code Optional}), so a
 * plain {@link ObjectMapper} suffices — no {@code Jdk8Module} is required. The
 * {@code @JsonProperty} names on each record already map the snake_case wire
 * format, so no naming strategy is configured either. This class lives in the
 * {@code io.xberg} package so it can construct binding records directly.
 */
public final class ExtractionResultFactory {

  private static final ObjectMapper MAPPER = new ObjectMapper();

  private ExtractionResultFactory() {}

  /**
   * Parses a single extracted-document JSON payload and wraps it in an
   * {@link ExtractionResult} whose {@code results} list holds exactly one
   * document.
   */
  public static ExtractionResult fromJson(String json) {
    return fromDocuments(List.of(json));
  }

  /**
   * Parses several extracted-document JSON payloads and wraps them in a single
   * {@link ExtractionResult}, mirroring a successful {@code
   * Xberg.extractBatch}.
   */
  public static ExtractionResult fromDocuments(List<String> jsons) {
    List<ExtractedDocument> documents =
        jsons.stream().map(ExtractionResultFactory::parse).toList();
    ExtractionSummary summary = ExtractionSummary.builder()
                                    .withInputs(documents.size())
                                    .withResults(documents.size())
                                    .build();
    return ExtractionResult.builder()
        .withResults(documents)
        .withSummary(summary)
        .build();
  }

  /**
   * Builds an {@link ExtractionResult} that carries both successful documents
   * and non-fatal per-input errors, mirroring a partially failed batch.
   */
  public static ExtractionResult withErrors(List<String> jsons,
                                            List<ExtractionErrorItem> errors) {
    List<ExtractedDocument> documents =
        jsons.stream().map(ExtractionResultFactory::parse).toList();
    ExtractionSummary summary =
        ExtractionSummary.builder()
            .withInputs(documents.size() + errors.size())
            .withResults(documents.size())
            .withErrors(errors.size())
            .build();
    return ExtractionResult.builder()
        .withResults(documents)
        .withErrors(errors)
        .withSummary(summary)
        .build();
  }

  private static ExtractedDocument parse(String json) {
    try {
      return MAPPER.readValue(json, ExtractedDocument.class);
    } catch (Exception e) {
      throw new IllegalArgumentException(
          "Failed to parse extracted-document JSON", e);
    }
  }
}
