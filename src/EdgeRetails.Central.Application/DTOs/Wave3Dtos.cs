namespace EdgeRetails.Central.Application.DTOs;

public class UploadInitDto
{
    public Guid ShopId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid ObjectId { get; set; }
    public string Purpose { get; set; } = "ENCRYPTED_BACKUP";
    public long ContentLength { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public string CiphertextSha256 { get; set; } = string.Empty;
    public string? LocalManifestAuthenticationOpaque { get; set; }
    public Guid RetentionPolicyId { get; set; }
}

public class UploadSessionResponseDto
{
    public Guid UploadId { get; set; }
    public Guid AuthorizedObjectId { get; set; }
    public long MaxBytes { get; set; }
    public long AcceptedOffset { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string ChunkRoute { get; set; } = string.Empty;
    public string FinalizeRoute { get; set; } = string.Empty;
}

public class UploadFinalizeDto
{
    public Guid UploadId { get; set; }
    public Guid ObjectId { get; set; }
    public long ExpectedLength { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string? CiphertextBase64 { get; set; }
}

public class ObjectActionDto
{
    public Guid ObjectId { get; set; }
    public string Purpose { get; set; } = "ENCRYPTED_BACKUP";
    public Guid? RetentionPolicyId { get; set; }
    public Guid? RestoreAuthorizationId { get; set; }
}

public class ObjectDownloadGrantResponseDto
{
    public Guid GrantId { get; set; }
    public string DownloadToken { get; set; } = string.Empty;
    public string ContentRoute { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public class TicketDto
{
    public Guid ShopId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<Guid> AttachmentObjectIds { get; set; } = new();
    public int RedactionProfileVersion { get; set; } = 1;
}

public class TicketActionDto
{
    public Guid TicketId { get; set; }
    public string Action { get; set; } = "REPLY";
    public string? ReplyText { get; set; }
    public string? Status { get; set; }
}

