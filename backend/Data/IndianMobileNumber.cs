namespace backend.Data;

public static class IndianMobileNumber
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? "";
        if (normalized.Length == 10 && normalized[0] is >= '6' and <= '9' &&
            normalized.All(character => character is >= '0' and <= '9'))
        {
            return true;
        }

        normalized = "";
        return false;
    }
}
