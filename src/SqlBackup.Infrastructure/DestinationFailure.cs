using Amazon.S3;
using Azure;

namespace SqlBackup.Infrastructure;

public static class DestinationFailure
{
    public static bool IsTransient(Exception exception)
    {
        return exception switch
        {
            RequestFailedException azure => azure.Status is 408 or 429 or >= 500,
            AmazonS3Exception s3 => (int)s3.StatusCode is 408 or 429 or >= 500,
            IOException => true,
            HttpRequestException => true,
            TimeoutException => true,
            _ => false
        };
    }

    public static string Describe(Exception exception)
    {
        string message = "Transfer failed. Check destination credentials, permissions, network access, capacity and any existing object. The source backup is retained.";
        if (exception.Data.Contains("MultipartCleanupFailed"))
        {
            message += " Incomplete S3 multipart cleanup could not be confirmed; inspect the bucket's incomplete uploads.";
        }

        return message;
    }
}
