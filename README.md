# S3Orchestrator-Ext-Shared

`S3Orchestrator-Ext-Shared` is a .NET 8 OutSystems external logic library that bridges OutSystems apps and Amazon S3.

It focuses on the scenarios this codebase currently implements:

- Creating pre-signed S3 URLs for download and upload.
- Listing buckets and objects, reading object metadata, resolving bucket region, and creating or deleting buckets.
- Moving binaries between OutSystems REST endpoints and S3, including large-file flows that avoid typical platform payload limits.
- Managing S3 objects with rename, move, and delete operations.
- Validating and normalizing PDFs stored in S3 with a bundled `qpdf` runtime.

## Current capability surface

The public actions exposed by the external library are:

| Area | Actions |
| --- | --- |
| Pre-signed URLs | `GetObjectPreSignedUrl`, `PutObjectPreSignedUrl` |
| Bucket and object discovery | `ListBuckets`, `GetBucketLocation`, `ListObjects`, `GetObjectMetadata` |
| Bucket management | `CreateBucket`, `DeleteBucket` |
| OutSystems REST <-> S3 transfer | `UploadFromRestToPresignedUrl`, `DownloadFromPresignedUrlToRest`, `DownloadFromPresignedUrlToRestBase64`, `UploadFromRestToS3Multipart` |
| Object lifecycle | `RenameMoveObject`, `RenameObject`, `MoveObject`, `DeleteObject` |
| PDF validation and rewrite | `CheckPdfInS3WithQpdf`, `NormalizePdfInS3WithQpdf` |

## Large-file transfer scenarios

This library currently supports three distinct transfer patterns:

1. `UploadFromRestToPresignedUrl`
   Streams a binary from an OutSystems REST source URL into a single pre-signed S3 `PUT`.
   Use this for simpler uploads when the source endpoint can safely serve the payload in one response.

2. `DownloadFromPresignedUrlToRest` and `DownloadFromPresignedUrlToRestBase64`
   Pulls from a pre-signed S3 `GET` URL and posts the file to an OutSystems REST target in chunks.
   These flows exist to help bypass payload limits on the OutSystems side.

3. `UploadFromRestToS3Multipart`
   Pulls a source binary from an OutSystems REST endpoint in slices and uploads it to S3 with Multipart Upload.
   This is the current large-upload path when a single-part pre-signed `PUT` is not enough.

Operational details reflected in the current implementation:

- Multipart upload uses a minimum part size of 5 MB and defaults to 8 MB.
- Chunked S3 -> REST download defaults to about 25 MB chunks.
- The base64 chunked variant defaults to 18 MB raw chunks to leave room for base64 expansion.
- Multipart upload fails early if the computed part count would exceed the S3 limit of 10,000 parts.
- Transient HTTP and S3 failures are retried during multipart upload.

## PDF validation and normalization

The repository now includes PDF helpers backed by a bundled `qpdf` runtime:

- `CheckPdfInS3WithQpdf`
  Runs `qpdf --check` against a PDF stored in S3 and returns structured diagnostics.

- `NormalizePdfInS3WithQpdf`
  Downloads a PDF from S3, applies a `qpdf` transformation, validates the output, and overwrites the same S3 key on success.

Supported normalization modes in the current code:

- `Linearize`
- `FlattenAnnotationsAll`
- `FlattenAnnotationsPrint`
- `FlattenAnnotationsScreen`

Important runtime constraints:

- `qpdf` normalization is supported only on Linux x64 external-logic runtimes.
- Encrypted PDFs are rejected by `NormalizePdfInS3WithQpdf`.
- The normalization flow currently supports source PDFs up to 100 MB.
- The `qpdf` runtime bundle is stored at `resources/qpdf/qpdf-12.3.0-bin-linux-x86_64.zip` and is copied to build/publish output.

## AWS and bucket behavior

The current implementation also includes a few behavior details that are easy to miss:

- Bucket region discovery is based on `HeadBucket`.
- Region values are normalized before being returned.
- `CreateBucket` omits the location constraint for `us-east-1`.
- `CreateBucket` verifies the bucket after creation and returns a clear failure when the bucket already exists.

## Build

Requirements:

- .NET 8 SDK

Build the solution:

```bash
dotnet build S3Orchestrator-Ext-Shared.sln
```

Build just the library project:

```bash
dotnet build S3Orchestrator_ExternalLogic.csproj
```

## Publish notes

This repo is treated as a public snapshot source repo, so release-facing changes should keep the public contract stable.

Before publishing or packaging, verify at least the following:

- The external action and parameter names remain compatible with previously published versions.
- The bundled native asset `resources/qpdf/qpdf-12.3.0-bin-linux-x86_64.zip` exists and is included in publish output.
- The published output contains the native `qpdf` files required by the PDF actions.

## Repository layout

```text
.
├── S3Orchestrator-Ext-Shared.sln
├── S3Orchestrator_ExternalLogic.cs
├── S3Orchestrator_ExternalLogic.csproj
├── resources/
│   ├── S3Orchestrator_ExternalLogic_lib.png
│   └── qpdf/
└── .github/
```

## Notes for maintainers

- The main implementation and the OutSystems public contract live in `S3Orchestrator_ExternalLogic.cs`.
- If you add or change actions, update this README together with the inline `OSAction`, `OSParameter`, and structure descriptions.
