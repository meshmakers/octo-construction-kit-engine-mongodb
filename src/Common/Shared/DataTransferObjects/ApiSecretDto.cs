using System;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class ApiSecretDto
{
    public string ValueEncrypted { get; set; } = null!;
    public string? ValueClearText { get; set; }
    
    public DateTime? ExpirationDate { get; set; }
    public string? Description { get; set; }
}
