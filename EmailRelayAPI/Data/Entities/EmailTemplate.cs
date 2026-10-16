using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

/// <summary>Reusable message template with {{placeholders}}. ClientId null = shared by all clients.</summary>
public class EmailTemplate
{
    public int Id { get; set; }
    public int? ClientId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(998)]
    public string Subject { get; set; } = string.Empty;
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? CreatedBy { get; set; }
}
