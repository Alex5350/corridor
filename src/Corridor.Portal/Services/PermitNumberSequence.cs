namespace Corridor.Portal.Services;

/// <summary>
/// Permit numbers follow IP-YYYY-NNNN where NNNN is a four digit sequence per calendar year,
/// computed from the numbers already stored in perm.ImportPermits.
/// </summary>
public static class PermitNumberSequence
{
    public static string Next(IEnumerable<string> existingNumbers, int year)
    {
        var prefix = $"IP-{year:D4}-";
        var highest = 0;
        foreach (var number in existingNumbers)
        {
            if (!number.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var suffix = number[prefix.Length..];
            if (int.TryParse(suffix, out var value) && value > highest)
            {
                highest = value;
            }
        }
        return $"{prefix}{highest + 1:D4}";
    }
}
