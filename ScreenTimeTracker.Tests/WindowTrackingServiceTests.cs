using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

namespace ScreenTimeTracker.Tests
{
    [TestClass]
    public class WindowTrackingServiceTests
    {
        [TestMethod]
        public void PauseTrackingForSuspend_OpenCurrentAndIdleRecords_FinalizesBoth()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            service.SetOpenRecordsForTest(
                CreateRecord("notepad", "Notepad"),
                CreateRecord("Idle / Away", "Idle / Away"));

            service.PauseTrackingForSuspend();

            Assert.IsFalse(service.IsTracking);
            Assert.HasCount(2, finalized);
            CollectionAssert.Contains(finalized.Select(s => s.ProcessName).ToList(), "notepad");
            CollectionAssert.Contains(finalized.Select(s => s.ProcessName).ToList(), "Idle / Away");
        }

        [TestMethod]
        public void ResumeTrackingAfterSuspend_AfterPause_RestartsTrackingWithoutDuplicateFinalization()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            service.SetOpenRecordsForTest(CreateRecord("notepad", "Notepad"), idleRecord: null);

            service.PauseTrackingForSuspend();
            service.ResumeTrackingAfterSuspend();

            Assert.IsTrue(service.IsTracking);
            Assert.HasCount(1, finalized);
        }

        [TestMethod]
        public void StopTracking_RepeatedAfterFinalization_DoesNotEmitDuplicateSlices()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            service.SetOpenRecordsForTest(
                CreateRecord("explorer", "File Explorer"),
                CreateRecord("Idle / Away", "Idle / Away"));

            service.StopTracking();
            service.StopTracking();

            Assert.HasCount(2, finalized);
        }

        [TestMethod]
        public void UpdateFocusedRecordForTest_FocusedRecord_DoesNotOverwriteAccumulatedDuration()
        {
            using var service = new WindowTrackingService();
            var current = CreateRecord("notepad", "Notepad");
            current.SetFocus(true);
            service.SetOpenRecordsForTest(current, idleRecord: null);

            service.UpdateFocusedRecordForTest(DateTime.Now);

            Assert.AreEqual(TimeSpan.Zero, current._accumulatedDuration);
        }

        [TestMethod]
        public void GetRecords_OpenRecords_ReturnsSnapshots()
        {
            using var service = new WindowTrackingService();
            var current = CreateRecord("notepad", "Notepad");
            service.SetOpenRecordsForTest(current, idleRecord: null);

            var snapshot = service.GetRecords().Single();
            snapshot.ProcessName = "changed";
            snapshot._accumulatedDuration = TimeSpan.FromHours(3);

            Assert.AreEqual("notepad", service.CurrentRecord?.ProcessName);
            Assert.AreNotSame(current, snapshot);
        }

        [TestMethod]
        public void GetRecords_FocusedRecord_ReturnsStableDurationSnapshot()
        {
            using var service = new WindowTrackingService();
            var current = CreateRecord("notepad", "Notepad");
            current._accumulatedDuration = TimeSpan.FromSeconds(5);
            current.SetFocus(true);
            service.SetOpenRecordsForTest(current, idleRecord: null);

            var snapshot = service.GetRecords().Single();
            var firstDuration = snapshot.Duration;
            Thread.Sleep(25);

            Assert.AreEqual(firstDuration, snapshot.Duration);
            Assert.IsTrue(snapshot.IsFocused);
        }

        [TestMethod]
        public void UpdateFocusedRecordForTest_PreviousDayRecord_FinalizesAtEndOfPreviousDay()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            var yesterday = DateTime.Today.AddDays(-1);
            var current = CreateRecord("notepad", "Notepad");
            current.StartTime = yesterday.AddHours(23);
            current.Date = yesterday;
            current.SetFocus(true);
            service.SetOpenRecordsForTest(current, idleRecord: null);

            service.UpdateFocusedRecordForTest(DateTime.Today);

            Assert.HasCount(1, finalized);
            Assert.AreEqual(yesterday.AddDays(1).AddSeconds(-1), finalized[0].EndTime);
            Assert.AreEqual(yesterday, finalized[0].Date);
            Assert.IsNull(service.CurrentRecord);
        }

        [TestMethod]
        public void ProcessWindowChangeForTest_DifferentWindow_FinalizesPreviousAndStartsNewRecord()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            var current = CreateRecord("notepad", "Notepad");
            current.WindowHandle = new IntPtr(1);
            current.ProcessId = 1;
            current.WindowTitle = "Notes";
            current.SetFocus(true);
            service.SetOpenRecordsForTest(current, idleRecord: null);

            service.ProcessWindowChangeForTest(new IntPtr(2), 2, "calc", "Calculator");

            Assert.HasCount(1, finalized);
            Assert.AreEqual("notepad", finalized[0].ProcessName);
            Assert.AreEqual("calc", service.CurrentRecord?.ProcessName);
            Assert.AreEqual("Calculator", service.CurrentRecord?.WindowTitle);
            Assert.IsTrue(service.CurrentRecord?.IsFocused);
        }

        [TestMethod]
        public void ProcessWindowChangeForTest_SameWindow_DoesNotFinalizeDuplicateSlice()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            var current = CreateRecord("notepad", "Notepad");
            current.WindowHandle = new IntPtr(1);
            current.ProcessId = 1;
            current.WindowTitle = "Notes";
            current.SetFocus(true);
            service.SetOpenRecordsForTest(current, idleRecord: null);

            service.ProcessWindowChangeForTest(new IntPtr(1), 1, "notepad", "Notes");

            Assert.HasCount(0, finalized);
            Assert.AreSame(current, service.CurrentRecord);
        }

        [TestMethod]
        public void ApplyIdleStateForTest_EnteringIdle_FinalizesActiveAndStartsIdleRecord()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            var now = DateTime.Today.AddHours(10);
            var current = CreateRecord("notepad", "Notepad");
            current.StartTime = now.AddMinutes(-5);
            current.Date = now.Date;
            current.SetFocus(true);
            service.SetOpenRecordsForTest(current, idleRecord: null);

            var shouldRefreshActiveWindow = service.ApplyIdleStateForTest(currentlyIdle: true, now);

            Assert.IsFalse(shouldRefreshActiveWindow);
            Assert.HasCount(1, finalized);
            Assert.AreEqual("notepad", finalized[0].ProcessName);
            Assert.AreEqual(now, finalized[0].EndTime);
            Assert.IsNull(service.CurrentRecord);
            Assert.AreEqual("Idle / Away", service.GetRecords().Single().ProcessName);
        }

        [TestMethod]
        public void ApplyIdleStateForTest_ExitingIdle_FinalizesIdleAndRequestsActiveRefresh()
        {
            using var service = new WindowTrackingService();
            var finalized = new List<UsageSlice>();
            service.UsageSliceFinalized += (_, slice) => finalized.Add(slice);

            var now = DateTime.Today.AddHours(11);
            var idleRecord = CreateRecord("Idle / Away", "Idle / Away");
            idleRecord.StartTime = now.AddMinutes(-10);
            idleRecord.Date = now.Date;
            service.SetOpenRecordsForTest(currentRecord: null, idleRecord);

            var shouldRefreshActiveWindow = service.ApplyIdleStateForTest(currentlyIdle: false, now);

            Assert.IsTrue(shouldRefreshActiveWindow);
            Assert.HasCount(1, finalized);
            Assert.AreEqual("Idle / Away", finalized[0].ProcessName);
            Assert.AreEqual(now, finalized[0].EndTime);
            Assert.IsFalse(service.GetRecords().Any());
        }

        private static AppUsageRecord CreateRecord(string processName, string applicationName)
        {
            return new AppUsageRecord
            {
                ProcessName = processName,
                ApplicationName = applicationName,
                WindowTitle = applicationName,
                StartTime = DateTime.Now.AddSeconds(-10),
                Date = DateTime.Today
            };
        }
    }
}
