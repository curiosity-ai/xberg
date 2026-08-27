```java title="Java"
import io.xberg.ExtractionConfig;
import io.xberg.PdfConfig;
import io.xberg.HierarchyConfig;

ExtractionConfig config = ExtractionConfig.builder()
    .withPdfOptions(PdfConfig.builder()
        .withHierarchy(HierarchyConfig.builder()
            .withEnabled(true)
            .withKClusters(3L)
            .withIncludeBbox(true)
            .build())
        .build())
    .build();
```
