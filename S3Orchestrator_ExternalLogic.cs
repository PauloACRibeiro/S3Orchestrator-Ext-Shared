using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using OutSystems.ExternalLibraries.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

//
// -------- Version 1.0.16  --------
// CreateBucket omits location constraint for us-east-1 (classic) to avoid InvalidLocationConstraint
// Normalize bucket location values (EU/US -> canonical) and return normalized values from GetBucketLocation
// Presigned PUT spools unknown-length streams to a temp file (bounded memory)
// Shared HttpClient/handler to reduce socket churn and DNS overhead
// Multipart upload retries transient GET/UploadPart failures with backoff
// Sync helper uses ConfigureAwait(false) to reduce deadlock risk

namespace S3Orchestrator_ExternalLogic
{
  // -------- Data structures --------
  [OSStructure(Description = "AWS S3 credentials and region")]
  public struct S3AuthInfo
  {
    [OSStructureField(Description = "AWS Access Key Id")]
    public string AccessKeyId { get; set; }

    [OSStructureField(Description = "AWS Secret Access Key")]
    public string SecretAccessKey { get; set; }

    [OSStructureField(Description = "Region name, e.g., eu-central-1")]
    public string Region { get; set; }
  }

  [OSStructure(Description = "Result of downloading from S3 into ODC REST")]
  public struct DownloadToRestResult
  {
    [OSStructureField(Description = "ID of the inserted DB record returned by the ODC REST")]
    public string BinGuid { get; set; }

    [OSStructureField(Description = "True if the ODC REST reported success")]
    public bool Success { get; set; }

    [OSStructureField(Description = "Error message from the ODC REST (if any)")]
    public string ErrorMessage { get; set; }
  }

  [OSStructure(Description = "Result of uploading from an ODC REST source into S3")]
  public struct UploadToS3Result
  {
    [OSStructureField(Description = "GUID identifying the binary used as the source in the upload")]
    public string BinGuid { get; set; }

    [OSStructureField(Description = "True if the multipart upload completed successfully")]
    public bool Success { get; set; }

    [OSStructureField(Description = "Error message if the upload failed")]
    public string ErrorMessage { get; set; }
  }

  [OSStructure(Description = "S3 object entry")]
  public struct BucketObjectInfo
  {
    [OSStructureField(Description = "Object key including any virtual directory path")]
    public string S3Key { get; set; }

    [OSStructureField(Description = "Size of the object in bytes")]
    public long FileSize { get; set; }

    [OSStructureField(Description = "Last modified timestamp of the object (UTC)")]
    public DateTime CreatedAt { get; set; }
  }

  [OSStructure(Description = "Result of listing objects in an S3 bucket")]
  public struct ListObjectsResult
  {
    [OSStructureField(Description = "Objects returned for this page")]
    public BucketObjectInfo[] Items { get; set; }

    [OSStructureField(Description = "True if there are more objects to fetch")]
    public bool IsTruncated { get; set; }

    [OSStructureField(Description = "Continuation token for the next page (empty when done)")]
    public string NextContinuationToken { get; set; }
  }

  [OSStructure(Description = "S3 object metadata")]
  public struct ObjectMetadataInfo
  {
    [OSStructureField(Description = "Object key")]
    public string Key { get; set; }

    [OSStructureField(Description = "Size of the object in bytes")]
    public long Size { get; set; }

    [OSStructureField(Description = "Last modified timestamp of the object (UTC)")]
    public DateTime LastModifiedUtc { get; set; }

    [OSStructureField(Description = "Entity tag for the object (ETag)")]
    public string ETag { get; set; }

    [OSStructureField(Description = "Content-Type of the object")]
    public string ContentType { get; set; }
  }

  // -------- Interface (icon in resources folder) --------
  [OSInterface(
      Name = "S3Orchestrator_ExternalLogic",
      IconResourceName = "S3Orchestrator_ExternalLogic.resources.S3Orchestrator_ExternalLogic_lib.png",
      Description = "Move large binaries between OutSystems REST and S3 by orchestrating AWS multipart uploads and downloads"
  )]
  public interface IPreSigner
  {
    [OSAction(Description = "Create a pre-signed GET URL for an S3 object")]
    string GetObjectPreSignedUrl(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Object key")] string key,
      [OSParameter(Description = "Duration in minutes")] int durationInMinutes);

    [OSAction(Description = "Create a pre-signed PUT URL for an S3 object")]
    string PutObjectPreSignedUrl(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Object key")] string key,
      [OSParameter(Description = "Content-Type for the upload")] string contentType,
      [OSParameter(Description = "Duration in minutes")] int durationInMinutes);

    [OSAction(Description = "List objects in an S3 bucket with optional prefix filter and pagination", ReturnName = "success")]
    bool ListObjects(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Prefix filter (optional)")] string prefix,
      [OSParameter(Description = "Continuation token (optional)")] string continuationToken,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    [OSAction(Description = "Get metadata for an S3 object", ReturnName = "success")]
    bool GetObjectMetadata(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Object key")] string key,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    [OSAction(Description = "List S3 buckets available for the credentials", ReturnName = "success")]
    bool ListBuckets(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);
    [OSAction(Description = "Get the region for an S3 bucket", ReturnName = "success")]
    bool GetBucketLocation(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    [OSAction(Description = "Create an S3 bucket", ReturnName = "success")]
    bool CreateBucket(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    [OSAction(Description = "Delete an S3 bucket (must be empty)", ReturnName = "success")]
    bool DeleteBucket(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    // Existing: single GET (ODC) -> single PUT (S3). Suitable while source responses stay under the platform cap.
    [OSAction(Description = "Upload to S3 using a pre-signed single-part PUT by streaming a binary from an ODC REST Source URL")]
    string UploadFromRestToPresignedUrl(
      [OSParameter(Description = "Source REST URL in the ODC app")] string sourceUrl,
      [OSParameter(Description = "GUID identifying the binary in the ODC REST")] string binGuid,
      [OSParameter(Description = "Auth header name for Source (e.g., Authorization)")] string authHeaderName,
      [OSParameter(Description = "Auth header value for Source (e.g., Bearer <token>)")] string authHeaderValue,
      [OSParameter(Description = "Pre-signed S3 PUT URL (single-part)")] string presignedPutUrl,
      [OSParameter(Description = "Content-Type to enforce on PUT (must match the presign)")] string contentType,
      [OSParameter(Description = "Timeout in seconds (default 300)")] int timeoutSeconds);

    // Restored: S3 -> ODC REST (chunked; large files), always sends X-Chunk-Total and fixed Content-Type per chunk.
    [OSAction(Description = "Download from S3 (pre-signed GET) and POST in parts (chunks) to an ODC REST target to bypass 30MB limit")]
    DownloadToRestResult DownloadFromPresignedUrlToRest(
      [OSParameter(Description = "Pre-signed S3 GET URL")] string presignedGetUrl,
      [OSParameter(Description = "Target ODC REST base URL (receives binary via POST)")] string targetUrl,
      [OSParameter(Description = "S3 object Key to append as URL parameter ?Key=<key>")] string s3ObjectKey,
      [OSParameter(Description = "S3 bucket name to append as URL parameter ?BucketName=<bucket>")] string bucketName,
      [OSParameter(Description = "Auth header name for the target (e.g., Authorization)")] string targetAuthHeaderName,
      [OSParameter(Description = "Auth header value for the target")] string targetAuthHeaderValue,
      [OSParameter(Description = "Content-Type to send (fixed for all chunks; default application/octet-stream)")] string targetContentType,
      [OSParameter(Description = "Chunk size in bytes (default 25,000,000 ≈ 25 MB)")] int chunkSizeBytes,
      [OSParameter(Description = "Timeout per chunk request in seconds (default 120)")] int timeoutSeconds);

    // NEW: Large files – source is fetched in many small responses; target uses S3 Multipart Upload (no presigned URL).
    [OSAction(Description = "Upload a large binary from ODC REST to S3 using MULTIPART (pull source in chunks)")]
    UploadToS3Result UploadFromRestToS3Multipart(
      [OSParameter(Description = "Auth info for S3 (AccessKey/Secret/Region)")] S3AuthInfo authInfo,
      [OSParameter(Description = "S3 bucket name")] string bucketName,
      [OSParameter(Description = "S3 object key to create")] string key,
      [OSParameter(Description = "Final Content-Type of the object (e.g., application/pdf)")] string contentType,
      [OSParameter(Description = "Source REST base URL in the ODC app")] string sourceUrl,
      [OSParameter(Description = "GUID identifying the binary in the ODC REST")] string binGuid,
      [OSParameter(Description = "Auth header name for Source (e.g., Authorization)")] string authHeaderName,
      [OSParameter(Description = "Auth header value for Source (e.g., Bearer <token>)")] string authHeaderValue,
      [OSParameter(Description = "Chunk size in bytes (min 5MB for S3 multipart; default 8MB)")] int chunkSizeBytes,
      [OSParameter(Description = "Timeout per request in seconds (default 120)")] int timeoutSeconds);

    [OSAction(Description = "Rename an object in S3 by copying to a new key and deleting the old one", ReturnName = "success")]
    bool RenameObject(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Current object key")] string currentKey,
      [OSParameter(Description = "New object key")] string newKey,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    [OSAction(Description = "Rename a file in S3 (change filename, keep directory)", ReturnName = "success")]
    bool RenameFile(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Current object key")] string currentKey,
      [OSParameter(Description = "New file name (without path)")] string newFileName,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    [OSAction(Description = "Delete a file in S3", ReturnName = "success")]
    bool DeleteFile(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Object key")] string key,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);

    [OSAction(Description = "Move a file in S3 to a new directory", ReturnName = "success")]
    bool MoveFile(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Source object key")] string sourceKey,
      [OSParameter(Description = "Target directory (can be empty for root)")] string targetDirectory,
      [OSParameter(Description = "Error message when success is false")] out string errormessage);
  }

  // -------- Implementation --------
  public class PreSignerImpl : IPreSigner
  {
    private static readonly SocketsHttpHandler SharedHttpHandler = new SocketsHttpHandler
    {
      AllowAutoRedirect = true,
      AutomaticDecompression = DecompressionMethods.None,
      PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    private static readonly ConcurrentDictionary<int, HttpClient> HttpClients = new ConcurrentDictionary<int, HttpClient>();

    private static HttpClient GetHttpClient(int timeoutSeconds)
    {
      if (timeoutSeconds <= 0) timeoutSeconds = 120;
      return HttpClients.GetOrAdd(timeoutSeconds, seconds =>
      {
        return new HttpClient(SharedHttpHandler, disposeHandler: false)
        {
          Timeout = TimeSpan.FromSeconds(seconds)
        };
      });
    }

    private readonly ILogger _logger;

    public PreSignerImpl() : this(NullLogger<PreSignerImpl>.Instance) { }

    public PreSignerImpl(ILogger logger)
    {
      _logger = logger ?? NullLogger<PreSignerImpl>.Instance;
    }

    public string GetObjectPreSignedUrl(S3AuthInfo authInfo, string bucketName, string key, int durationInMinutes)
    {
      try
      {
        _logger.LogInformation("Generating pre-signed GET URL for bucket {Bucket} and key {Key}", bucketName, key);
        Validate(authInfo, bucketName, key, durationInMinutes);
        using var s3 = CreateClient(authInfo);
        var req = new GetPreSignedUrlRequest
        {
          BucketName = bucketName,
          Key = key,
          Verb = HttpVerb.GET,
          Expires = DateTime.UtcNow.AddMinutes(durationInMinutes)
        };
        var url = s3.GetPreSignedURL(req);
        _logger.LogInformation("Generated pre-signed GET URL for bucket {Bucket} and key {Key}", bucketName, key);
        return url;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to generate pre-signed GET URL for bucket {Bucket} and key {Key}", bucketName, key);
        throw;
      }
    }

    public bool ListObjects(S3AuthInfo authInfo, string bucketName, string prefix, string continuationToken, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Listing objects for bucket {Bucket} with prefix {Prefix}", bucketName, prefix ?? string.Empty);
        if (string.IsNullOrWhiteSpace(authInfo.AccessKeyId)) throw new ArgumentException("AccessKeyId is required.");
        if (string.IsNullOrWhiteSpace(authInfo.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
        if (string.IsNullOrWhiteSpace(authInfo.Region)) throw new ArgumentException("Region is required.");
        if (string.IsNullOrWhiteSpace(bucketName)) throw new ArgumentException("bucketName is required.");

        using var s3 = CreateClient(authInfo);
        var request = new ListObjectsV2Request
        {
          BucketName = bucketName,
          Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
          ContinuationToken = string.IsNullOrWhiteSpace(continuationToken) ? null : continuationToken
        };

        var response = Sync(s3.ListObjectsV2Async(request));
        var items = new List<BucketObjectInfo>();
        if (response.S3Objects != null)
        {
          foreach (var obj in response.S3Objects)
          {
            items.Add(new BucketObjectInfo
            {
              S3Key = obj.Key ?? string.Empty,
              FileSize = obj.Size.GetValueOrDefault(),
              CreatedAt = (obj.LastModified ?? DateTime.MinValue).ToUniversalTime()
            });
          }
        }

        var isTruncated = response.IsTruncated.GetValueOrDefault();

        _logger.LogInformation(
          "Listed {Count} objects from bucket {Bucket} (truncated={IsTruncated})",
          items.Count,
          bucketName,
          isTruncated);

        _ = new ListObjectsResult
        {
          Items = items.ToArray(),
          IsTruncated = isTruncated,
          NextContinuationToken = isTruncated ? (response.NextContinuationToken ?? string.Empty) : string.Empty
        };
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to list objects from bucket {Bucket}", bucketName);
        errormessage = ex.Message;
        return false;
      }
    }

    public bool GetObjectMetadata(S3AuthInfo authInfo, string bucketName, string key, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Getting metadata for bucket {Bucket} and key {Key}", bucketName, key);
        Validate(authInfo, bucketName, key, 1);
        ValidateS3Key(key);

        using var s3 = CreateClient(authInfo);
        var response = Sync(s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
          BucketName = bucketName,
          Key = key
        }));

        var etag = response.ETag ?? string.Empty;
        if (!string.IsNullOrEmpty(etag)) etag = etag.Trim('"');

        var lastModifiedUtc = response.LastModified.HasValue
          ? response.LastModified.Value.ToUniversalTime()
          : DateTime.MinValue;

        _ = new ObjectMetadataInfo
        {
          Key = key,
          Size = response.ContentLength,
          LastModifiedUtc = lastModifiedUtc,
          ETag = etag,
          ContentType = response.ContentType ?? string.Empty
        };

        _logger.LogInformation("Retrieved metadata for bucket {Bucket} and key {Key}", bucketName, key);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to get metadata for bucket {Bucket} and key {Key}", bucketName, key);
        errormessage = ex.Message;
        return false;
      }
    }

    public bool ListBuckets(S3AuthInfo authInfo, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Listing S3 buckets for provided credentials.");
        if (string.IsNullOrWhiteSpace(authInfo.AccessKeyId)) throw new ArgumentException("AccessKeyId is required.");
        if (string.IsNullOrWhiteSpace(authInfo.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
        if (string.IsNullOrWhiteSpace(authInfo.Region)) throw new ArgumentException("Region is required.");

        using var s3 = CreateClient(authInfo);
        var response = Sync(s3.ListBucketsAsync());
        var targetRegion = authInfo.Region.Trim();
        var bucketNames = new List<string>();

        if (response.Buckets != null)
        {
          foreach (var bucket in response.Buckets)
          {
            if (string.IsNullOrWhiteSpace(bucket.BucketName)) continue;
            var bucketName = bucket.BucketName;
            try
            {
              var locationResponse = Sync(s3.GetBucketLocationAsync(new GetBucketLocationRequest
              {
                BucketName = bucketName
              }));

              var location = NormalizeBucketRegion(locationResponse.Location);
              if (!string.Equals(location, targetRegion, StringComparison.OrdinalIgnoreCase)) continue;

              bucketNames.Add(bucketName);
            }
            catch (Exception ex)
            {
              _logger.LogWarning(ex, "Failed to resolve region for bucket {Bucket}; skipping.", bucketName);
            }
          }
        }

        _logger.LogInformation("Listed {Count} buckets for provided credentials in region {Region}.", bucketNames.Count, targetRegion);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to list buckets for provided credentials.");
        errormessage = ex.Message;
        return false;
      }
    }

    public bool GetBucketLocation(S3AuthInfo authInfo, string bucketName, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Getting location for bucket {Bucket}", bucketName);
        if (string.IsNullOrWhiteSpace(authInfo.AccessKeyId)) throw new ArgumentException("AccessKeyId is required.");
        if (string.IsNullOrWhiteSpace(authInfo.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
        if (string.IsNullOrWhiteSpace(authInfo.Region)) throw new ArgumentException("Region is required.");
        if (string.IsNullOrWhiteSpace(bucketName)) throw new ArgumentException("bucketName is required.");

        using var s3 = CreateClient(authInfo);
        var response = Sync(s3.GetBucketLocationAsync(new GetBucketLocationRequest
        {
          BucketName = bucketName
        }));

        var region = NormalizeBucketRegion(response.Location);
        _logger.LogInformation("Retrieved location {Region} for bucket {Bucket}", region, bucketName);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to get location for bucket {Bucket}", bucketName);
        errormessage = ex.Message;
        return false;
      }
    }

    public bool CreateBucket(S3AuthInfo authInfo, string bucketName, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Creating bucket {Bucket}", bucketName);
        if (string.IsNullOrWhiteSpace(authInfo.AccessKeyId)) throw new ArgumentException("AccessKeyId is required.");
        if (string.IsNullOrWhiteSpace(authInfo.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
        if (string.IsNullOrWhiteSpace(authInfo.Region)) throw new ArgumentException("Region is required.");
        if (string.IsNullOrWhiteSpace(bucketName)) throw new ArgumentException("bucketName is required.");

        using var s3 = CreateClient(authInfo);
        var bucketRegion = S3Region.FindValue(authInfo.Region);
        var isUsEast1 = string.Equals(bucketRegion?.Value, S3Region.USEast1.Value, StringComparison.OrdinalIgnoreCase)
          || string.Equals(authInfo.Region, "us-east-1", StringComparison.OrdinalIgnoreCase);
        var request = new PutBucketRequest
        {
          BucketName = bucketName
        };

        if (!isUsEast1)
        {
          request.BucketRegion = bucketRegion;
        }
        else
        {
          _logger.LogInformation("Skipping location constraint for us-east-1 when creating bucket {Bucket}", bucketName);
        }

        Sync(s3.PutBucketAsync(request));
        _logger.LogInformation("Created bucket {Bucket}", bucketName);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to create bucket {Bucket}", bucketName);
        errormessage = ex.Message;
        return false;
      }
    }

    public bool DeleteBucket(S3AuthInfo authInfo, string bucketName, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Deleting bucket {Bucket}", bucketName);
        if (string.IsNullOrWhiteSpace(authInfo.AccessKeyId)) throw new ArgumentException("AccessKeyId is required.");
        if (string.IsNullOrWhiteSpace(authInfo.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
        if (string.IsNullOrWhiteSpace(authInfo.Region)) throw new ArgumentException("Region is required.");
        if (string.IsNullOrWhiteSpace(bucketName)) throw new ArgumentException("bucketName is required.");

        using var s3 = CreateClient(authInfo);
        var request = new DeleteBucketRequest
        {
          BucketName = bucketName
        };

        Sync(s3.DeleteBucketAsync(request));
        _logger.LogInformation("Deleted bucket {Bucket}", bucketName);
        return true;
      }
      catch (AmazonS3Exception ex) when (string.Equals(ex.ErrorCode, "BucketNotEmpty", StringComparison.OrdinalIgnoreCase))
      {
        _logger.LogError(ex, "Failed to delete bucket {Bucket} because it is not empty.", bucketName);
        errormessage = "Bucket must be empty before deletion.";
        return false;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to delete bucket {Bucket}", bucketName);
        errormessage = ex.Message;
        return false;
      }
    }

    public string PutObjectPreSignedUrl(S3AuthInfo authInfo, string bucketName, string key, string contentType, int durationInMinutes)
    {
      try
      {
        _logger.LogInformation("Generating pre-signed PUT URL for bucket {Bucket} and key {Key}", bucketName, key);
        Validate(authInfo, bucketName, key, durationInMinutes);
        using var s3 = CreateClient(authInfo);
        var req = new GetPreSignedUrlRequest
        {
          BucketName = bucketName,
          Key = key,
          Verb = HttpVerb.PUT,
          Expires = DateTime.UtcNow.AddMinutes(durationInMinutes),
          ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType
        };
        var url = s3.GetPreSignedURL(req);
        _logger.LogInformation("Generated pre-signed PUT URL for bucket {Bucket} and key {Key}", bucketName, key);
        return url;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to generate pre-signed PUT URL for bucket {Bucket} and key {Key}", bucketName, key);
        throw;
      }
    }

    // --- Upload ODC REST -> S3 (single PUT) --- Keep for smaller files ---
    public string UploadFromRestToPresignedUrl(
      string sourceUrl,
      string binGuid,
      string authHeaderName,
      string authHeaderValue,
      string presignedPutUrl,
      string contentType,
      int timeoutSeconds)
    {
      try
      {
        if (string.IsNullOrWhiteSpace(sourceUrl)) throw new ArgumentException("sourceUrl is required.");
        if (string.IsNullOrWhiteSpace(binGuid)) throw new ArgumentException("binGuid is required.");
        if (string.IsNullOrWhiteSpace(presignedPutUrl)) throw new ArgumentException("presignedPutUrl is required.");
        if (timeoutSeconds <= 0) timeoutSeconds = 300;

        _logger.LogInformation("Uploading from REST source host {SourceHost} to pre-signed S3 host {TargetHost}", SafeHost(sourceUrl), SafeHost(presignedPutUrl));

        var http = GetHttpClient(timeoutSeconds);

        var totalLength = TryProbeLength(http, sourceUrl, binGuid, authHeaderName, authHeaderValue);

        var resolvedSourceUrl = AppendQueryParameter(sourceUrl, "binGuid", binGuid);
        if (totalLength.HasValue && totalLength.Value > 0)
        {
          resolvedSourceUrl = AppendQueryParameter(
                                 AppendQueryParameter(resolvedSourceUrl, "offset", "0"),
                                 "length", totalLength.Value.ToString());
        }

        using var getReq = new HttpRequestMessage(HttpMethod.Get, resolvedSourceUrl);
        getReq.Headers.AcceptEncoding.Clear();
        getReq.Headers.AcceptEncoding.ParseAdd("identity");

        if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
          getReq.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

        using var getResp = http.Send(getReq, HttpCompletionOption.ResponseHeadersRead);
        getResp.EnsureSuccessStatusCode();

        var srcStream = getResp.Content.ReadAsStream();
        var upstreamLength = getResp.Content.Headers.ContentLength;

        HttpContent putContent;
        var effectiveLength = upstreamLength ?? totalLength;
        string? tempFilePath = null;
        FileStream? tempFileStream = null;

        if (effectiveLength.HasValue)
        {
          putContent = new StreamContent(srcStream);
          putContent.Headers.ContentLength = effectiveLength.Value;
        }
        else
        {
          // When Content-Length is missing, buffer to a temp file to avoid unbounded memory usage.
          _logger.LogInformation("Buffering source stream to temporary file to determine Content-Length.");
          tempFilePath = Path.Combine(Path.GetTempPath(), $"s3orch-presign-{Guid.NewGuid():N}.bin");
          var bufferedLength = BufferToTempFile(srcStream, tempFilePath);
          srcStream.Dispose();
          tempFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
          putContent = new StreamContent(tempFileStream);
          putContent.Headers.ContentLength = bufferedLength;
        }

        using var putReq = new HttpRequestMessage(HttpMethod.Put, presignedPutUrl) { Content = putContent };
        if (!string.IsNullOrWhiteSpace(contentType))
          putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        putReq.Headers.ExpectContinue = false;

        try
        {
          using var putResp = http.Send(putReq, HttpCompletionOption.ResponseHeadersRead);
          putResp.EnsureSuccessStatusCode();

          var etag = putResp.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
          _logger.LogInformation("Upload to pre-signed S3 completed with ETag {ETag}", etag);
          return etag;
        }
        finally
        {
          if (tempFileStream != null)
          {
            tempFileStream.Dispose();
          }
          if (!string.IsNullOrWhiteSpace(tempFilePath))
          {
            try { File.Delete(tempFilePath); } catch { }
          }
        }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed upload from REST source host {SourceHost} to pre-signed S3 host {TargetHost}", SafeHost(sourceUrl), SafeHost(presignedPutUrl));
        throw;
      }
    }
    private static readonly HttpStatusCode[] TransientStatus =
      { HttpStatusCode.Forbidden, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout };

    // --- S3 -> ODC REST (chunked; large files) ---
    public DownloadToRestResult DownloadFromPresignedUrlToRest(
      string presignedGetUrl,
      string targetUrl,
      string s3ObjectKey,
      string bucketName,
      string targetAuthHeaderName,
      string targetAuthHeaderValue,
      string targetContentType,
      int chunkSizeBytes,
      int timeoutSeconds)
    {
      var stopwatch = System.Diagnostics.Stopwatch.StartNew();
      HttpResponseMessage? getResp = null;
      Stream? srcStream = null;
      string? tempFilePath = null;
      FileStream? tempFileStream = null;
      try
      {
        if (string.IsNullOrWhiteSpace(presignedGetUrl)) throw new ArgumentException("presignedGetUrl is required.");
        if (string.IsNullOrWhiteSpace(targetUrl)) throw new ArgumentException("targetUrl is required.");
        if (string.IsNullOrWhiteSpace(s3ObjectKey)) throw new ArgumentException("s3ObjectKey is required.");
        if (string.IsNullOrWhiteSpace(bucketName)) throw new ArgumentException("bucketName is required.");

        _logger.LogInformation("Downloading from S3 host {SourceHost} to target host {TargetHost}", SafeHost(presignedGetUrl), SafeHost(targetUrl));

        // Keep headroom under the ~30MB gateway limit
        const int maxChunkSize = 25_000_000;
        if (chunkSizeBytes <= 0 || chunkSizeBytes > maxChunkSize) chunkSizeBytes = maxChunkSize; // default to 25 MB
        if (timeoutSeconds <= 0) timeoutSeconds = 120;

        var http = GetHttpClient(timeoutSeconds);

        // A) Determine total length up-front so we ALWAYS send X-Chunk-Total
        long? contentLength = TryGetContentLength(http, presignedGetUrl);
        if (!contentLength.HasValue)
        {
          _logger.LogWarning("Source length unavailable via HEAD/Range for host {SourceHost}; probing GET headers", SafeHost(presignedGetUrl));
          getResp = OpenGetResponse(http, presignedGetUrl);
          getResp.EnsureSuccessStatusCode();
          contentLength = getResp.Content.Headers.ContentLength;

          if (!contentLength.HasValue)
          {
            _logger.LogWarning("Source length missing from GET headers for host {SourceHost}; buffering to temporary file", SafeHost(presignedGetUrl));
            tempFilePath = Path.GetTempFileName();
            using (var responseStream = getResp.Content.ReadAsStream())
            {
              contentLength = BufferToTempFile(responseStream, tempFilePath);
            }
            getResp.Dispose();
            getResp = null;
            tempFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            srcStream = tempFileStream;
          }
          else
          {
            srcStream = getResp.Content.ReadAsStream();
          }
        }

        if (!contentLength.HasValue)
          throw new InvalidOperationException("Cannot determine source length (HEAD/Range/GET failed).");

        int totalChunks = (int)((contentLength.Value + (chunkSizeBytes - 1)) / chunkSizeBytes);
        _logger.LogInformation("Source length {ContentLength} bytes, chunk size {ChunkSize} bytes, total chunks {TotalChunks}", contentLength.Value, chunkSizeBytes, totalChunks);

        // Open streaming GET from S3 (only if we didn't already open it above)
        if (srcStream == null)
        {
          getResp = OpenGetResponse(http, presignedGetUrl);
          getResp.EnsureSuccessStatusCode();
          srcStream = getResp.Content.ReadAsStream();
        }
        // B) Force a single content-type for all chunks
        var forcedContentType = string.IsNullOrWhiteSpace(targetContentType)
          ? "application/octet-stream"
          : targetContentType;

        var targetWithKey = AppendQueryParameter(
                              AppendQueryParameter(targetUrl, "Key", s3ObjectKey),
                              "BucketName", bucketName);

        var uploadId = Guid.NewGuid().ToString("N");
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
        try
        {
          long totalRead = 0;
          int index = 0;

          DownloadToRestResult finalResult = new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "" };

          for (; ; index++)
          {
            int read = ReadFull(srcStream, buffer, 0, chunkSizeBytes);
            if (read <= 0)
            {
              if (index == 0)
              {
                _logger.LogWarning("Source stream is empty for key {Key}", s3ObjectKey);
                return new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "Source stream is empty." };
              }
              break;
            }

            totalRead += read;
            bool isLast = (index == totalChunks - 1) || (totalRead >= contentLength.Value);

            _logger.LogDebug("Posting chunk {ChunkIndex}/{TotalChunks} (bytes read {BytesRead})", index + 1, totalChunks, read);

            DownloadToRestResult? attemptResult = null;

            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
              using var content = new ByteArrayContent(buffer, 0, read);
              content.Headers.ContentType = new MediaTypeHeaderValue(forcedContentType);
              content.Headers.ContentLength = read;
              using var postReq = new HttpRequestMessage(HttpMethod.Post, targetWithKey) { Content = content };
              // Chunk headers for assembly (server is 0-based)
              postReq.Headers.TryAddWithoutValidation("X-Upload-Id", uploadId);
              postReq.Headers.TryAddWithoutValidation("X-Chunk-Index", index.ToString());
              postReq.Headers.TryAddWithoutValidation("X-Chunk-Index-Base", "0");
              postReq.Headers.TryAddWithoutValidation("X-Chunk-Total", totalChunks.ToString());
              postReq.Headers.TryAddWithoutValidation("X-Last-Chunk", isLast ? "true" : "false");

              if (!string.IsNullOrWhiteSpace(targetAuthHeaderName) && !string.IsNullOrWhiteSpace(targetAuthHeaderValue))
                postReq.Headers.TryAddWithoutValidation(targetAuthHeaderName, targetAuthHeaderValue);

              postReq.Headers.ExpectContinue = false;

              using var postResp = http.Send(postReq, HttpCompletionOption.ResponseHeadersRead);

              if (!postResp.IsSuccessStatusCode && TransientStatus.Contains(postResp.StatusCode) && attempt < maxAttempts)
              {
                _logger.LogWarning("Transient status {StatusCode} on chunk {ChunkIndex}, attempt {Attempt}/{MaxAttempts}", postResp.StatusCode, index + 1, attempt, maxAttempts);
                System.Threading.Thread.Sleep(200 * attempt);
                continue;
              }

              if (isLast)
              {
                var successHeader = GetHeaderValue(postResp, "success");
                var errorHeader = GetHeaderValue(postResp, "errorMessage");
                var body = postResp.Content != null ? Sync(postResp.Content.ReadAsStringAsync()) : string.Empty;
                var binGuid = ExtractBinGuidFromBody(body);

                bool success = ParseBool(successHeader ?? string.Empty) && postResp.IsSuccessStatusCode;
                string errorMsg = errorHeader ?? (success ? "" : $"HTTP {(int)postResp.StatusCode} {postResp.ReasonPhrase}");

                attemptResult = new DownloadToRestResult { BinGuid = binGuid, Success = success, ErrorMessage = errorMsg };
              }
              else
              {
                if (!postResp.IsSuccessStatusCode)
                {
                  var msg = postResp.Content != null ? Sync(postResp.Content.ReadAsStringAsync()) : $"HTTP {(int)postResp.StatusCode} {postResp.ReasonPhrase}";
                  attemptResult = new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = msg };
                }
                else
                {
                  attemptResult = new DownloadToRestResult { BinGuid = "", Success = true, ErrorMessage = "" };
                }
              }

              break;
            }

            if (attemptResult == null)
            {
              _logger.LogError("Unknown error sending chunk {ChunkIndex} for key {Key}", index + 1, s3ObjectKey);
              return new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "Unknown error sending chunk." };
            }

            if (!attemptResult.Value.Success)
            {
              _logger.LogError("Failed sending chunk {ChunkIndex} for key {Key}. Error: {Error}", index + 1, s3ObjectKey, attemptResult.Value.ErrorMessage);
              return attemptResult.Value;
            }

            if (isLast)
            {
              finalResult = attemptResult.Value;
              _logger.LogInformation("Download to REST completed in {ElapsedMs} ms, BinGuid {BinGuid}", stopwatch.ElapsedMilliseconds, finalResult.BinGuid);
              return finalResult;
            }
          }

          _logger.LogError("Unexpected end of stream without final chunk for key {Key}", s3ObjectKey);
          return new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "Unexpected end of stream without final chunk." };
        }
        finally
        {
          ArrayPool<byte>.Shared.Return(buffer);
        }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed download from S3 host {SourceHost} to target host {TargetHost}", SafeHost(presignedGetUrl), SafeHost(targetUrl));
        throw;
      }
      finally
      {
        try { getResp?.Dispose(); } catch { }
        if (tempFileStream != null) tempFileStream.Dispose();
        if (!string.IsNullOrWhiteSpace(tempFilePath))
        {
          try { File.Delete(tempFilePath); } catch { }
        }
      }
    }

    // ---------- NEW: ODC REST (chunked GETs) -> S3 MULTIPART ----------
    public UploadToS3Result UploadFromRestToS3Multipart(
      S3AuthInfo authInfo,
      string bucketName,
      string key,
      string contentType,
      string sourceUrl,
      string binGuid,
      string authHeaderName,
      string authHeaderValue,
      int chunkSizeBytes,
      int timeoutSeconds)
    {
      var stopwatch = System.Diagnostics.Stopwatch.StartNew();
      if (string.IsNullOrWhiteSpace(bucketName)) throw new ArgumentException("bucketName is required.");
      if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required.");
      if (string.IsNullOrWhiteSpace(sourceUrl)) throw new ArgumentException("sourceUrl is required.");
      if (string.IsNullOrWhiteSpace(binGuid)) throw new ArgumentException("binGuid is required.");
      if (chunkSizeBytes < 5 * 1024 * 1024) chunkSizeBytes = 8 * 1024 * 1024; // S3 requires >= 5MB per part (except last)
      if (timeoutSeconds <= 0) timeoutSeconds = 120;

      _logger.LogInformation("Starting multipart upload to bucket {Bucket} and key {Key} from REST source host {SourceHost}", bucketName, key, SafeHost(sourceUrl));

      // Disable transparent decompression to ensure exact byte-for-byte stream consistency
      using var s3 = CreateClient(authInfo);
      var http = GetHttpClient(timeoutSeconds);

      // Discover total size (so we know how many parts)
      var length = TryProbeLength(http, sourceUrl, binGuid, authHeaderName, authHeaderValue);
      if (!length.HasValue)
      {
        _logger.LogWarning("Source endpoint did not return total length.");
        return new UploadToS3Result { BinGuid = binGuid, Success = false, ErrorMessage = "Source endpoint must provide total length (HEAD or 1-byte probe failed)." };
      }

      long totalLength = length.Value;
      if (totalLength <= 0)
      {
        _logger.LogWarning("Source stream is empty for binGuid {BinGuid}", binGuid);
        return new UploadToS3Result { BinGuid = binGuid, Success = false, ErrorMessage = "Source stream is empty." };
      }

      int totalParts = (int)((totalLength + (long)chunkSizeBytes - 1) / chunkSizeBytes);
      if (totalParts > 10_000)
      {
        _logger.LogError("Computed part count {PartCount} exceeds S3 multipart limit.", totalParts);
        return new UploadToS3Result { BinGuid = binGuid, Success = false, ErrorMessage = "Computed part count exceeds S3 multipart limit (10,000). Increase chunk size." };
      }

      _logger.LogInformation("Multipart upload plan: total length {TotalLength} bytes, chunk size {ChunkSize} bytes, parts {TotalParts}", totalLength, chunkSizeBytes, totalParts);

      var buffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
      string uploadId = string.Empty;
      var partETags = new System.Collections.Generic.List<PartETag>(capacity: totalParts);
      try
      {
        // Initiate multipart
        var initiate = Sync(s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
          BucketName = bucketName,
          Key = key,
          ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
        }));

        uploadId = initiate.UploadId;

        const int maxAttempts = 3;
        long offset = 0;
        for (int partNumber = 1; partNumber <= totalParts; partNumber++)
        {
          int thisLen = (int)Math.Min(chunkSizeBytes, totalLength - offset);
          _logger.LogDebug("Uploading part {PartNumber}/{TotalParts} (offset {Offset}, length {Length})", partNumber, totalParts, offset, thisLen);

          Exception? lastError = null;
          bool uploaded = false;

          for (int attempt = 1; attempt <= maxAttempts; attempt++)
          {
            try
            {
              // GET slice from ODC REST (?binGuid=&offset=&length=)
              var sliceUrl = AppendQueryParameter(
                               AppendQueryParameter(
                                 AppendQueryParameter(sourceUrl, "binGuid", binGuid),
                                 "offset", offset.ToString()),
                               "length", thisLen.ToString());

              using var getReq = new HttpRequestMessage(HttpMethod.Get, sliceUrl);
              getReq.Headers.AcceptEncoding.Clear();
              getReq.Headers.AcceptEncoding.ParseAdd("identity");
              if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
                getReq.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

              using var getResp = http.Send(getReq, HttpCompletionOption.ResponseHeadersRead);
              if (!getResp.IsSuccessStatusCode)
              {
                if (IsTransientStatusCode(getResp.StatusCode) && attempt < maxAttempts)
                {
                  _logger.LogWarning("Transient status {StatusCode} fetching part {PartNumber}, attempt {Attempt}/{MaxAttempts}", getResp.StatusCode, partNumber, attempt, maxAttempts);
                  DelayForRetry(attempt);
                  continue;
                }
                getResp.EnsureSuccessStatusCode();
              }

              using var src = getResp.Content.ReadAsStream();
              // Read exactly thisLen bytes
              int read = 0;
              while (read < thisLen)
              {
                int n = src.Read(buffer, read, thisLen - read);
                if (n <= 0) throw new EndOfStreamException($"Unexpected EOF at offset {offset}, expected {thisLen - read} more bytes.");
                read += n;
              }

              // Upload part
              using var ms = new MemoryStream(buffer, 0, read, writable: false);
              var uploadPartResponse = Sync(s3.UploadPartAsync(new UploadPartRequest
              {
                BucketName = bucketName,
                Key = key,
                UploadId = uploadId,
                PartNumber = partNumber,
                PartSize = read,
                InputStream = ms
              }));

              partETags.Add(new PartETag(partNumber, uploadPartResponse.ETag));
              offset += read;
              uploaded = true;
              break;
            }
            catch (AmazonS3Exception ex) when (IsTransientS3Exception(ex) && attempt < maxAttempts)
            {
              lastError = ex;
              _logger.LogWarning(ex, "Transient S3 error uploading part {PartNumber}, attempt {Attempt}/{MaxAttempts}", partNumber, attempt, maxAttempts);
              DelayForRetry(attempt);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
              lastError = ex;
              _logger.LogWarning(ex, "Transient HTTP error uploading part {PartNumber}, attempt {Attempt}/{MaxAttempts}", partNumber, attempt, maxAttempts);
              DelayForRetry(attempt);
            }
            catch (TaskCanceledException ex) when (attempt < maxAttempts)
            {
              lastError = ex;
              _logger.LogWarning(ex, "Timeout uploading part {PartNumber}, attempt {Attempt}/{MaxAttempts}", partNumber, attempt, maxAttempts);
              DelayForRetry(attempt);
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
              lastError = ex;
              _logger.LogWarning(ex, "I/O error uploading part {PartNumber}, attempt {Attempt}/{MaxAttempts}", partNumber, attempt, maxAttempts);
              DelayForRetry(attempt);
            }
          }

          if (!uploaded)
          {
            throw new InvalidOperationException($"Failed to upload part {partNumber} after {maxAttempts} attempts.", lastError);
          }
        }

        // Complete multipart
        var complete = new CompleteMultipartUploadRequest
        {
          BucketName = bucketName,
          Key = key,
          UploadId = uploadId
        };
        if (partETags.Count != totalParts)
          return new UploadToS3Result { BinGuid = binGuid, Success = false, ErrorMessage = "Uploaded part count mismatch." };

        if (partETags.Count == 0)
          return new UploadToS3Result { BinGuid = binGuid, Success = false, ErrorMessage = "No parts uploaded." };
        complete.AddPartETags(partETags);

        Sync(s3.CompleteMultipartUploadAsync(complete));
        _logger.LogInformation("Multipart upload completed in {ElapsedMs} ms for bucket {Bucket} and key {Key}", stopwatch.ElapsedMilliseconds, bucketName, key);
        // Successful completion means the object is committed in S3; we return a success status with the original binGuid reference.
        return new UploadToS3Result { BinGuid = binGuid, Success = true, ErrorMessage = string.Empty };
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Multipart upload failed for bucket {Bucket} and key {Key}", bucketName, key);
        if (!string.IsNullOrEmpty(uploadId))
        {
          try { Sync(s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucketName, Key = key, UploadId = uploadId })); }
          catch { /* best effort */ }
        }
        return new UploadToS3Result { BinGuid = binGuid, Success = false, ErrorMessage = ex.Message };
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }

    public bool RenameObject(S3AuthInfo authInfo, string bucketName, string currentKey, string newKey, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Renaming object in bucket {Bucket} from {CurrentKey} to {NewKey}", bucketName, currentKey, newKey);
        Validate(authInfo, bucketName, currentKey, 1); // Duration not used but validates other fields
        ValidateS3Key(currentKey);
        ValidateS3Key(newKey);

        using var s3 = CreateClient(authInfo);

        // 1. Copy
        var copyRequest = new CopyObjectRequest
        {
          SourceBucket = bucketName,
          SourceKey = currentKey,
          DestinationBucket = bucketName,
          DestinationKey = newKey
        };
        Sync(s3.CopyObjectAsync(copyRequest));

        // 2. Delete original
        var deleteRequest = new DeleteObjectRequest
        {
          BucketName = bucketName,
          Key = currentKey
        };
        Sync(s3.DeleteObjectAsync(deleteRequest));

        _logger.LogInformation("Renamed object in bucket {Bucket} from {CurrentKey} to {NewKey}", bucketName, currentKey, newKey);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to rename object in bucket {Bucket} from {CurrentKey} to {NewKey}", bucketName, currentKey, newKey);
        errormessage = ex.Message;
        return false;
      }
    }

    public bool RenameFile(S3AuthInfo authInfo, string bucketName, string currentKey, string newFileName, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Renaming file in bucket {Bucket} for key {CurrentKey} to new file name {NewFileName}", bucketName, currentKey, newFileName);
        if (string.IsNullOrWhiteSpace(currentKey)) throw new ArgumentException("currentKey is required.");
        if (string.IsNullOrWhiteSpace(newFileName)) throw new ArgumentException("newFileName is required.");
        ValidateS3Key(currentKey);
        // newFileName is not a full key yet, but we should validate it's not empty/weird
        if (newFileName.Contains("/") || newFileName.Contains("\\")) throw new ArgumentException("newFileName cannot contain path separators.");

        // Extract directory from currentKey
        string directory = "";
        int lastSlash = currentKey.LastIndexOf('/');
        if (lastSlash >= 0)
        {
          directory = currentKey.Substring(0, lastSlash + 1);
        }

        string newKey = directory + newFileName;
        return RenameObject(authInfo, bucketName, currentKey, newKey, out errormessage);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to rename file in bucket {Bucket} for key {CurrentKey} to new file name {NewFileName}", bucketName, currentKey, newFileName);
        errormessage = ex.Message;
        return false;
      }
    }

    public bool DeleteFile(S3AuthInfo authInfo, string bucketName, string key, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Deleting file in bucket {Bucket} with key {Key}", bucketName, key);
        Validate(authInfo, bucketName, key, 1);
        ValidateS3Key(key);

        using var s3 = CreateClient(authInfo);
        var deleteRequest = new DeleteObjectRequest
        {
          BucketName = bucketName,
          Key = key
        };
        Sync(s3.DeleteObjectAsync(deleteRequest));
        _logger.LogInformation("Deleted file in bucket {Bucket} with key {Key}", bucketName, key);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to delete file in bucket {Bucket} with key {Key}", bucketName, key);
        errormessage = ex.Message;
        return false;
      }
    }

    public bool MoveFile(S3AuthInfo authInfo, string bucketName, string sourceKey, string targetDirectory, out string errormessage)
    {
      errormessage = string.Empty;
      try
      {
        _logger.LogInformation("Moving file in bucket {Bucket} from {SourceKey} to directory {TargetDirectory}", bucketName, sourceKey, targetDirectory);
        if (string.IsNullOrWhiteSpace(sourceKey)) throw new ArgumentException("sourceKey is required.");
        ValidateS3Key(sourceKey);

        // targetDirectory can be empty (root), but if not empty, ensure it ends with /
        if (!string.IsNullOrEmpty(targetDirectory) && !targetDirectory.EndsWith("/"))
        {
          targetDirectory += "/";
        }

        // Extract filename from sourceKey
        string fileName = Path.GetFileName(sourceKey);
        if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("Could not determine filename from sourceKey.");

        string newKey = targetDirectory + fileName;
        return RenameObject(authInfo, bucketName, sourceKey, newKey, out errormessage);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to move file in bucket {Bucket} from {SourceKey} to directory {TargetDirectory}", bucketName, sourceKey, targetDirectory);
        errormessage = ex.Message;
        return false;
      }
    }

    // ===== Helpers =====

    // Try to obtain Content-Length via HEAD; if missing, use 1-byte ranged GET and parse Content-Range.
    private static long? TryGetContentLength(HttpClient http, string url)
    {
      // Attempt HEAD
      try
      {
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = http.Send(head);
        if (resp.IsSuccessStatusCode)
        {
          var len = resp.Content.Headers.ContentLength;
          if (len.HasValue) return len.Value;

          // Additional fallback: sometimes Content-Length is sent as a header, not in Content.Headers
          if (resp.Headers.TryGetValues("Content-Length", out var hVals) &&
              long.TryParse(hVals.FirstOrDefault(), out var headerLen))
            return headerLen;
        }
      }
      catch { /* ignore and fall back */ }

      // Fallback: GET with Range: bytes=0-0 to get Content-Range: bytes 0-0/12345
      try
      {
        using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.AcceptEncoding.Clear();
        ranged.Headers.AcceptEncoding.ParseAdd("identity");
        ranged.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = http.Send(ranged, HttpCompletionOption.ResponseHeadersRead);
        if ((int)resp.StatusCode == 206) // Partial Content
        {
          var cr = resp.Content.Headers.ContentRange; // may be null depending on handler; handle manually if needed
          if (cr != null && cr.Length.HasValue) return cr.Length.Value;

          // Manual parse as a fallback
          if (resp.Content.Headers.TryGetValues("Content-Range", out var values))
          {
            var val = values.FirstOrDefault(); // e.g., "bytes 0-0/12345"
            if (!string.IsNullOrEmpty(val))
            {
              var slash = val.LastIndexOf('/');
              if (slash > 0 && long.TryParse(val.Substring(slash + 1), out var total))
                return total;
            }
          }
        }
        else if (resp.IsSuccessStatusCode)
        {
          var len = resp.Content.Headers.ContentLength;
          if (len.HasValue) return len.Value;
          if (resp.Headers.TryGetValues("Content-Length", out var hVals) &&
              long.TryParse(hVals.FirstOrDefault(), out var headerLen))
            return headerLen;
        }
        System.Diagnostics.Debug.WriteLine($"[Probe] Status={(int)resp.StatusCode} CR={resp.Content.Headers.ContentRange} CL={resp.Content.Headers.ContentLength}");
      }

      catch { /* ignore */ }

      return null;
    }

    private static HttpResponseMessage OpenGetResponse(HttpClient http, string url)
    {
      var getReq = new HttpRequestMessage(HttpMethod.Get, url);
      getReq.Headers.AcceptEncoding.Clear();
      getReq.Headers.AcceptEncoding.ParseAdd("identity");
      return http.Send(getReq, HttpCompletionOption.ResponseHeadersRead);
    }

    private static long? TryProbeLength(HttpClient http, string sourceUrl, string binGuid, string authHeaderName, string authHeaderValue)
    {
      // HEAD first
      try
      {
        var headUrl = AppendQueryParameter(sourceUrl, "binGuid", binGuid);
        using var head = new HttpRequestMessage(HttpMethod.Head, headUrl);
        if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
          head.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

        using var resp = http.Send(head);
        if (resp.IsSuccessStatusCode)
        {
          var len = resp.Content.Headers.ContentLength;
          if (len.HasValue) return len.Value;
        }
      }
      catch { }

      // Fallback 1-byte probe (?offset=0&length=1) expecting either X-Total-Length or Content-Range headers
      try
      {
        var probeUrl = AppendQueryParameter(
                         AppendQueryParameter(
                           AppendQueryParameter(sourceUrl, "binGuid", binGuid),
                           "offset", "0"),
                         "length", "1");

        using var get = new HttpRequestMessage(HttpMethod.Get, probeUrl);
        get.Headers.AcceptEncoding.Clear();
        get.Headers.AcceptEncoding.ParseAdd("identity");
        if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
          get.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

        using var resp = http.Send(get, HttpCompletionOption.ResponseHeadersRead);
        if (resp.IsSuccessStatusCode)
        {
          if (resp.Headers.TryGetValues("X-Total-Length", out var vals))
            if (long.TryParse(vals.FirstOrDefault(), out var total))
              return total;

          var cr = resp.Content?.Headers?.ContentRange;
          if (cr != null)
          {
            if (cr.Length.HasValue) return cr.Length.Value;
            // ContentRangeHeaderValue.ToString() follows "bytes start-end/total"
            var crText = cr.ToString();
            if (!string.IsNullOrEmpty(crText))
            {
              var slash = crText.LastIndexOf('/');
              if (slash > 0 && long.TryParse(crText.Substring(slash + 1), out var totalFromText))
                return totalFromText;
            }
          }

          // Some handlers may expose Content-Range as a raw header
          if (resp.Content?.Headers?.TryGetValues("Content-Range", out var rawRanges) == true ||
              resp.Headers.TryGetValues("Content-Range", out rawRanges))
          {
            var raw = rawRanges.FirstOrDefault();
            if (!string.IsNullOrEmpty(raw))
            {
              var slash = raw.LastIndexOf('/');
              if (slash > 0 && long.TryParse(raw.Substring(slash + 1), out var totalFromRaw))
                return totalFromRaw;
            }
          }
        }
      }
      catch { }

      return null;
    }

    private static void Sync(Task task)
    {
      task.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private static T Sync<T>(Task<T> task)
    {
      return task.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
      return statusCode == HttpStatusCode.InternalServerError
          || statusCode == HttpStatusCode.BadGateway
          || statusCode == HttpStatusCode.ServiceUnavailable
          || statusCode == HttpStatusCode.GatewayTimeout
          || statusCode == HttpStatusCode.TooManyRequests
          || statusCode == HttpStatusCode.Forbidden;
    }

    private static bool IsTransientS3Exception(AmazonS3Exception ex)
    {
      if (ex == null) return false;
      if (string.Equals(ex.ErrorCode, "SlowDown", StringComparison.OrdinalIgnoreCase)) return true;
      return IsTransientStatusCode(ex.StatusCode);
    }

    private static void DelayForRetry(int attempt)
    {
      System.Threading.Thread.Sleep(200 * attempt);
    }

    private static long BufferToTempFile(Stream src, string path)
    {
      const int bufferSize = 1024 * 1024; // 1 MB buffer to keep memory bounded
      var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
      try
      {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        long total = 0;
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
          file.Write(buffer, 0, read);
          total += read;
        }
        return total;
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }

    // Read up to count bytes; returns actual read (0 = EOF)
    private static int ReadFull(Stream s, byte[] buffer, int offset, int count)
    {
      int total = 0;
      while (total < count)
      {
        int n = s.Read(buffer, offset + total, count - total);
        if (n <= 0) break;
        total += n;
      }
      return total;
    }

    private static string? GetHeaderValue(HttpResponseMessage resp, string headerName)
    {
      if (resp.Headers.TryGetValues(headerName, out var values))
        return values.FirstOrDefault();
      if (resp.Content?.Headers != null && resp.Content.Headers.TryGetValues(headerName, out var v2))
        return v2.FirstOrDefault();
      return null;
    }

    private static bool ParseBool(string value)
    {
      if (string.IsNullOrWhiteSpace(value)) return false;
      return value.Equals("true", StringComparison.OrdinalIgnoreCase)
          || value.Equals("1")
          || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractBinGuidFromBody(string body)
    {
      if (string.IsNullOrWhiteSpace(body)) return string.Empty;
      var trimmed = body.Trim().Trim('"');

      if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
      {
        try
        {
          using var doc = JsonDocument.Parse(trimmed);
          var root = doc.RootElement;
          if (root.TryGetProperty("binGUID", out var v)) return v.GetString() ?? string.Empty;
          if (root.TryGetProperty("binguid", out var v2)) return v2.GetString() ?? string.Empty;
        }
        catch { /* fall back to plain text */ }
      }
      return trimmed;
    }

    private static string AppendQueryParameter(string url, string name, string value)
    {
      if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL cannot be empty.", nameof(url));
      var trimmed = url.Trim();
      var hasQuery = trimmed.Contains('?');
      var needsAmpersand = hasQuery && !trimmed.EndsWith("?") && !trimmed.EndsWith("&");
      var prefix = hasQuery ? (needsAmpersand ? "&" : string.Empty) : "?";
      return $"{trimmed}{prefix}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value ?? string.Empty)}";
    }

    private static void Validate(S3AuthInfo auth, string bucket, string key, int mins)
    {
      if (string.IsNullOrWhiteSpace(auth.AccessKeyId)) throw new ArgumentException("AccessKeyId is required.");
      if (string.IsNullOrWhiteSpace(auth.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
      if (string.IsNullOrWhiteSpace(auth.Region)) throw new ArgumentException("Region is required.");
      if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("bucketName is required.");
      if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required.");
      if (mins <= 0 || mins > 10080) throw new ArgumentOutOfRangeException(nameof(mins), "durationInMinutes must be 1–10080.");
    }

    private static void ValidateS3Key(string key)
    {
      if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("S3 Key cannot be null or empty.");
      if (System.Text.Encoding.UTF8.GetByteCount(key) > 1024) throw new ArgumentException("S3 Key is too long (max 1024 bytes).");
    }

    private static AmazonS3Client CreateClient(S3AuthInfo auth)
    {
      var region = RegionEndpoint.GetBySystemName(auth.Region);

      // Force a stable, region-scoped endpoint and avoid host-prefix injection
      // so the SDK does not transform the hostname into environment/VPC-specific
      // forms like s3.<random>.amazonaws.com which may be blocked by egress rules.
      var cfg = new AmazonS3Config
      {
        RegionEndpoint = region,
        // Explicit service URL keeps the hostname stable (s3.{region}.amazonaws.com)
        ServiceURL = $"https://s3.{auth.Region}.amazonaws.com",
        ForcePathStyle = true,                 // use path-style: https://s3.{region}.amazonaws.com/bucket/key
        DisableHostPrefixInjection = true,     // avoid bucket-name host prefixing
        UseDualstackEndpoint = false,          // keep IPv4-only to avoid unexpected AAAA resolution issues
        DisableMultiregionAccessPoints = true  // prevent MRAP lookups that may pick non-standard hostnames
      };

      return new AmazonS3Client(auth.AccessKeyId, auth.SecretAccessKey, cfg);
    }

    private static string NormalizeBucketRegion(S3Region location)
    {
      if (location == S3Region.USEast1) return "us-east-1";
      var raw = location?.Value;
      if (string.IsNullOrWhiteSpace(raw)) return "us-east-1";
      if (string.Equals(raw, "EU", StringComparison.OrdinalIgnoreCase)) return "eu-west-1";
      if (string.Equals(raw, "US", StringComparison.OrdinalIgnoreCase)) return "us-east-1";
      return raw;
    }

    private static string SafeHost(string url)
    {
      if (string.IsNullOrWhiteSpace(url)) return "unknown";
      try
      {
        return new Uri(url).Host;
      }
      catch
      {
        return "invalid-url";
      }
    }
  }
}
