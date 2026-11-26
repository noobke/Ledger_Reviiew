using Microsoft.Data.Sqlite;
using LedgerReview.Domain;

namespace LedgerReview.Infrastructure;

public sealed class Workspace
{
    public string Root { get; }
    public string AssetsRoot { get; }

    public Workspace(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["Workspace"];
        Root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LedgerReview")
            : configured!;
        AssetsRoot = Path.Combine(env.ContentRootPath, "Assets");
        Directory.CreateDirectory(Root);
        foreach (var d in new[] { "batches", "templates", "sigbook", "logs", "export" })
            Directory.CreateDirectory(Path.Combine(Root, d));
    }

    public string BatchDir(string batchId) => Path.Combine(Root, "batches", batchId);
    public string SourceDir(string batchId) => Path.Combine(BatchDir(batchId), "source");
    public string PagesDir(string batchId) => Path.Combine(BatchDir(batchId), "pages");
    public string CropsDir(string batchId) => Path.Combine(BatchDir(batchId), "crops");
    public string ThumbsDir(string batchId) => Path.Combine(BatchDir(batchId), "thumbs");

    public void EnsureBatchDirs(string batchId)
    {
        foreach (var d in new[] { SourceDir(batchId), PagesDir(batchId), CropsDir(batchId), ThumbsDir(batchId) })
            Directory.CreateDirectory(d);
    }

    public static string SafeName(string? s, string fallback = "未识别")
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace('/', '_').Replace('\\', '_').Trim();
        return string.IsNullOrWhiteSpace(s) ? fallback : (s.Length > 80 ? s[..80] : s);
    }
}

public sealed class Db
{
    private readonly string _cs;
    private readonly object _gate = new();

    public Db(Workspace ws)
    {
        _cs = $"Data Source={Path.Combine(ws.Root, "ledger.db")};Cache=Shared";
        Init();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS Batch(
          Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Station TEXT, Equipment TEXT, Status TEXT NOT NULL,
          CreatedAt TEXT NOT NULL, TotalPages INTEGER NOT NULL DEFAULT 0,
          ProcessedPages INTEGER NOT NULL DEFAULT 0, ProblemCount INTEGER NOT NULL DEFAULT 0, Error TEXT);
        CREATE TABLE IF NOT EXISTS SourceFile(
          Id TEXT PRIMARY KEY, BatchId TEXT NOT NULL, FileName TEXT, Path TEXT, EquipmentHint TEXT, Sha256 TEXT,
          PageCount INTEGER NOT NULL DEFAULT 0, Status TEXT, Error TEXT);
        CREATE TABLE IF NOT EXISTS Page(
          Id TEXT PRIMARY KEY, BatchId TEXT NOT NULL, SourceFileId TEXT NOT NULL, PageNo INTEGER NOT NULL,
          ImagePath TEXT, ThumbPath TEXT, Width INTEGER, Height INTEGER, Quality REAL,
          Status TEXT NOT NULL DEFAULT 'pending', Error TEXT);
        CREATE TABLE IF NOT EXISTS Document(
          Id TEXT PRIMARY KEY, PageId TEXT NOT NULL UNIQUE, DocType TEXT, TypeConfidence REAL,
          SeqNo INTEGER, DocDate TEXT, EquipmentId TEXT, Station TEXT, RunCount INTEGER,
          OcrText TEXT, EventKey TEXT);
        CREATE TABLE IF NOT EXISTS FieldValue(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, DocumentId TEXT NOT NULL, Code TEXT, Raw TEXT, Norm TEXT,
          Confidence REAL, X REAL, Y REAL, W REAL, H REAL);
        CREATE TABLE IF NOT EXISTS CheckItem(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, DocumentId TEXT NOT NULL, ItemNo INTEGER, Result TEXT, Ink REAL);
        CREATE TABLE IF NOT EXISTS SignatureSlot(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, DocumentId TEXT NOT NULL, Code TEXT, Role TEXT,
          X REAL, Y REAL, W REAL, H REAL, HasInk INTEGER, CropPath TEXT);
        CREATE TABLE IF NOT EXISTS SignatureMatch(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, SlotId INTEGER NOT NULL, SegmentNo INTEGER,
          Verdict TEXT, PersonId TEXT, PersonName TEXT, Score REAL, Candidates TEXT, CropPath TEXT,
          Confirmed INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS Person(
          Id TEXT PRIMARY KEY, Name TEXT, Active INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS SignatureRef(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, PersonId TEXT, Idx INTEGER, CropPath TEXT);
        CREATE TABLE IF NOT EXISTS Finding(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, BatchId TEXT, PageId TEXT, DocumentId TEXT,
          RuleCode TEXT, Severity TEXT, Message TEXT, X REAL, Y REAL, W REAL, H REAL,
          CropPath TEXT, Status TEXT NOT NULL DEFAULT 'open', Meta TEXT);
        CREATE TABLE IF NOT EXISTS Setting(Key TEXT PRIMARY KEY, Value TEXT);
        CREATE INDEX IF NOT EXISTS IX_Page_Batch ON Page(BatchId);
        CREATE INDEX IF NOT EXISTS IX_Doc_Page ON Document(PageId);
        CREATE INDEX IF NOT EXISTS IX_Finding_Batch ON Finding(BatchId);
        CREATE INDEX IF NOT EXISTS IX_Finding_Page ON Finding(PageId);
        """;
        cmd.ExecuteNonQuery();
        try { Exec("ALTER TABLE Batch ADD COLUMN Equipment TEXT"); } catch { }
        try { Exec("ALTER TABLE SourceFile ADD COLUMN EquipmentHint TEXT"); } catch { }
    }

    private void Exec(string sql, params (string k, object? v)[] ps)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string k, object? v)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string Str(SqliteDataReader r, string name) => r[name] is DBNull ? "" : Convert.ToString(r[name]) ?? "";
    public static int Int(SqliteDataReader r, string name) => r[name] is DBNull ? 0 : Convert.ToInt32(r[name]);
    public static long Long(SqliteDataReader r, string name) => r[name] is DBNull ? 0 : Convert.ToInt64(r[name]);
    public static double Dbl(SqliteDataReader r, string name) => r[name] is DBNull ? 0 : Convert.ToDouble(r[name]);

    // ---------- write ----------
    public void CreateBatch(string id, string name, string station, string equipment)
        => Exec("INSERT INTO Batch(Id,Name,Station,Equipment,Status,CreatedAt) VALUES($i,$n,$s,$e,'pending',$t)",
            ("$i", id), ("$n", name), ("$s", station), ("$e", equipment), ("$t", DateTime.Now.ToString("s")));

    public void AddSourceFile(string id, string batchId, string fileName, string path, string hint, string sha, int pages, string status)
        => Exec("INSERT INTO SourceFile(Id,BatchId,FileName,Path,EquipmentHint,Sha256,PageCount,Status) VALUES($i,$b,$f,$p,$e,$h,$c,$s)",
            ("$i", id), ("$b", batchId), ("$f", fileName), ("$p", path), ("$e", hint), ("$h", sha), ("$c", pages), ("$s", status));

    public void AddPage(string id, string batchId, string fileId, int no)
        => Exec("INSERT INTO Page(Id,BatchId,SourceFileId,PageNo,Status) VALUES($i,$b,$f,$n,'pending')",
            ("$i", id), ("$b", batchId), ("$f", fileId), ("$n", no));

    public void UpdatePageImage(string pageId, string image, string thumb, int w, int h, double quality)
        => Exec("UPDATE Page SET ImagePath=$im,ThumbPath=$th,Width=$w,Height=$h,Quality=$q,Status='rendered' WHERE Id=$i",
            ("$im", image), ("$th", thumb), ("$w", w), ("$h", h), ("$q", quality), ("$i", pageId));

    public void SetPageStatus(string pageId, string status, string? error = null)
        => Exec("UPDATE Page SET Status=$s,Error=$e WHERE Id=$i", ("$s", status), ("$e", error), ("$i", pageId));

    public void UpdateBatch(string batchId, int total, int processed, string status, string? error = null)
        => Exec("UPDATE Batch SET TotalPages=$t,ProcessedPages=$p,Status=$s,Error=$e WHERE Id=$i",
            ("$t", total), ("$p", processed), ("$s", status), ("$e", error), ("$i", batchId));

    public void SetBatchStatus(string batchId, string status, string? error = null)
        => Exec("UPDATE Batch SET Status=$s,Error=$e WHERE Id=$i", ("$s", status), ("$e", error), ("$i", batchId));

    public void ResetPages(string batchId)
        => Exec("UPDATE Page SET Status='pending', Error=NULL WHERE BatchId=$b", ("$b", batchId));

    public void SetDocumentIdentity(string docId, string station, string equipment)
    {
        Exec("UPDATE Document SET Station=$s,EquipmentId=$e WHERE Id=$d", ("$s", station), ("$e", equipment), ("$d", docId));
        void Sync(string code, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            bool exists = Query("SELECT 1 FROM FieldValue WHERE DocumentId=$d AND Code=$c",
                _ => 1, ("$d", docId), ("$c", code)).Count > 0;
            if (exists)
                Exec("UPDATE FieldValue SET Raw=$v,Norm=$v WHERE DocumentId=$d AND Code=$c",
                    ("$v", value), ("$d", docId), ("$c", code));
            else
                AddField(docId, code, value, value, 0.75, new Rect(0, 0, 0, 0));
        }
        Sync("station", station);
        Sync("equipment", equipment);
    }

    public void SetDocumentSeq(string docId, int seq)
    {
        Exec("UPDATE Document SET SeqNo=$q WHERE Id=$d", ("$q", seq), ("$d", docId));
        bool exists = Query("SELECT 1 FROM FieldValue WHERE DocumentId=$d AND Code='seq'",
            _ => 1, ("$d", docId)).Count > 0;
        var v = seq.ToString();
        if (exists) Exec("UPDATE FieldValue SET Raw=$v,Norm=$v WHERE DocumentId=$d AND Code='seq'", ("$v", v), ("$d", docId));
        else AddField(docId, "seq", v, v, 0.7, new Rect(0, 0, 0, 0));
    }

    public void UpdateBatchInfo(string batchId, string name, string station, string equipment)
        => Exec("UPDATE Batch SET Name=CASE WHEN $n='' THEN Name ELSE $n END, Station=$s, Equipment=$e WHERE Id=$i",
            ("$n", name ?? ""), ("$s", station ?? ""), ("$e", equipment ?? ""), ("$i", batchId));

    public void RefreshProblemCount(string batchId)
        => Exec("UPDATE Batch SET ProblemCount=(SELECT COUNT(*) FROM Finding WHERE BatchId=$b AND Severity='Serious') WHERE Id=$b", ("$b", batchId));

    public void SaveDocument(string id, string pageId, PageFact fact)
        => Exec("""
            INSERT INTO Document(Id,PageId,DocType,TypeConfidence,SeqNo,DocDate,EquipmentId,Station,RunCount,OcrText)
            VALUES($i,$p,$t,$c,$q,$d,$e,$s,$r,$o)
            ON CONFLICT(PageId) DO UPDATE SET DocType=$t,TypeConfidence=$c,SeqNo=$q,DocDate=$d,EquipmentId=$e,Station=$s,RunCount=$r,OcrText=$o
            """,
            ("$i", id), ("$p", pageId), ("$t", fact.Type.ToString()), ("$c", fact.Confidence), ("$q", fact.SeqNo),
            ("$d", fact.DocDate), ("$e", fact.EquipmentId), ("$s", fact.Station), ("$r", fact.RunCount), ("$o", fact.OcrText));

    public void ClearDocumentChildren(string docId)
    {
        Exec("DELETE FROM SignatureMatch WHERE SlotId IN (SELECT Id FROM SignatureSlot WHERE DocumentId=$d)", ("$d", docId));
        Exec("DELETE FROM FieldValue WHERE DocumentId=$d", ("$d", docId));
        Exec("DELETE FROM CheckItem WHERE DocumentId=$d", ("$d", docId));
        Exec("DELETE FROM SignatureSlot WHERE DocumentId=$d", ("$d", docId));
    }

    public void AddField(string docId, string code, string raw, string norm, double conf, Rect r)
        => Exec("INSERT INTO FieldValue(DocumentId,Code,Raw,Norm,Confidence,X,Y,W,H) VALUES($d,$c,$r,$n,$f,$x,$y,$w,$h)",
            ("$d", docId), ("$c", code), ("$r", raw), ("$n", norm), ("$f", conf), ("$x", r.X), ("$y", r.Y), ("$w", r.W), ("$h", r.H));

    public void AddCheck(string docId, CheckFact c)
        => Exec("INSERT INTO CheckItem(DocumentId,ItemNo,Result,Ink) VALUES($d,$i,$r,$k)",
            ("$d", docId), ("$i", c.ItemNo), ("$r", c.Result), ("$k", c.Ink));

    public long AddSignatureSlot(string docId, SignatureFact s)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO SignatureSlot(DocumentId,Code,Role,X,Y,W,H,HasInk,CropPath) VALUES($d,$c,$r,$x,$y,$w,$h,$k,$p); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$d", docId); cmd.Parameters.AddWithValue("$c", s.Code);
            cmd.Parameters.AddWithValue("$r", s.Role); cmd.Parameters.AddWithValue("$x", s.Rect.X);
            cmd.Parameters.AddWithValue("$y", s.Rect.Y); cmd.Parameters.AddWithValue("$w", s.Rect.W);
            cmd.Parameters.AddWithValue("$h", s.Rect.H); cmd.Parameters.AddWithValue("$k", s.HasInk ? 1 : 0);
            cmd.Parameters.AddWithValue("$p", s.CropPath ?? (object)DBNull.Value);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void AddMatch(long slotId, int seg, SignatureFact s, Candidate? cand, string verdict, string candidatesJson, string? crop)
        => Exec("INSERT INTO SignatureMatch(SlotId,SegmentNo,Verdict,PersonId,PersonName,Score,Candidates,CropPath) VALUES($s,$g,$v,$p,$n,$c,$j,$r)",
            ("$s", slotId), ("$g", seg), ("$v", verdict), ("$p", cand?.PersonId), ("$n", cand?.PersonName),
            ("$c", cand?.Score ?? 0), ("$j", candidatesJson), ("$r", crop));

    public void ReplaceFindings(string batchId, string pageId, string? docId, IEnumerable<FindingDraft> drafts)
    {
        Exec("DELETE FROM Finding WHERE PageId=$p", ("$p", pageId));
        foreach (var d in drafts)
            Exec("INSERT INTO Finding(BatchId,PageId,DocumentId,RuleCode,Severity,Message,X,Y,W,H,CropPath,Meta) VALUES($b,$p,$d,$r,$s,$m,$x,$y,$w,$h,$c,$j)",
                ("$b", batchId), ("$p", pageId), ("$d", docId), ("$r", d.RuleCode), ("$s", d.Severity.ToString()),
                ("$m", d.Message), ("$x", d.Rect.X), ("$y", d.Rect.Y), ("$w", d.Rect.W), ("$h", d.Rect.H),
                ("$c", d.CropPath), ("$j", d.Meta));
    }

    public void ClearCrossFindings(string batchId)
        => Exec("DELETE FROM Finding WHERE BatchId=$b AND RuleCode IN ('C1','B2','B7','E3','B4')", ("$b", batchId));

    public void AddFinding(string batchId, string pageId, string? docId, FindingDraft d)
        => Exec("INSERT INTO Finding(BatchId,PageId,DocumentId,RuleCode,Severity,Message,X,Y,W,H,CropPath,Meta) VALUES($b,$p,$d,$r,$s,$m,$x,$y,$w,$h,$c,$j)",
            ("$b", batchId), ("$p", pageId), ("$d", docId), ("$r", d.RuleCode), ("$s", d.Severity.ToString()),
            ("$m", d.Message), ("$x", d.Rect.X), ("$y", d.Rect.Y), ("$w", d.Rect.W), ("$h", d.Rect.H),
            ("$c", d.CropPath), ("$j", d.Meta));

    public List<DocRow> GetDocs(string batchId) => Query("""
        SELECT d.Id,d.PageId,d.DocType,d.SeqNo,d.DocDate,d.EquipmentId,d.Station,d.TypeConfidence,
               p.PageNo,p.SourceFileId
        FROM Document d JOIN Page p ON p.Id=d.PageId
        WHERE p.BatchId=$b ORDER BY p.PageNo
        """, r => new DocRow(Str(r,"Id"), Str(r,"PageId"), Str(r,"DocType"), Int(r,"SeqNo"), Str(r,"DocDate"),
            Str(r,"EquipmentId"), Str(r,"Station"), Dbl(r,"TypeConfidence"), Int(r,"PageNo"), Str(r,"SourceFileId")), ("$b", batchId));

    public List<MatchPersonRow> GetMatchedPersons(string batchId) => Query("""
        SELECT ss.DocumentId, sm.PersonId, sm.PersonName, sm.Verdict
        FROM SignatureMatch sm JOIN SignatureSlot ss ON ss.Id=sm.SlotId
        JOIN Document d ON d.Id=ss.DocumentId JOIN Page p ON p.Id=d.PageId
        WHERE p.BatchId=$b AND sm.Verdict='MATCH'
        """, r => new MatchPersonRow(Str(r,"DocumentId"), Str(r,"PersonId"), Str(r,"PersonName"), Str(r,"Verdict")), ("$b", batchId));

    public void SetEvent(string docId, string eventKey) => Exec("UPDATE Document SET EventKey=$e WHERE Id=$d", ("$e", eventKey), ("$d", docId));

    public void UpdateFindingStatus(long id, string status) => Exec("UPDATE Finding SET Status=$s WHERE Id=$i", ("$s", status), ("$i", id));

    public void ConfirmMatch(long id, string verdict, string? personId, string? personName)
        => Exec("UPDATE SignatureMatch SET Verdict=$v,PersonId=$p,PersonName=$n,Confirmed=1 WHERE Id=$i",
            ("$v", verdict), ("$p", personId), ("$n", personName), ("$i", id));

    public void UpsertPerson(string id, string name) => Exec("INSERT INTO Person(Id,Name,Active) VALUES($i,$n,1) ON CONFLICT(Id) DO UPDATE SET Name=$n", ("$i", id), ("$n", name));
    public void AddRef(string personId, int idx, string crop) => Exec("INSERT INTO SignatureRef(PersonId,Idx,CropPath) VALUES($p,$i,$c)", ("$p", personId), ("$i", idx), ("$c", crop));
    public void ClearRefs() { Exec("DELETE FROM SignatureRef"); Exec("DELETE FROM Person"); }

    public void SetSetting(string k, string v) => Exec("INSERT INTO Setting(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v", ("$k", k), ("$v", v));
    public string? GetSetting(string k) => Query("SELECT Value FROM Setting WHERE Key=$k", r => Str(r, "Value"), ("$k", k)).FirstOrDefault();

    // ---------- read ----------
    public List<BatchRow> GetBatches() => Query("""
        SELECT Id,Name,Station,Equipment,Status,CreatedAt,TotalPages,ProcessedPages,ProblemCount,Error
        FROM Batch ORDER BY CreatedAt DESC
        """, r => new BatchRow(Str(r,"Id"), Str(r,"Name"), Str(r,"Station"), Str(r,"Equipment"), Str(r,"Status"), Str(r,"CreatedAt"),
            Int(r,"TotalPages"), Int(r,"ProcessedPages"), Int(r,"ProblemCount"), Str(r,"Error")));

    public BatchRow? GetBatch(string id) => GetBatches().FirstOrDefault(b => b.Id == id);

    public List<FileRow> GetFiles(string batchId) => Query("SELECT Id,FileName,PageCount,Status,Error FROM SourceFile WHERE BatchId=$b",
        r => new FileRow(Str(r,"Id"), Str(r,"FileName"), Int(r,"PageCount"), Str(r,"Status"), Str(r,"Error")), ("$b", batchId));

    public List<SourceRow> GetSourceFiles(string batchId) => Query("SELECT Id,FileName,Path,PageCount FROM SourceFile WHERE BatchId=$b ORDER BY FileName",
        r => new SourceRow(Str(r,"Id"), Str(r,"FileName"), Str(r,"Path"), Int(r,"PageCount")), ("$b", batchId));

    public List<WorkPageRow> GetWorkPages(string batchId) => Query("""
        SELECT p.Id,p.SourceFileId,p.PageNo,p.Status,p.ImagePath,s.Path AS SourcePath,s.EquipmentHint AS EquipmentHint
        FROM Page p JOIN SourceFile s ON s.Id=p.SourceFileId
        WHERE p.BatchId=$b AND p.Status <> 'done' ORDER BY p.PageNo
        """, r => new WorkPageRow(Str(r,"Id"), Str(r,"SourceFileId"), Int(r,"PageNo"), Str(r,"Status"),
            Str(r,"ImagePath"), Str(r,"SourcePath"), Str(r,"EquipmentHint")), ("$b", batchId));

    public List<ExportRow> GetExportRows(string batchId) => Query("""
        SELECT d.DocType,d.DocDate,d.EquipmentId,d.Station,p.PageNo,p.Id AS PageId,s.Path AS SourcePath
        FROM Document d JOIN Page p ON p.Id=d.PageId JOIN SourceFile s ON s.Id=p.SourceFileId
        WHERE p.BatchId=$b ORDER BY p.PageNo
        """, r => new ExportRow(Str(r,"DocType"), Str(r,"DocDate"), Str(r,"EquipmentId"), Str(r,"Station"),
            Int(r,"PageNo"), Str(r,"PageId"), Str(r,"SourcePath")), ("$b", batchId));

    public List<PageRow> GetPages(string batchId, string? type, bool onlyProblem)
    {
        var sql = """
            SELECT p.Id,p.PageNo,p.Status,p.Error,p.ImagePath,p.ThumbPath,p.Quality,p.SourceFileId,
                   d.DocType,d.SeqNo,d.DocDate,d.EquipmentId,d.Station,d.TypeConfidence,
                   (SELECT COUNT(*) FROM Finding f WHERE f.PageId=p.Id AND f.Status='open') AS OpenFindings,
                   (SELECT COUNT(*) FROM Finding f WHERE f.PageId=p.Id AND f.Severity='Serious' AND f.Status='open') AS SeriousFindings
            FROM Page p LEFT JOIN Document d ON d.PageId=p.Id
            WHERE p.BatchId=$b
            """;
        var ps = new List<(string, object?)> { ("$b", batchId) };
        if (!string.IsNullOrEmpty(type) && type != "ALL") { sql += " AND d.DocType=$t"; ps.Add(("$t", type)); }
        if (onlyProblem) sql += " AND EXISTS(SELECT 1 FROM Finding f WHERE f.PageId=p.Id AND f.Status='open')";
        sql += " ORDER BY p.PageNo";
        return Query(sql, r => new PageRow(Str(r,"Id"), Int(r,"PageNo"), Str(r,"Status"),
            Str(r,"DocType"), Str(r,"DocDate"), Str(r,"EquipmentId"), Str(r,"Station"), Dbl(r,"TypeConfidence"),
            Str(r,"ThumbPath"), Int(r,"OpenFindings"), Int(r,"SeriousFindings"), Str(r,"Error"), Str(r,"SourceFileId")), ps.ToArray());
    }

    public PageDetail? GetPageDetail(string pageId)
    {
        var rows = Query("""
            SELECT p.Id,p.BatchId,p.PageNo,p.Status,p.Error,p.ImagePath,p.ThumbPath,p.Quality,p.Width,p.Height,
                   d.Id AS DocId,d.DocType,d.TypeConfidence,d.SeqNo,d.DocDate,d.EquipmentId,d.Station,d.RunCount,d.OcrText
            FROM Page p LEFT JOIN Document d ON d.PageId=p.Id WHERE p.Id=$p
            """, r => new PageDetail {
                Id = Str(r,"Id"), BatchId = Str(r,"BatchId"), PageNo = Int(r,"PageNo"), Status = Str(r,"Status"),
                Error = Str(r,"Error"), ImagePath = Str(r,"ImagePath"), ThumbPath = Str(r,"ThumbPath"), Quality = Dbl(r,"Quality"),
                Width = Int(r,"Width"), Height = Int(r,"Height"), DocId = Str(r,"DocId"), DocType = Str(r,"DocType"),
                TypeConfidence = Dbl(r,"TypeConfidence"), SeqNo = Int(r,"SeqNo"), DocDate = Str(r,"DocDate"),
                EquipmentId = Str(r,"EquipmentId"), Station = Str(r,"Station"), RunCount = Int(r,"RunCount"), OcrText = Str(r,"OcrText")
            }, ("$p", pageId)).FirstOrDefault();
        if (rows == null) return null;
        var docId = rows.DocId;
        if (!string.IsNullOrEmpty(docId))
        {
            rows.Fields = Query("SELECT Code,Raw,Norm,Confidence,X,Y,W,H FROM FieldValue WHERE DocumentId=$d ORDER BY Id",
                r => new FieldRow(Str(r,"Code"), Str(r,"Raw"), Str(r,"Norm"), Dbl(r,"Confidence"), Dbl(r,"X"), Dbl(r,"Y"), Dbl(r,"W"), Dbl(r,"H")), ("$d", docId));
            rows.Checks = Query("SELECT ItemNo,Result,Ink FROM CheckItem WHERE DocumentId=$d ORDER BY ItemNo",
                r => new CheckRow(Int(r,"ItemNo"), Str(r,"Result"), Dbl(r,"Ink")), ("$d", docId));
            rows.Signatures = Query("SELECT Id,Code,Role,X,Y,W,H,HasInk,CropPath FROM SignatureSlot WHERE DocumentId=$d ORDER BY Id",
                r => new SlotRow(Long(r,"Id"), Str(r,"Code"), Str(r,"Role"), Dbl(r,"X"), Dbl(r,"Y"), Dbl(r,"W"), Dbl(r,"H"), Int(r,"HasInk") == 1, Str(r,"CropPath")), ("$d", docId));
            foreach (var s in rows.Signatures)
                s.Matches = Query("SELECT Id,SegmentNo,Verdict,PersonId,PersonName,Score,Candidates,CropPath,Confirmed FROM SignatureMatch WHERE SlotId=$s ORDER BY SegmentNo",
                    r => new MatchRow(Long(r,"Id"), Int(r,"SegmentNo"), Str(r,"Verdict"), Str(r,"PersonId"), Str(r,"PersonName"),
                        Dbl(r,"Score"), Str(r,"Candidates"), Str(r,"CropPath"), Int(r,"Confirmed") == 1), ("$s", s.Id));
        }
        rows.Findings = Query("SELECT Id,RuleCode,Severity,Message,X,Y,W,H,Status,CropPath FROM Finding WHERE PageId=$p ORDER BY CASE Severity WHEN 'Serious' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END, Id",
            r => new FindingRow(Long(r,"Id"), Str(r,"RuleCode"), Str(r,"Severity"), Str(r,"Message"), Dbl(r,"X"), Dbl(r,"Y"), Dbl(r,"W"), Dbl(r,"H"), Str(r,"Status"), Str(r,"CropPath")), ("$p", pageId));
        return rows;
    }

    public List<FindingRow> GetFindings(string batchId, string? severity, string? status, int limit = 500)
    {
        var sql = """
            SELECT f.Id,f.RuleCode,f.Severity,f.Message,f.X,f.Y,f.W,f.H,f.Status,f.CropPath,f.PageId,p.PageNo,d.DocType,d.EquipmentId,d.DocDate
            FROM Finding f LEFT JOIN Page p ON p.Id=f.PageId LEFT JOIN Document d ON d.Id=f.DocumentId
            WHERE f.BatchId=$b
            """;
        var ps = new List<(string, object?)> { ("$b", batchId) };
        if (!string.IsNullOrEmpty(severity) && severity != "ALL") { sql += " AND f.Severity=$s"; ps.Add(("$s", severity)); }
        if (!string.IsNullOrEmpty(status) && status != "ALL") { sql += " AND f.Status=$t"; ps.Add(("$t", status)); }
        sql += " ORDER BY CASE f.Severity WHEN 'Serious' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END, p.PageNo LIMIT $l";
        ps.Add(("$l", limit));
        return Query(sql, r => new FindingRow(Long(r,"Id"), Str(r,"RuleCode"), Str(r,"Severity"), Str(r,"Message"),
            Dbl(r,"X"), Dbl(r,"Y"), Dbl(r,"W"), Dbl(r,"H"), Str(r,"Status"), Str(r,"CropPath"))
        {
            PageId = Str(r,"PageId"), PageNo = Int(r,"PageNo"), DocType = Str(r,"DocType"),
            EquipmentId = Str(r,"EquipmentId"), DocDate = Str(r,"DocDate")
        }, ps.ToArray());
    }

    public List<PersonRow> GetPersons() => Query("SELECT Id,Name FROM Person ORDER BY Id",
        r => new PersonRow(Str(r,"Id"), Str(r,"Name")));

    public string? GetPageImage(string pageId, bool thumb)
    {
        var col = thumb ? "ThumbPath" : "ImagePath";
        return Query($"SELECT {col} AS P FROM Page WHERE Id=$i", r => Str(r,"P"), ("$i", pageId)).FirstOrDefault();
    }

    public string? GetMatchCrop(long matchId)
        => Query("SELECT CropPath AS P FROM SignatureMatch WHERE Id=$i", r => Str(r,"P"), ("$i", matchId)).FirstOrDefault();

    public (string PersonId, string PersonName)? GetPerson(string id)
    {
        var rows = Query("SELECT Id,Name FROM Person WHERE Id=$i", r => (Str(r,"Id"), Str(r,"Name")), ("$i", id));
        return rows.Count == 0 ? null : rows[0];
    }

    public List<(string Id, string Name)> GetPersonList() => GetPersons().Select(p => (p.Id, p.Name)).ToList();
}

public sealed record BatchRow(string Id, string Name, string Station, string Equipment, string Status, string CreatedAt, int TotalPages, int ProcessedPages, int ProblemCount, string Error);
public sealed record FileRow(string Id, string FileName, int PageCount, string Status, string Error);
public sealed record SourceRow(string Id, string FileName, string Path, int PageCount);
public sealed record WorkPageRow(string Id, string SourceFileId, int PageNo, string Status, string ImagePath, string SourcePath, string EquipmentHint);
public sealed record ExportRow(string DocType, string DocDate, string EquipmentId, string Station, int PageNo, string PageId, string SourcePath);
public sealed record PageRow(string Id, int PageNo, string Status, string DocType, string DocDate, string EquipmentId, string Station,
    double TypeConfidence, string ThumbPath, int OpenFindings, int SeriousFindings, string Error, string SourceFileId);
public sealed record FieldRow(string Code, string Raw, string Norm, double Confidence, double X, double Y, double W, double H);
public sealed record CheckRow(int ItemNo, string Result, double Ink);
public sealed record FindingRow(long Id, string RuleCode, string Severity, string Message, double X, double Y, double W, double H, string Status, string CropPath)
{
    public string PageId { get; init; } = "";
    public int PageNo { get; init; }
    public string DocType { get; init; } = "";
    public string EquipmentId { get; init; } = "";
    public string DocDate { get; init; } = "";
}
public sealed record PersonRow(string Id, string Name);
public sealed record DocRow(string Id, string PageId, string DocType, int SeqNo, string DocDate, string EquipmentId,
    string Station, double Confidence, int PageNo, string SourceFileId);
public sealed record MatchPersonRow(string DocumentId, string PersonId, string PersonName, string Verdict);

public sealed class PageDetail
{
    public string Id { get; set; } = "";
    public string BatchId { get; set; } = "";
    public int PageNo { get; set; }
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string ThumbPath { get; set; } = "";
    public double Quality { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string DocId { get; set; } = "";
    public string DocType { get; set; } = "";
    public double TypeConfidence { get; set; }
    public int SeqNo { get; set; }
    public string DocDate { get; set; } = "";
    public string EquipmentId { get; set; } = "";
    public string Station { get; set; } = "";
    public int RunCount { get; set; }
    public string OcrText { get; set; } = "";
    public List<FieldRow> Fields { get; set; } = new();
    public List<CheckRow> Checks { get; set; } = new();
    public List<SlotRow> Signatures { get; set; } = new();
    public List<FindingRow> Findings { get; set; } = new();
}

public sealed class SlotRow
{
    public long Id { get; init; }
    public string Code { get; init; }
    public string Role { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public bool HasInk { get; init; }
    public string CropPath { get; init; }
    public List<MatchRow> Matches { get; set; } = new();
    public SlotRow(long id, string code, string role, double x, double y, double w, double h, bool hasInk, string crop)
    { Id = id; Code = code; Role = role; X = x; Y = y; W = w; H = h; HasInk = hasInk; CropPath = crop; }
}

public sealed record MatchRow(long Id, int SegmentNo, string Verdict, string PersonId, string PersonName, double Score, string Candidates, string CropPath, bool Confirmed);

