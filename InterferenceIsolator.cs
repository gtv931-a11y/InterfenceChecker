// InterferenceIsolator.cs  -  Revit 2026 (.NET 8, net8.0-windows, UseWPF=true)
// Add-in classes:  App  -> InterferenceIsolator.App
//                  Cmd  -> InterferenceIsolator.IsolateCommand
// References: RevitAPI, RevitAPIUI (Private=false). Icon: %AppData%\Autodesk\Revit\Addins\2026\CheckIntersectionFormat.png
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using WButton = System.Windows.Controls.Button;
using WTextBox = System.Windows.Controls.TextBox;
using WStyle = System.Windows.Style;
using WBinding = System.Windows.Data.Binding;

namespace InterferenceIsolator
{
    // ------------------------------------------------------------------ RIBBON
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication a)
        {
            var panel = a.CreateRibbonPanel("Interferences"); // Add-Ins tab
            var data = new PushButtonData("IsolateInterferences", "Isolate\nInterferences",
                Assembly.GetExecutingAssembly().Location, typeof(IsolateCommand).FullName)
            { ToolTip = "Isolates and reviews interferences from a Revit HTML report." };
            var btn = (PushButton)panel.AddItem(data);
            string png = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2026", "CheckIntersectionFormat.png");
            if (File.Exists(png))
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(png); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                btn.LargeImage = bi; btn.Image = bi;
            }
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication a) => Result.Succeeded;
    }

    // ------------------------------------------------------------------ COMMAND
    [Transaction(TransactionMode.Manual)]
    public class IsolateCommand : IExternalCommand
    {
        static MainWindow _win;
        static RevitHandler _handler;

        public Result Execute(ExternalCommandData d, ref string message, ElementSet elements)
        {
            try
            {
                if (_win != null) { _win.Activate(); return Result.Succeeded; }
                if (d.Application.ActiveUIDocument == null) { message = "Open a project first."; return Result.Failed; }
                IntPtr owner = d.Application.MainWindowHandle;

                var sw = new StartWindow();
                new WindowInteropHelper(sw).Owner = owner;
                if (sw.ShowDialog() != true) return Result.Cancelled;

                Session session;
                try { session = Session.Load(sw.SelectedPath); }
                catch (Exception ex) { TaskDialog.Show("Interferences", "Could not read the report:\n" + ex.Message); return Result.Failed; }

                if (_handler == null) { _handler = new RevitHandler(); _handler.Event = ExternalEvent.Create(_handler); }
                _win = new MainWindow(session, _handler);
                new WindowInteropHelper(_win).Owner = owner;
                _win.Closed += (s, e) => _win = null;
                _win.Show();
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    // ------------------------------------------------------------------ EXTERNAL EVENT
    public class RevitHandler : IExternalEventHandler
    {
        readonly Queue<Action<UIApplication>> _q = new Queue<Action<UIApplication>>();
        public ExternalEvent Event;
        public Action<string> OnError;
        public void Post(Action<UIApplication> a) { lock (_q) _q.Enqueue(a); Event.Raise(); }
        public void Execute(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication> a;
                lock (_q) { if (_q.Count == 0) break; a = _q.Dequeue(); }
                try { a(app); } catch (Exception ex) { OnError?.Invoke(ex.Message); }
            }
        }
        public string GetName() => "InterferenceIsolatorHandler";
    }

    // ------------------------------------------------------------------ MODEL / PERSISTENCE
    public enum RowStatus { Pending, Resolved, Failed }

    public class HistoryEntry
    {
        public DateTime When { get; set; }
        public string Status { get; set; } = "";
        public string Note { get; set; } = "";
    }

    public class ClashRow : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public string TextA { get; set; } = "";
        public string TextB { get; set; } = "";
        public string ClashName { get; set; } = "";   // only filled when the report provides one (Navisworks)
        public long IdA { get; set; }
        public long IdB { get; set; }
        public RowStatus Status { get; set; }
        public bool DeletedA { get; set; }
        public bool DeletedB { get; set; }
        public DateTime? LastChange { get; set; }
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();

        [JsonIgnore] public string DisplayA => TextA + (DeletedA ? "   [deleted]" : "");
        [JsonIgnore] public string DisplayB => TextB + (DeletedB ? "   [deleted]" : "");
        [JsonIgnore] public string Mark => Status == RowStatus.Resolved ? "✓" : Status == RowStatus.Failed ? "✗" : "";
        [JsonIgnore] public string LastChangeText => LastChange?.ToString("yyyy-MM-dd HH:mm") ?? "";

        public event PropertyChangedEventHandler PropertyChanged;
        public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class SessionData
    {
        public string SourcePath { get; set; } = "";
        public string ProjectFile { get; set; } = "";
        public List<ClashRow> Rows { get; set; } = new List<ClashRow>();
    }

    // ------------------------------------------------------------------ HTML PARSER
    // Supports: Revit "Interference Report", Navisworks table report (ES/EN), Navisworks "Clash Report" (div/span, ES/EN).
    public static class HtmlReportParser
    {
        enum Format { Unknown, Revit, NavisworksTable, NavisworksList }

        static readonly Regex RevitRowRx = new Regex(@"<tr>\s*<td>\s*(\d+)\s*</td>\s*<td>(.*?)</td>\s*<td>(.*?)</td>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        public static List<ClashRow> Parse(string html)
        {
            List<ClashRow> rows;
            switch (Detect(html))
            {
                case Format.NavisworksTable: rows = ParseNavisworksTable(html); break;
                case Format.NavisworksList: rows = ParseNavisworksList(html); break;
                case Format.Revit: rows = ParseRevit(html); break;
                default: throw new InvalidDataException("Unrecognized report format (not a Revit or Navisworks HTML report).");
            }
            if (rows.Count == 0) throw new InvalidDataException("No interference rows were found in the HTML.");
            return rows;
        }

        static Format Detect(string html)
        {
            if (html.Contains("class=\"mainTable\"")) return Format.NavisworksTable;
            if (html.Contains("class=\"clashobject\"")) return Format.NavisworksList;
            if (RevitRowRx.IsMatch(html)) return Format.Revit;
            return Format.Unknown;
        }

        // strips tags first, then decodes entities (so "&lt;No level&gt;" survives), then collapses whitespace
        static string Clean(string s) =>
            Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<.*?>", " ", RegexOptions.Singleline)), @"\s+", " ").Trim();

        static string Describe(string name, long id) => string.IsNullOrEmpty(name) ? $"id {id}" : $"{name} : id {id}";

        // unique Element A / Element B pairs only
        static void AddUnique(List<ClashRow> rows, HashSet<string> seen, long idA, long idB, string nameA, string nameB, string clashName)
        {
            if (!seen.Add(idA + "|" + idB)) return;
            rows.Add(new ClashRow
            {
                Index = rows.Count + 1, IdA = idA, IdB = idB,
                TextA = Describe(nameA, idA), TextB = Describe(nameB, idB), ClashName = clashName ?? ""
            });
        }

        // ---- Revit: <tr><td>n</td><td>... : id 123</td><td>... : id 456</td></tr>
        static List<ClashRow> ParseRevit(string html)
        {
            var rows = new List<ClashRow>(); var seen = new HashSet<string>();
            var idRx = new Regex(@":\s*id\s*(\d+)\s*$", RegexOptions.IgnoreCase);
            foreach (Match m in RevitRowRx.Matches(html))
            {
                string a = Clean(m.Groups[2].Value), b = Clean(m.Groups[3].Value);
                var ia = idRx.Match(a); var ib = idRx.Match(b);
                if (!ia.Success || !ib.Success) continue;
                long idA = long.Parse(ia.Groups[1].Value), idB = long.Parse(ib.Groups[1].Value);
                if (!seen.Add(idA + "|" + idB)) continue;
                rows.Add(new ClashRow { Index = int.Parse(m.Groups[1].Value), TextA = a, TextB = b, IdA = idA, IdB = idB });
            }
            return rows;
        }

        // ---- Navisworks table report ("Informe de conflictos"): rows with "Element ID: n" cells, clash name in 2nd cell
        static List<ClashRow> ParseNavisworksTable(string html)
        {
            var rows = new List<ClashRow>(); var seen = new HashSet<string>();
            var rowRx = new Regex(@"<tr class=""(?:clashGroupRow|childRow|childRowLast|contentRow)"">(.*?)</tr>", RegexOptions.Singleline);
            var cellRx = new Regex(@"<td[^>]*>(.*?)</td>", RegexOptions.Singleline);
            var idRx = new Regex(@"^(?:Element\s*ID|ID\s+de\s+elemento)\s*:\s*(\d+)$", RegexOptions.IgnoreCase);
            foreach (Match r in rowRx.Matches(html))
            {
                var cells = cellRx.Matches(r.Groups[1].Value).Cast<Match>().Select(c => Clean(c.Groups[1].Value)).ToList();
                var ids = cells.Select(c => idRx.Match(c)).Where(x => x.Success)
                    .Select(x => long.Parse(x.Groups[1].Value)).Take(2).ToList();
                if (ids.Count < 2) continue;                       // also skips the summary-table "contentRow" rows
                string clashName = cells.Count > 1 ? cells[1] : "";
                AddUnique(rows, seen, ids[0], ids[1], null, null, clashName);   // this layout has no Item Name
            }
            return rows;
        }

        // ---- Navisworks "Clash Report": <span class="name">Label</span><span class="value">Value</span>
        class Item { public long Id; public string Name = ""; }

        static List<ClashRow> ParseNavisworksList(string html)
        {
            var rows = new List<ClashRow>(); var seen = new HashSet<string>();
            var tokRx = new Regex(@"<h4 class=""clashobject"">|<span class=""name"">(.*?)</span><span class=""value"">(.*?)</span>", RegexOptions.Singleline);
            var clashLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name", "Clash Name", "Nombre", "Nombre de conflicto" };
            var idLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Element ID", "ID de elemento" };
            var itemLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Item Name", "Nombre de elemento" };

            string clashName = ""; var items = new List<Item>(); Item cur = null;
            void Flush()
            {
                if (items.Count >= 2 && items[0].Id != 0 && items[1].Id != 0)
                    AddUnique(rows, seen, items[0].Id, items[1].Id, items[0].Name, items[1].Name, clashName);
                items = new List<Item>(); cur = null;
            }

            foreach (Match t in tokRx.Matches(html))
            {
                if (!t.Groups[1].Success) { cur = new Item(); items.Add(cur); continue; }   // <h4 class="clashobject">
                string label = Clean(t.Groups[1].Value).TrimEnd('*').Trim();
                string value = Clean(t.Groups[2].Value);
                if (clashLabels.Contains(label)) { Flush(); clashName = value; }
                else if (cur != null && idLabels.Contains(label)) { if (cur.Id == 0 && long.TryParse(value, out long id)) cur.Id = id; }
                else if (cur != null && itemLabels.Contains(label)) cur.Name = value;
            }
            Flush();
            return rows;
        }
    }

    public class Session
    {
        public static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InterferenceIsolator");
        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public string SourcePath = "", ProjectFile = "", StorePath = "";
        public List<ClashRow> Rows = new List<ClashRow>();

        public static Session Load(string htmlPath)
        {
            byte[] bytes = File.ReadAllBytes(htmlPath);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 20); // same html => same history
            string html = Encoding.UTF8.GetString(bytes);

            var parsed = HtmlReportParser.Parse(html);

            var s = new Session { SourcePath = htmlPath, StorePath = Path.Combine(StoreDir, hash + ".json"), Rows = parsed };
            var pm = Regex.Match(html, @"Project File:\s*</b>\s*(.*?)\s*<br>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (pm.Success) s.ProjectFile = WebUtility.HtmlDecode(pm.Groups[1].Value).Trim();

            if (File.Exists(s.StorePath))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(s.StorePath), Opts);
                    if (saved?.Rows != null && saved.Rows.Count == parsed.Count) s.Rows = saved.Rows;
                }
                catch { }
            }
            s.Save();
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(
                    new SessionData { SourcePath = SourcePath, ProjectFile = ProjectFile, Rows = Rows }, Opts));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ REVIT LOGIC
    public class Isolator
    {
        const double Scale = 1.15;      // bounding box around both elements is expanded by 1.15x
        const double MinHalf = 0.5;     // minimum half-extent per axis (feet) for slender elements (conduits)
        const string ViewName = "Isolated_Interference";

        readonly HashSet<ElementId> _halftoned = new HashSet<ElementId>();
        string _cleanedKey = "";

        static View3D GetOrCreate3D(Document doc)
        {
            var v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name == ViewName);
            if (v != null) return v;
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            v = View3D.CreateIsometric(doc, vft.Id);
            v.Name = ViewName;
            try { v.Discipline = ViewDiscipline.Coordination; v.DetailLevel = ViewDetailLevel.Fine; } catch { }
            return v;
        }

        void FullClear(Document doc, View3D v)
        {
            v.IsSectionBoxActive = false; doc.Regenerate();
            var def = new OverrideGraphicSettings();
            foreach (var id in new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().ToElementIds())
                try { if (v.GetElementOverrides(id).Halftone) v.SetElementOverrides(id, def); } catch { }
            _halftoned.Clear();
        }

        void ClearTracked(View3D v)
        {
            var def = new OverrideGraphicSettings();
            foreach (var id in _halftoned) try { v.SetElementOverrides(id, def); } catch { }
            _halftoned.Clear();
        }

        public string Isolate(UIApplication app, ClashRow row)
        {
            var uidoc = app.ActiveUIDocument; if (uidoc == null) return "No active document.";
            var doc = uidoc.Document;
            var els = new[] { doc.GetElement(new ElementId(row.IdA)), doc.GetElement(new ElementId(row.IdB)) }
                .Where(e => e != null).ToList();
            if (els.Count == 0) return "Neither element was found in the active document (deleted or located in a linked model).";
            var bbs = els.Select(e => e.get_BoundingBox(null)).Where(b => b != null).ToList();
            if (bbs.Count == 0) return "Elements have no geometry.";

            // box enclosing both elements, then 1.15x from its center
            XYZ mn = new XYZ(bbs.Min(b => b.Min.X), bbs.Min(b => b.Min.Y), bbs.Min(b => b.Min.Z));
            XYZ mx = new XYZ(bbs.Max(b => b.Max.X), bbs.Max(b => b.Max.Y), bbs.Max(b => b.Max.Z));
            XYZ c = (mn + mx) * 0.5;
            double hx = Math.Max((mx.X - mn.X) / 2 * Scale, MinHalf);
            double hy = Math.Max((mx.Y - mn.Y) / 2 * Scale, MinHalf);
            double hz = Math.Max((mx.Z - mn.Z) / 2 * Scale, MinHalf);
            XYZ bmin = new XYZ(c.X - hx, c.Y - hy, c.Z - hz), bmax = new XYZ(c.X + hx, c.Y + hy, c.Z + hz);

            View3D v;
            using (var t = new Transaction(doc, "Isolate interference"))
            {
                t.Start();
                v = GetOrCreate3D(doc);
                string key = doc.PathName + "|" + doc.Title;
                if (key != _cleanedKey) { FullClear(doc, v); _cleanedKey = key; } else ClearTracked(v);

                v.SetSectionBox(new BoundingBoxXYZ { Min = bmin, Max = bmax });
                v.IsSectionBoxActive = true;
                doc.Regenerate();

                var skip = new HashSet<ElementId>(els.Select(e => e.Id));
                var ogs = new OverrideGraphicSettings(); ogs.SetHalftone(true);
                var ids = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bmin, bmax))).ToElementIds();
                foreach (var id in ids)
                {
                    if (skip.Contains(id)) continue;
                    try { v.SetElementOverrides(id, ogs); _halftoned.Add(id); } catch { }
                }
                t.Commit();
            }

            uidoc.ActiveView = v;
            uidoc.Selection.SetElementIds(els.Select(e => e.Id).ToList());
            var uiv = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == v.Id);
            uiv?.ZoomAndCenterRectangle(bmin, bmax);

            string del = (row.DeletedA || row.DeletedB) ? " (one element was not found in the active document)" : "";
            return $"Row {row.Index} isolated{del}.";
        }

        public string ResetView(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document; if (doc == null) return "No active document.";
            var v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name == ViewName);
            if (v == null) return "The interference view does not exist yet.";
            using (var t = new Transaction(doc, "Reset interference view"))
            { t.Start(); FullClear(doc, v); t.Commit(); }
            return "View reset (section box and halftone removed).";
        }

        public void Refresh(UIApplication app, IList<ClashRow> rows, bool exact)
        {
            var doc = app.ActiveUIDocument?.Document; if (doc == null) throw new InvalidOperationException("No active document.");
            foreach (var r in rows)
            {
                var a = doc.GetElement(new ElementId(r.IdA)); var b = doc.GetElement(new ElementId(r.IdB));
                bool dA = a == null, dB = b == null;
                RowStatus ns = (dA || dB) ? RowStatus.Resolved : (Clash(doc, a, b, exact) ? RowStatus.Failed : RowStatus.Resolved);
                if (ns != r.Status || dA != r.DeletedA || dB != r.DeletedB)
                {
                    var notes = new List<string>();
                    if (dA) notes.Add($"Deleted: id {r.IdA}");
                    if (dB) notes.Add($"Deleted: id {r.IdB}");
                    var now = DateTime.Now;
                    r.History.Add(new HistoryEntry { When = now, Status = ns.ToString(), Note = string.Join(" | ", notes) });
                    r.LastChange = now;
                }
                r.Status = ns; r.DeletedA = dA; r.DeletedB = dB;
                r.Touch();
            }
        }

        static bool Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            const double tol = 1e-6;
            return a.Min.X < b.Max.X - tol && a.Max.X > b.Min.X + tol
                && a.Min.Y < b.Max.Y - tol && a.Max.Y > b.Min.Y + tol
                && a.Min.Z < b.Max.Z - tol && a.Max.Z > b.Min.Z + tol;
        }

        static bool Clash(Document doc, Element a, Element b, bool exact)
        {
            var ba = a.get_BoundingBox(null); var bb = b.get_BoundingBox(null);
            bool both = ba != null && bb != null;
            if (both && !Overlap(ba, bb)) return false;   // separated bounding boxes: no longer clashing
            if (both && !exact) return true;              // bounding box mode only
            try
            {
                using (var col = new FilteredElementCollector(doc, new List<ElementId> { b.Id }))
                    return col.WherePasses(new ElementIntersectsElementFilter(a)).GetElementCount() > 0;
            }
            catch { return both; }
        }
    }

    // ------------------------------------------------------------------ HTML REPORT
    static class ReportWriter
    {
        public static string Write(Session s, string folder)
        {
            string E(string x) => WebUtility.HtmlEncode(x ?? "");
            int ok = s.Rows.Count(r => r.Status == RowStatus.Resolved);
            int bad = s.Rows.Count(r => r.Status == RowStatus.Failed);
            int pend = s.Rows.Count - ok - bad;
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Interference Progress Report</title><style>" +
                "body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}table{border-collapse:collapse;width:100%}" +
                "th,td{border:1px solid #b8bfca;padding:6px 8px;font-size:13px;vertical-align:top}th{background:#3B4453;color:#fff}" +
                "tr.ok td{background:#d9f2de}tr.fail td{background:#f6d6d6}tr.pend td{background:#f2f4f7}" +
                "ul{margin:0;padding-left:16px;font-size:12px}</style></head><body><h1>Interference Progress Report</h1>");
            sb.Append($"<p><b>Source report:</b> {E(s.SourcePath)}<br><b>Project:</b> {E(s.ProjectFile)}<br>" +
                $"<b>Generated:</b> {DateTime.Now:yyyy-MM-dd HH:mm:ss}<br><b>Total:</b> {s.Rows.Count} &nbsp; " +
                $"<b>Resolved:</b> {ok} &nbsp; <b>Still clashing:</b> {bad} &nbsp; <b>Not checked:</b> {pend}</p>");
            sb.Append("<table><tr><th>#</th><th>Element A</th><th>Element B</th><th>Status</th><th>Last modification</th><th>History</th></tr>");
            foreach (var r in s.Rows)
            {
                string cls = r.Status == RowStatus.Resolved ? "ok" : r.Status == RowStatus.Failed ? "fail" : "pend";
                string st = r.Status == RowStatus.Resolved ? "✓ Resolved" : r.Status == RowStatus.Failed ? "✗ Still clashing" : "— Not checked";
                sb.Append($"<tr class=\"{cls}\"><td>{r.Index}</td><td>{E(r.DisplayA)}</td><td>{E(r.DisplayB)}</td><td>{st}</td>" +
                    $"<td>{(r.LastChange.HasValue ? r.LastChange.Value.ToString("yyyy-MM-dd HH:mm:ss") : "")}</td><td><ul>");
                foreach (var h in r.History)
                    sb.Append($"<li>{h.When:yyyy-MM-dd HH:mm:ss} — {E(h.Status)}{(string.IsNullOrEmpty(h.Note) ? "" : " — " + E(h.Note))}</li>");
                sb.Append("</ul></td></tr>");
            }
            sb.Append("</table></body></html>");
            string file = Path.Combine(folder, $"InterferenceProgress_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            return file;
        }
    }

    // ------------------------------------------------------------------ STYLE (Revit 2026 theme, RGB 59,68,83)
    static class Ui
    {
        static SolidColorBrush B(byte r, byte g, byte b)
        { var x = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        public static readonly SolidColorBrush Bg = B(59, 68, 83), Dark = B(47, 55, 68), RowN = B(70, 80, 95),
            Fg = B(240, 243, 247), Line = B(43, 50, 61), Green = B(46, 107, 58), Red = B(122, 46, 46),
            Sel = B(95, 113, 146), OkMark = B(110, 224, 127), BadMark = B(255, 107, 107);

        const string BtnXaml = @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
<Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='2' Padding='{TemplateBinding Padding}'>
<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
<ControlTemplate.Triggers>
<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#5B677C'/></Trigger>
<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='#2F3744'/></Trigger>
<Trigger Property='IsEnabled' Value='False'><Setter TargetName='bd' Property='Opacity' Value='0.45'/></Trigger>
</ControlTemplate.Triggers></ControlTemplate>";

        public static WButton Btn(string glyph, string text, string tip)
        {
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            if (glyph != null)
                sp.Children.Add(new TextBlock
                {
                    Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
                    Margin = new Thickness(0, 0, text != null ? 6 : 0, 0), VerticalAlignment = VerticalAlignment.Center
                });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return new WButton
            {
                Content = sp, ToolTip = tip, Template = (ControlTemplate)XamlReader.Parse(BtnXaml),
                Background = B(75, 85, 103), BorderBrush = B(107, 119, 138), Foreground = Fg,
                Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), MinHeight = 28
            };
        }
    }

    // ------------------------------------------------------------------ START WINDOW
    public class StartWindow : Window
    {
        public string SelectedPath { get; private set; }
        readonly WTextBox _tb;
        static string LastFile => Path.Combine(Session.StoreDir, "last.txt");

        public StartWindow()
        {
            Title = "Interferences — Load Report"; Width = 560; Height = 200; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Ui.Bg; Foreground = Ui.Fg;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = "Select the interference report (.html) exported from Revit:", Margin = new Thickness(0, 0, 0, 10) });

            var browse = Ui.Btn(null, "Browse…", null); browse.Margin = new Thickness(8, 0, 0, 0);
            _tb = new WTextBox { Background = Ui.Dark, Foreground = Ui.Fg, CaretBrush = Ui.Fg, BorderBrush = Ui.Line, Padding = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
            try { if (File.Exists(LastFile)) _tb.Text = File.ReadAllText(LastFile).Trim(); } catch { }
            var row = new DockPanel();
            DockPanel.SetDock(browse, System.Windows.Controls.Dock.Right);
            row.Children.Add(browse); row.Children.Add(_tb);
            root.Children.Add(row);

            var ok = Ui.Btn(null, "Load", null); var cancel = Ui.Btn(null, "Cancel", null);
            ok.Width = 90; cancel.Width = 90; cancel.Margin = new Thickness(0);
            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 28, 0, 0) };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            root.Children.Add(btns);
            Content = root;

            browse.Click += (s, e) =>
            {
                var dlg = new OpenFileDialog { Filter = "HTML Report (*.html;*.htm)|*.html;*.htm", Title = "Interference Report" };
                if (dlg.ShowDialog(this) == true) _tb.Text = dlg.FileName;
            };
            ok.Click += (s, e) =>
            {
                string p = _tb.Text.Trim().Trim('"');
                if (!File.Exists(p)) { MessageBox.Show(this, "File does not exist.", "Interferences"); return; }
                SelectedPath = p;
                try { Directory.CreateDirectory(Session.StoreDir); File.WriteAllText(LastFile, p); } catch { }
                DialogResult = true;
            };
            cancel.Click += (s, e) => DialogResult = false;
        }
    }

    // ------------------------------------------------------------------ MAIN WINDOW (history)
    public class MainWindow : Window
    {
        readonly Session _s;
        readonly RevitHandler _h;
        readonly Isolator _iso = new Isolator();
        readonly TextBlock _summary = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
        readonly TextBlock _msg = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        readonly WButton _btnRefresh;
        readonly CheckBox _chkExact;

        public MainWindow(Session s, RevitHandler h)
        {
            _s = s; _h = h; _h.OnError = m => SetMsg("Error: " + m);

            Title = "Interferences — " + Path.GetFileName(s.SourcePath);
            Width = 780; Height = 440; MinWidth = 520; MinHeight = 260;
            ResizeMode = ResizeMode.CanResizeWithGrip; ShowInTaskbar = false;
            Background = Ui.Bg; Foreground = Ui.Fg; FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 20; Top = SystemParameters.WorkArea.Top + 80;

            var dock = new DockPanel();

            // top bar
            var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 6) };
            DockPanel.SetDock(bar, System.Windows.Controls.Dock.Top);
            _btnRefresh = Ui.Btn("\uE72C", "Refresh", "Re-evaluates all interferences");
            var btnExport = Ui.Btn("\uE74E", "Export HTML", "Saves an HTML report with the change history");
            var btnReset = Ui.Btn("\uE777", "Reset View", "Removes section box and halftone from the interference view");
            _chkExact = new CheckBox
            {
                Content = "Exact Geometry", IsChecked = true, Foreground = Ui.Fg, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Checked: confirms with actual geometric intersection (same as Revit). Unchecked: bounding box only."
            };
            bar.Children.Add(_btnRefresh); bar.Children.Add(btnExport); bar.Children.Add(btnReset); bar.Children.Add(_chkExact);

            // bottom bar
            var statusBar = new Border { Background = Ui.Dark, Padding = new Thickness(10, 5, 24, 5) };
            DockPanel.SetDock(statusBar, System.Windows.Controls.Dock.Bottom);
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sp.Children.Add(_summary); sp.Children.Add(_msg);
            statusBar.Child = sp;

            dock.Children.Add(bar); dock.Children.Add(statusBar); dock.Children.Add(BuildGrid());
            Content = dock;

            _btnRefresh.Click += (a, b) => DoRefresh();
            btnReset.Click += (a, b) => _h.Post(app => SetMsg(_iso.ResetView(app)));
            btnExport.Click += (a, b) =>
            {
                var dlg = new OpenFolderDialog { Title = "Folder to save the report" };
                if (dlg.ShowDialog(this) != true) return;
                try { _s.Save(); SetMsg("Report saved: " + ReportWriter.Write(_s, dlg.FolderName)); }
                catch (Exception ex) { SetMsg("Export error: " + ex.Message); }
            };
            Closed += (a, b) => _s.Save();
            UpdateSummary();
            SetMsg("Click a row to isolate that interference.");
        }

        DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserSortColumns = false,
                CanUserResizeRows = false, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Ui.Line, Background = Ui.Bg, Foreground = Ui.Fg, BorderThickness = new Thickness(0),
                RowHeaderWidth = 0, ItemsSource = _s.Rows
            };

            var hs = new WStyle(typeof(DataGridColumnHeader));
            hs.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, Ui.Dark));
            hs.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, Ui.Fg));
            hs.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, Ui.Line));
            hs.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            hs.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 5, 8, 5)));
            hs.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            grid.ColumnHeaderStyle = hs;

            var cs = new WStyle(typeof(DataGridCell));
            cs.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            cs.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
            cs.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Ui.Fg));
            cs.Setters.Add(new Setter(DataGridCell.FocusVisualStyleProperty, null));
            var csSel = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            csSel.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            csSel.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Brushes.White));
            cs.Triggers.Add(csSel);
            grid.CellStyle = cs;

            var rs = new WStyle(typeof(DataGridRow));
            rs.Setters.Add(new Setter(DataGridRow.BackgroundProperty, Ui.RowN));
            rs.Setters.Add(new Setter(DataGridRow.ForegroundProperty, Ui.Fg));
            rs.Setters.Add(new Setter(DataGridRow.MinHeightProperty, 26.0));
            rs.Setters.Add(new EventSetter(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnRowClick)));
            rs.Triggers.Add(StatusTrigger(RowStatus.Resolved, Ui.Green));
            rs.Triggers.Add(StatusTrigger(RowStatus.Failed, Ui.Red));
            var sel = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(DataGridRow.BorderBrushProperty, Brushes.White));
            sel.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(1)));
            rs.Triggers.Add(sel);
            grid.RowStyle = rs;

            grid.Columns.Add(TextCol("#", "Index", new DataGridLength(38), false));
            grid.Columns.Add(TextCol("Element A", "DisplayA", new DataGridLength(1, DataGridLengthUnitType.Star), true));
            grid.Columns.Add(TextCol("Element B", "DisplayB", new DataGridLength(1, DataGridLengthUnitType.Star), true));
            grid.Columns.Add(TextCol("Clash", "ClashName", new DataGridLength(110), true));
            grid.Columns.Add(TextCol("Modified", "LastChangeText", new DataGridLength(115), false));

            var ms = new WStyle(typeof(TextBlock));
            ms.Setters.Add(new Setter(TextBlock.FontSizeProperty, 16.0));
            ms.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            ms.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            ms.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            var okT = new DataTrigger { Binding = new WBinding("Status"), Value = RowStatus.Resolved };
            okT.Setters.Add(new Setter(TextBlock.ForegroundProperty, Ui.OkMark));
            var badT = new DataTrigger { Binding = new WBinding("Status"), Value = RowStatus.Failed };
            badT.Setters.Add(new Setter(TextBlock.ForegroundProperty, Ui.BadMark));
            ms.Triggers.Add(okT); ms.Triggers.Add(badT);
            grid.Columns.Add(new DataGridTextColumn
            { Header = "", Binding = new WBinding("Mark"), Width = new DataGridLength(38), ElementStyle = ms });
            return grid;
        }

        static DataTrigger StatusTrigger(RowStatus st, Brush bg)
        {
            var t = new DataTrigger { Binding = new WBinding("Status"), Value = st };
            t.Setters.Add(new Setter(DataGridRow.BackgroundProperty, bg));
            return t;
        }

        static DataGridTextColumn TextCol(string header, string path, DataGridLength w, bool tip)
        {
            var st = new WStyle(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            st.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            st.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(6, 0, 6, 0)));
            if (tip) st.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new WBinding(path)));
            return new DataGridTextColumn { Header = header, Binding = new WBinding(path), Width = w, ElementStyle = st };
        }

        void OnRowClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow r && r.Item is ClashRow row)
                _h.Post(app => SetMsg(_iso.Isolate(app, row)));
        }

        void DoRefresh()
        {
            _btnRefresh.IsEnabled = false; SetMsg("Re-evaluating…");
            bool exact = _chkExact.IsChecked == true;
            _h.Post(app =>
            {
                try
                {
                    _iso.Refresh(app, _s.Rows, exact);
                    _s.Save(); UpdateSummary();
                    SetMsg("Updated " + DateTime.Now.ToString("HH:mm:ss"));
                }
                finally { _btnRefresh.IsEnabled = true; }
            });
        }

        void UpdateSummary()
        {
            int ok = _s.Rows.Count(r => r.Status == RowStatus.Resolved);
            int bad = _s.Rows.Count(r => r.Status == RowStatus.Failed);
            _summary.Text = $"✓ {ok}   ✗ {bad}   Total {_s.Rows.Count}";
        }

        void SetMsg(string m) { _msg.Text = m; }
    }
}