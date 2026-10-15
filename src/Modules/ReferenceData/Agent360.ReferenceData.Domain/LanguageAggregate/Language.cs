using Hmz.Core.SharedKernel.Data;

namespace Agent360.ReferenceData.Domain.LanguageAggregate;

public sealed class Language : SoftDeletableAuditableHasRowVersionEntityBase<Language>
{
    public string Name { get; private set; } = string.Empty;

    public string Code { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    private Language() { }

    public Language(string name, string code)
    {
        Name = name;
        Code = code;
        IsActive = true;
    }

    public static Language Create(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required", nameof(code));
        }

        return new Language
        {
            Name = name,
            Code = code.ToLowerInvariant(),
            IsActive = true
        };
    }

    public void Update(string name, bool isActive)
    {
        Name = name;
        IsActive = isActive;
    }
}