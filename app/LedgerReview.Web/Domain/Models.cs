namespace LedgerReview.Domain;

public enum DocType
{
    Unknown,
    ChuitiHalfMonth,
    ChuitiQuarter,
    ChuitiHalfYear,
    ChuitiYear,
    FutiHalfMonth,
    FutiQuarter,
    FutiHalfYear,
    FutiYear,
    SupervisionF1,
    SelfCheckC2,
    Repair,
    SignatureBook,
    ControlCard
}

public enum Severity { Info, Warning, Serious }

public sealed record Rect(double X, double Y, double W, double H);

public sealed class FormTemplate
{
    public required DocType Type { get; init; }
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
    public string Family { get; init; } = "";
    public string Period { get; init; } = "";
    public int ItemCount { get; init; }
    public int RowsLeft { get; init; }
    public int RowsRight { get; init; }
    public double TableTop { get; init; }
    public double TableBottom { get; init; }
    public double LeftResultX { get; init; }
    public double RightResultX { get; init; }
    public double ResultWidth { get; init; } = 0.052;
    public bool TwoColumns { get; init; } = true;
    public Rect? NameSlot { get; init; }
    public Rect? CustomerSlot { get; init; }
    public Rect? SupervisorSlot { get; init; }
    public int[] NaItems { get; init; } = Array.Empty<int>();
    public string[] TitleKeywords { get; init; } = Array.Empty<string>();
    public string? TemplateAsset { get; init; }
}

public static class Catalog
{
    private const double MT = 0.150;   // maintenance table top
    private const double MB = 0.700;   // maintenance table bottom

    public static readonly FormTemplate[] All =
    {
        new() { Type = DocType.ChuitiHalfMonth, Code = "CHUITI_R", DisplayName = "垂梯半月保养单", Family = "CHUITI", Period = "半月",
                ItemCount = 31, RowsLeft = 25, RowsRight = 6, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                NaItems = new[] { 1, 17 }, TitleKeywords = new[] { "曳引与强制驱动", "半月" }, TemplateAsset = "CHUITI_R.png" },
        new() { Type = DocType.ChuitiQuarter, Code = "CHUITI_Q", DisplayName = "垂梯季度保养单", Family = "CHUITI", Period = "季度",
                ItemCount = 44, RowsLeft = 25, RowsRight = 19, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                NaItems = new[] { 1, 17, 32, 35 }, TitleKeywords = new[] { "曳引与强制驱动", "季度" }, TemplateAsset = "CHUITI_Q.png" },
        new() { Type = DocType.ChuitiHalfYear, Code = "CHUITI_HY", DisplayName = "垂梯半年保养单", Family = "CHUITI", Period = "半年",
                ItemCount = 59, RowsLeft = 31, RowsRight = 28, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                NaItems = new[] { 1, 17, 32, 35, 45 }, TitleKeywords = new[] { "曳引与强制驱动", "半年" }, TemplateAsset = "CHUITI_HY.png" },
        new() { Type = DocType.ChuitiYear, Code = "CHUITI_Y", DisplayName = "垂梯年度保养单", Family = "CHUITI", Period = "年度",
                ItemCount = 70, RowsLeft = 42, RowsRight = 28, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                NaItems = new[] { 41, 50 }, TitleKeywords = new[] { "曳引与强制驱动", "年度" }, TemplateAsset = "CHUITI_Y.png" },

        new() { Type = DocType.FutiHalfMonth, Code = "FUTI_SR", DisplayName = "扶梯半月保养单", Family = "FUTI", Period = "半月",
                ItemCount = 33, RowsLeft = 27, RowsRight = 6, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                TitleKeywords = new[] { "自动扶梯与人行道", "半月" }, TemplateAsset = "FUTI_SR.png" },
        new() { Type = DocType.FutiQuarter, Code = "FUTI_SQ", DisplayName = "扶梯季度保养单", Family = "FUTI", Period = "季度",
                ItemCount = 38, RowsLeft = 27, RowsRight = 11, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                TitleKeywords = new[] { "自动扶梯与人行道", "季度" }, TemplateAsset = "FUTI_SQ.png" },
        new() { Type = DocType.FutiHalfYear, Code = "FUTI_SH", DisplayName = "扶梯半年保养单", Family = "FUTI", Period = "半年",
                ItemCount = 50, RowsLeft = 27, RowsRight = 23, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                TitleKeywords = new[] { "自动扶梯与人行道", "半年" }, TemplateAsset = "FUTI_SH.png" },
        new() { Type = DocType.FutiYear, Code = "FUTI_SSA", DisplayName = "扶梯年度保养单", Family = "FUTI", Period = "年度",
                ItemCount = 63, RowsLeft = 34, RowsRight = 29, TableTop = MT, TableBottom = MB, LeftResultX = 0.455, RightResultX = 0.879,
                NameSlot = new(0.715, 0.670, 0.255, 0.052), CustomerSlot = new(0.715, 0.814, 0.180, 0.050),
                NaItems = new[] { 41, 50 }, TitleKeywords = new[] { "自动扶梯与人行道", "年度" }, TemplateAsset = "FUTI_SSA.png" },

        new() { Type = DocType.SupervisionF1, Code = "SUPERVISION_F1", DisplayName = "电梯维护保养监督记录表", Family = "SUPERVISION", Period = "",
                ItemCount = 6, RowsLeft = 6, RowsRight = 0, TableTop = 0.235, TableBottom = 0.510, LeftResultX = 0.622, RightResultX = 0, ResultWidth = 0.165, TwoColumns = false,
                SupervisorSlot = new(0.25, 0.676, 0.23, 0.055), CustomerSlot = new(0.25, 0.792, 0.25, 0.085),
                TitleKeywords = new[] { "监督记录表" }, TemplateAsset = "F1.png" },
        new() { Type = DocType.SelfCheckC2, Code = "SELF_CHECK_C2", DisplayName = "定期自行检查记录表", Family = "SUPERVISION", Period = "",
                ItemCount = 15, RowsLeft = 15, RowsRight = 0, TableTop = 0.250, TableBottom = 0.680, LeftResultX = 0.700, RightResultX = 0, ResultWidth = 0.150, TwoColumns = false,
                CustomerSlot = new(0.300, 0.895, 0.310, 0.045),
                TitleKeywords = new[] { "定期自行检查" }, TemplateAsset = "C2.png" },
        new() { Type = DocType.Repair, Code = "REPAIR", DisplayName = "急修单", Family = "REPAIR", Period = "",
                ItemCount = 0, TitleKeywords = new[] { "服务记录", "Service Docket" }, TemplateAsset = "REPAIR.png" },
        new() { Type = DocType.SignatureBook, Code = "SIGNATURE_BOOK", DisplayName = "人员签名字迹表", Family = "BOOK", Period = "",
                ItemCount = 0, TitleKeywords = new[] { "签名字迹" } },
        new() { Type = DocType.ControlCard, Code = "CONTROL_CARD", DisplayName = "施工作业卡控表", Family = "CONTROL", Period = "",
                ItemCount = 0, TitleKeywords = new[] { "施工作业卡控" } }
    };

    public static FormTemplate? Find(DocType t) => All.FirstOrDefault(x => x.Type == t);
    public static FormTemplate? Find(string code) => All.FirstOrDefault(x => x.Code == code);
}

public sealed record OcrWord(string Text, double X, double Y, double W, double H);
public sealed record OcrLine(string Text, List<OcrWord> Words);
public sealed record OcrPage(List<OcrLine> Lines, int PixelWidth, int PixelHeight)
{
    public static readonly OcrPage Empty = new(new List<OcrLine>(), 0, 0);
    public string FullText => string.Join("\n", Lines.Select(l => l.Text));
}

public sealed class PageFact
{
    public DocType Type { get; set; } = DocType.Unknown;
    public double Confidence { get; set; }
    public int? SeqNo { get; set; }
    public string? DocDate { get; set; }       // yyyy-MM-dd
    public string? EquipmentId { get; set; }
    public string? Station { get; set; }
    public int? RunCount { get; set; }
    public string OcrText { get; set; } = "";
    public List<CheckFact> Checks { get; } = new();
    public List<SignatureFact> Signatures { get; } = new();
}

public sealed class CheckFact
{
    public int ItemNo { get; set; }
    public string Result { get; set; } = "BLANK"; // FILLED / BLANK
    public double Ink { get; set; }
    public Rect Rect { get; set; } = new(0, 0, 0, 0);
}

public sealed class SignatureFact
{
    public string Code { get; set; } = "";
    public string Role { get; set; } = "";   // MAINTAINER / CUSTOMER / SUPERVISOR
    public bool HasInk { get; set; }
    public Rect Rect { get; set; } = new(0, 0, 0, 0);
    public string? CropPath { get; set; }
    public string Verdict { get; set; } = "UNDETERMINED";
    public string? PersonName { get; set; }
    public double Score { get; set; }
    public List<Candidate> Candidates { get; } = new();
    public List<SignatureOutcome> Outcomes { get; } = new();
}

public sealed record SignatureOutcome(string Verdict, string? PersonId, string? PersonName, double Score, string CandidatesJson, string? CropPath);

public sealed record Candidate(string PersonId, string PersonName, double Score);

public sealed class FindingDraft
{
    public string RuleCode { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Warning;
    public string Message { get; init; } = "";
    public Rect Rect { get; init; } = new(0, 0, 0, 0);
    public string? CropPath { get; init; }
    public string? Meta { get; init; }
}














