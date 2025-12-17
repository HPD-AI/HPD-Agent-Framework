# AgentConfig - Document Handling

## Overview

Configure how agents handle file uploads and text extraction from documents.

## Properties

### DocumentHandling
Settings for document processing.

[Detailed docs →](./AgentConfig-DocumentHandling.md)

Properties:
- `DocumentTagFormat` - Custom tag format for injecting documents into messages
- `MaxFileSizeBytes` - Maximum file size to process (default: 10MB)

**Note:** This is legacy configuration. The modern approach uses `WithDocumentHandling()` middleware extension.

## Examples

[Coming soon...]

## Related Topics

- [Document Handling Middleware](../Middleware/DocumentHandling.md)
- [Text Extraction](../Documents/TextExtraction.md)
- [File Upload Security](../Security/FileUploads.md)
