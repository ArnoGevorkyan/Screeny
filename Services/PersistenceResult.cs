namespace ScreenTimeTracker.Services
{
    public enum PersistenceResult
    {
        Saved,
        DuplicateIgnored,
        RetryableFailure,
        FatalFailure
    }
}
