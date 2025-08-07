using System.Diagnostics.Metrics;

public interface IStorageMetrics
{
    void TrackOpen(string deviceName);
    void TrackClose(string deviceName);
    void TrackRead(string deviceName, int bytes, double elapsedMs);
    void TrackWrite(string deviceName, int bytes, double elapsedMs);
    void TrackError(string deviceName, string operation);
}
