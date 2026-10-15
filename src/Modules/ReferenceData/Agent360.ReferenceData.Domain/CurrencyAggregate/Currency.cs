using Hmz.Core.SharedKernel.Data;

namespace Agent360.ReferenceData.Domain.CurrencyAggregate;

public sealed class Currency : SoftDeletableAuditableHasRowVersionEntityBase<Currency>
{
    public string Code { get; private set; } = string.Empty; // VND

    public string Symbol { get; private set; } = string.Empty; // đ

    public int DecimalDigits { get; private set; } // 0

    public bool IsActive { get; private set; } = true;

    private Currency() { }

    public Currency(string code, string symbol, int decimalDigits)
    {
        Code = code;
        Symbol = symbol;
        DecimalDigits = decimalDigits;
        IsActive = true;
    }

    public static Currency Create(string code, string symbol, int decimalDigits)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code is required", nameof(code));

        if (decimalDigits < 0 || decimalDigits > 6)
            throw new ArgumentException("Invalid decimal digits", nameof(decimalDigits));

        return new Currency
        {
            Code = code.Trim().ToUpperInvariant(),
            Symbol = symbol,
            DecimalDigits = decimalDigits,
            IsActive = true
        };
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;
}