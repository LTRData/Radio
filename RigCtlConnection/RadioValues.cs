using System;

namespace LTRData.RigCtl;

/// <summary>
/// Helper methods to parse and format values from and to connected radio.
/// </summary>
public static class RadioValues
{
    /// <summary>
    /// Parse signal strength value as S[1-9] and dB over S9.
    /// </summary>
    /// <param name="strength">Value from radio</param>
    /// <param name="sValue">Parsed S value</param>
    /// <param name="plusDb">Parsed dB value above S9</param>
    public static void ParseStrengthValue(decimal strength, out sbyte sValue, out int plusDb)
    {
        if (strength > 0m)
        {
            sValue = 9;
            plusDb = (int)Math.Round(strength, MidpointRounding.AwayFromZero);
        }
        else
        {
            sValue = (sbyte)Math.Round(strength / 6m + 9m, MidpointRounding.AwayFromZero);
            plusDb = 0;
        }
    }

    /// <summary>
    /// Format signal strength value as S[1-9]+NdB format
    /// </summary>
    /// <param name="sValue">S value</param>
    /// <param name="plusDb">dB above S9 value</param>
    public static string FormatStrengthValue(sbyte sValue, int plusDb)
    {
        if (sValue < 0)
        {
            return "No signal";
        }

        if (plusDb <= 0)
        {
            return $"S {sValue}";
        }

        return $"S {sValue} + {plusDb} dB";
    }

    /// <summary>
    /// Format SWR and ALC values in TX mode
    /// </summary>
    /// <param name="swr">SWR value from radio</param>
    /// <param name="alc">ALC value from radio</param>
    /// <returns></returns>
    public static string FormatSWRValue(decimal swr, decimal alc) => $"SWR {swr:0.0} ALC {alc:0.0}";

    /// <summary>
    /// Returns default tuning steps for various radio modes.
    /// </summary>
    /// <param name="frequency">Current frequency</param>
    /// <param name="mode">Current mode</param>
    /// <returns>Default tuning step</returns>
    public static int GetTuningStep(int frequency, string? mode) => mode switch
    {
        "USB" => 2500,
        "LSB" => 2500,
        "AM" => frequency > 1800000 ? 5000 : 9000,
        "FM" => frequency > 148000000 ? 25000 : frequency < 70000000 ? 10000 : 12500,
        "WFM" => 50000,
        _ => 1000
    };

}
