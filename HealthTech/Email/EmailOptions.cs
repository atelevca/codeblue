namespace HealthTech.Email;

/// <summary>
/// Local SMTP only (Mailpit on this machine): no TLS, no authentication. Pointing Host at an
/// external server would be an external call and break the offline requirement.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string FromAddress { get; set; } = "no-reply@healthtech.local";
    public string FromName { get; set; } = "HealthTech";
    public int TimeoutSeconds { get; set; } = 15;
}
