using SharedKernel;

namespace App.Core.Entities;

/// <summary>
/// A reusable email body owned by one recruiter. Templates are private to their author: another
/// recruiter in the same company cannot read, edit or delete them.
/// </summary>
public class EmailTemplate : Entity
{
    public const int NameMaxLength = 120;
    public const int SubjectMaxLength = 200;
    public const int BodyMaxLength = 8000;

    public Guid RecruiterId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;

    // EF Core constructor
    protected EmailTemplate() { }

    public EmailTemplate(Guid recruiterId, string name, string subject, string body)
    {
        if (recruiterId == Guid.Empty)
            throw new ArgumentException("RecruiterId is required.", nameof(recruiterId));

        RecruiterId = recruiterId;
        SetContent(name, subject, body);
    }

    public void Update(string name, string subject, string body)
    {
        SetContent(name, subject, body);
        Touch();
    }

    private void SetContent(string name, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        if (name.Trim().Length > NameMaxLength)
            throw new ArgumentException($"Name cannot exceed {NameMaxLength} characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject is required.", nameof(subject));
        if (subject.Trim().Length > SubjectMaxLength)
            throw new ArgumentException($"Subject cannot exceed {SubjectMaxLength} characters.", nameof(subject));
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Body is required.", nameof(body));

        var trimmedBody = body.Trim();
        if (trimmedBody.Length > BodyMaxLength)
            throw new ArgumentException($"Body cannot exceed {BodyMaxLength} characters.", nameof(body));

        Name = name.Trim();
        Subject = subject.Trim();
        Body = trimmedBody;
    }
}
