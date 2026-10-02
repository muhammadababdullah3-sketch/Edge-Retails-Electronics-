namespace EdgeRetails.Application.Production.Printing;

/// <summary>
/// Authoritative Code 128 (Subset B) barcode encoder.
/// Produces deterministic module patterns for alphanumeric physical identities (e.g. AB1-PKF-DLX56-000027).
/// </summary>
public static class Code128Encoder
{
    private const int StartCodeB = 104;
    private const int StopCode = 106;

    // Code 128 pattern table for symbols 0 to 106
    // Each string represents alternating bar/space widths (6 elements, except STOP which has 7)
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", // 0-9
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132", // 10-19
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211", // 20-29
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313", // 30-39
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331", // 40-49
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111", // 50-59
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214", // 60-69
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111", // 70-79
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141", // 80-89
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141", // 90-99
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"                                 // 100-106
    ];

    public static string EncodeToModules(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var symbols = new List<int> { StartCodeB };
        var checksum = StartCodeB;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            var code = ch - 32;
            if (code < 0 || code > 95)
            {
                throw new ArgumentException($"Character '{ch}' is outside standard Code 128 Set B ASCII range (32-127).", nameof(input));
            }

            symbols.Add(code);
            checksum += (i + 1) * code;
        }

        symbols.Add(checksum % 103);
        symbols.Add(StopCode);

        var sb = new System.Text.StringBuilder();
        foreach (var sym in symbols)
        {
            var pattern = Patterns[sym];
            var isBar = true;
            foreach (var ch in pattern)
            {
                var width = ch - '0';
                sb.Append(isBar ? '1' : '0', width);
                isBar = !isBar;
            }
        }

        return sb.ToString();
    }
}
