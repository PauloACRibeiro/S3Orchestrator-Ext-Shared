# S3Orchestrator-Ext-Shared

`S3Orchestrator-Ext-Shared` is a .NET 10 OutSystems external logic library for moving binaries between OutSystems REST endpoints and Amazon S3.

The current codebase is focused on:

- Creating pre-signed S3 URLs for download and upload.
- Listing buckets and objects and reading object metadata.
- Creating and deleting buckets.
- Moving large binaries between OutSystems REST endpoints and S3.
- Renaming, moving, and deleting objects in S3.

## Current public actions

The external library currently exposes these actions:

| Area | Actions |
| --- | --- |
| Pre-signed URLs | `GetObjectPreSignedUrl`, `PutObjectPreSignedUrl` |
| Discovery | `ListObjects`, `GetObjectMetadata`, `ListBuckets`, `GetBucketLocation` |
| Bucket management | `CreateBucket`, `DeleteBucket` |
| Transfer flows | `UploadFromRestToPresignedUrl`, `DownloadFromPresignedUrlToRest`, `UploadFromRestToS3Multipart` |
| Object operations | `RenameObject`, `RenameFile`, `DeleteFile`, `MoveFile` |

## Transfer patterns

This repository currently implements three transfer patterns:

1. `UploadFromRestToPresignedUrl`
   Streams a binary from an OutSystems REST source endpoint into a single pre-signed S3 `PUT`.

2. `DownloadFromPresignedUrlToRest`
   Downloads from a pre-signed S3 `GET` URL and posts the content to an OutSystems REST target in chunks to help work around platform payload limits.

3. `UploadFromRestToS3Multipart`
   Pulls a source binary from an OutSystems REST endpoint in slices and uploads it to S3 with Multipart Upload.

Current implementation details worth knowing:

- Multipart upload uses a minimum part size of 5 MB and defaults to 8 MB.
- Chunked S3 to REST download defaults to about 25 MB chunks.
- Multipart upload returns an explicit failure if the computed part count would exceed the S3 limit of 10,000 parts.
- Multipart upload retries transient HTTP and S3 failures.
- Unknown-length streams for pre-signed `PUT` uploads are spooled to a temporary file to keep memory bounded.

## AWS behavior

The current implementation also includes a few S3-specific behavior details:

- Bucket region values are normalized before being returned.
- `CreateBucket` omits the location constraint for `us-east-1`.
- Bucket clients are built from `AccessKeyId`, `SecretAccessKey`, and `Region` supplied through `S3AuthInfo`.

## Build

Requirements:

- .NET 8 SDK

Build the solution:

```bash
dotnet build S3Orchestrator-Ext-Shared.sln
```

Build the project directly:

```bash
dotnet build S3Orchestrator_ExternalLogic.csproj
```

## Project layout

```text
.
├── S3Orchestrator-Ext-Shared.sln
├── S3Orchestrator_ExternalLogic.cs
├── S3Orchestrator_ExternalLogic.csproj
├── resources/
│   └── S3Orchestrator_ExternalLogic_lib.png
└── .github/
```

## Notes for maintainers

- The public contract and implementation both live in `S3Orchestrator_ExternalLogic.cs`.
- If the OutSystems action surface changes, update this README in the same change.
- This repo is treated like a release-facing snapshot, so action names and parameter semantics should be changed carefully.
