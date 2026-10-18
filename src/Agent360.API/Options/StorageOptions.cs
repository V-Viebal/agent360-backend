namespace Agent360.API.Options;

public class StorageOptions
{
    public const string StorageSection = "Storage";

    public string Provider { get; set; } = "Local"; // Local | Minio | S3
    public string ServerUrl { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = "agent360-production";
    public bool UseSsl { get; set; } = true;
}
