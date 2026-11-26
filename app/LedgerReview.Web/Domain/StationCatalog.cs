using System.Text.RegularExpressions;

namespace LedgerReview.Domain;

/// <summary>
/// 滨海快线站点字典。台账表单里的“项目名称/使用地点”是手写的，OCR 出来经常缺字错字，
/// 用它把识别结果归一到标准站名；也用于判断一页是否真的读到了站点。
/// </summary>
public static class StationCatalog
{
    public static readonly string[] BinhaiExpress =
    {
        "福州火车站", "东门", "闽都", "南公园", "三叉街", "帝封江", "祥谦",
        "首占", "滨海西", "大数据", "滨海中央商务区", "机场", "文岭"
    };

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["福州站"] = "福州火车站", ["火车站"] = "福州火车站", ["福州火车站"] = "福州火车站",
        ["东门站"] = "东门", ["闽都站"] = "闽都", ["南公园站"] = "南公园",
        ["三叉街站"] = "三叉街", ["帝封江站"] = "帝封江", ["祥谦站"] = "祥谦",
        ["首占站"] = "首占", ["滨海西站"] = "滨海西", ["大数据站"] = "大数据",
        ["滨海中央商务区站"] = "滨海中央商务区", ["中央商务区"] = "滨海中央商务区",
        ["滨海商务区"] = "滨海中央商务区", ["商务区"] = "滨海中央商务区",
        ["长乐机场"] = "机场", ["机场站"] = "机场", ["文岭站"] = "文岭"
    };

    public static bool IsKnown(string? station)
        => !string.IsNullOrWhiteSpace(station) && BinhaiExpress.Contains(station);

    /// <summary>把“滨海快线-首占站”“首占”等统一成标准站名；认不出返回 null。</summary>
    public static string? Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Regex.Replace(text, @"[\s\-—_·:：,，.。()（）]", "");
        foreach (var (alias, canonical) in Aliases.OrderByDescending(x => x.Key.Length))
            if (t.Contains(alias)) return canonical;
        foreach (var name in BinhaiExpress.OrderByDescending(x => x.Length))
            if (t.Contains(name)) return name;
        return null;
    }

    /// <summary>逐行近似匹配，容忍 1 个字的手写 OCR 错字（仅用于表头/项目名称行）。</summary>
    public static string? MatchLine(string? line)
    {
        var exact = Match(line);
        if (exact != null) return exact;
        if (string.IsNullOrWhiteSpace(line)) return null;
        var t = Regex.Replace(line, @"[\s\-—_·:：,，.。()（）]", "");
        if (t.Length > 12) return null;
        foreach (var name in BinhaiExpress)
        {
            if (Math.Abs(t.Length - name.Length) > 1) continue;
            if (t.Contains(name)) return name;
            if (EditDistance(t, name) <= 1) return name;
        }
        return null;
    }

    private static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>设备编号归一化：全角括号、空格、大小写。</summary>
    public static string NormalizeEquipment(string raw)
        => Regex.Replace(raw, @"\s+", "").Replace('（', '(').Replace('）', ')').ToUpperInvariant();

    /// <summary>用于归组/比对的设备号：去掉末尾的 (N)/(W) 等使用单位后缀。</summary>
    public static string EquipmentKey(string raw)
        => Regex.Replace(NormalizeEquipment(raw), @"\((N|W|S)\)$", "");
}
