import pathlib
import sys

src, output = map(pathlib.Path, sys.argv[1:])
db = (src / "Services/DatabaseService.cs").read_text()
if db.count("#if !UNIT_TEST") != 1 or db.count("#endif") != 1:
    raise SystemExit("Recheck DatabaseService's production-only guard")
output.mkdir(parents=True, exist_ok=True)
(output / "DatabaseService.cs").write_text(db.replace("#if !UNIT_TEST", "", 1).replace("#endif", "", 1))

ui = (src / "MainWindow.UI.cs").read_text()
logic = (src / "MainWindow.Logic.cs").read_text()
head = ui[:ui.index("namespace ScreenTimeTracker")] + "namespace ScreenTimeTracker { public sealed partial class MainWindow {\n"
chart = ui[ui.index("    public sealed partial class MainWindow"):ui.index("        // ---------------- Restored helper methods")]
chart = chart[chart.index("{") + 1:]
mode = ui[ui.index("        private void UpdateChartViewMode("):ui.index("        private void UpdateDatePickerButtonText(")]
refresh = ui[ui.index("        private void DoChartRefresh("):ui.index("        private void DoIconRetry(")]
load = logic[logic.index("        private void LoadRecordsForDate("):logic.index("        // ---------------- Date range loading logic")]
build = logic[logic.index("        private static List<AppUsageRecord> BuildRecords("):logic.rindex("\n    }\n}")]
maintenance = logic[logic.index("        private void DoAutoSave("):logic.index("        [Flags]")]

def between(text, before, after):
    return text[text.index(before):text.index(after, text.index(before) + len(before))]

average = between(ui, "        private void UpdateAveragePanel(", "        private void ShowNoDataDialog(")
summary = between(ui, "        private void UpdateSummaryTab(", "        private void MaximizeButton_Click(")
summary = summary[summary.index("        private void UpdateSummaryTab(", len("        private void UpdateSummaryTab(")):]

dates = between(ui, "        private (DateTime Start, DateTime End) GetCurrentViewDateRange(", "        private void UpdateSummaryTab(")
update_live = between(ui, "        private void UpdateOrAddLiveRecord(", "        // Helper: rebuild live records")
range_load = between(logic, "        private void LoadRecordsForDateRange(", "        /// <summary>\n        /// Calculates the total active time")
summary = summary[:summary.index("        private void ", len("        private void "))]
import re
summary = re.sub(r"(?m)^ +if \(MostUsedAppIcon != null && MostUsedPlaceholderIcon != null\)\n +\{.*?\n {20}\}", "", summary, flags=re.S)
(output / "MainWindowChart.cs").write_text(head + chart + mode + refresh + load + build + maintenance + average + dates + summary + update_live + range_load + "\n} }\n")
