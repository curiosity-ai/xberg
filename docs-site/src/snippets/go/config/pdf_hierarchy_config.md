```go title="Go"
package main

import "github.com/xberg-io/xberg/packages/go"

func main() {
	enabled := true
	includeBbox := true
	kClusters := uint(6)
	kClustersAdvanced := uint(12)

	// Basic hierarchy configuration
	config := xberg.ExtractionConfig{
		PdfOptions: &xberg.PdfConfig{
			ExtractImages: true,
			Hierarchy: &xberg.HierarchyConfig{
				Enabled:     &enabled,
				KClusters:   &kClusters,
				IncludeBbox: &includeBbox,
			},
		},
	}

	// Advanced hierarchy configuration with more clusters
	advancedConfig := xberg.ExtractionConfig{
		PdfOptions: &xberg.PdfConfig{
			ExtractImages: true,
			Hierarchy: &xberg.HierarchyConfig{
				Enabled:     &enabled,
				KClusters:   &kClustersAdvanced,
				IncludeBbox: &includeBbox,
			},
		},
	}

	_ = config
	_ = advancedConfig
}
```
