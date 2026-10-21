using System.Globalization;

namespace SqlBackup.Core;

public static class DisplaySize
{
    public static string Format(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "—";
        }

        double value = bytes.Value;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString(index == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[index];
    }
}
