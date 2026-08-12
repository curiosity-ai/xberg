```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig

async def main() -> None:
    result = await extract(ExtractInput(uri="document.pdf"), ExtractionConfig())

    # Common fields live directly on Metadata
    metadata = result.results[0].metadata
    pdf_metadata = metadata.format.pdf if metadata.format else None
    if pdf_metadata and pdf_metadata.page_count:
        print(f"Pages: {pdf_metadata.page_count}")
    if metadata.title:
        print(f"Title: {metadata.title}")
    if metadata.authors:
        print(f"Authors: {', '.join(metadata.authors)}")

    html_result = await extract(ExtractInput(uri="page.html"), ExtractionConfig())
    html_metadata = html_result.results[0].metadata
    html_format = html_metadata.format.html if html_metadata.format else None
    if html_format:
        if html_format.title:
            print(f"Title: {html_format.title}")
        if html_format.description:
            print(f"Description: {html_format.description}")

        # Access keywords as array
        if html_format.keywords:
            print(f"Keywords: {', '.join(html_format.keywords)}")

        # Access canonical URL
        if html_format.canonical_url:
            print(f"Canonical URL: {html_format.canonical_url}")

        # Access Open Graph fields from map
        if html_format.open_graph:
            if "image" in html_format.open_graph:
                print(f"Open Graph Image: {html_format.open_graph['image']}")
            if "title" in html_format.open_graph:
                print(f"Open Graph Title: {html_format.open_graph['title']}")
            if "type" in html_format.open_graph:
                print(f"Open Graph Type: {html_format.open_graph['type']}")

        # Access Twitter Card fields from map
        if html_format.twitter_card:
            if "card" in html_format.twitter_card:
                print(f"Twitter Card Type: {html_format.twitter_card['card']}")
            if "creator" in html_format.twitter_card:
                print(f"Twitter Creator: {html_format.twitter_card['creator']}")

        if html_format.language:
            print(f"Language: {html_format.language}")

        if html_format.text_direction:
            print(f"Text Direction: {html_format.text_direction}")

        # Access headers
        if html_format.headers:
            print(f"Headers: {', '.join(h.text for h in html_format.headers)}")

        # Access links
        if html_format.links:
            for link in html_format.links:
                print(f"Link: {link.href} ({link.text})")

        # Access images
        if html_format.images:
            for image in html_format.images:
                print(f"Image: {image}")

        # Access structured data
        if html_format.structured_data:
            print(f"Structured data items: {len(html_format.structured_data)}")

asyncio.run(main())
```
