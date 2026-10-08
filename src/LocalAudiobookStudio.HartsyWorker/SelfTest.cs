using HartsyInference.Engine;

namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>
/// <c>--selftest</c>, run by the app after installing: brings up the backend the worker will use. Exit codes
/// as in protocol.py: 0 OK, 1 error, 2 CUDA required but unavailable.
/// </summary>
internal static class SelfTest
{
    public static Task<int> RunAsync()
    {
        try
        {
            // LAS_DEVICES (from the manifest's "devices") says whether CUDA is required.
            var devices = (Environment.GetEnvironmentVariable("LAS_DEVICES") ?? "cuda")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!devices.Contains("cuda"))
                return Task.FromResult(Check("cpu") ? 0 : 1);
            if (Check("cuda")) return Task.FromResult(0);
            if (devices.Contains("cpu"))
            {
                Protocol.Log("CUDA is not available; this worker will only run on the CPU.");
                return Task.FromResult(Check("cpu") ? 0 : 1);
            }
            return Task.FromResult(Environment.GetEnvironmentVariable("LAS_ALLOW_CPU") == "1" ? 0 : 2);
        }
        catch (Exception ex)
        {
            Protocol.Log(ex.ToString());
            return Task.FromResult(1);
        }
    }

    private static bool Check(string backend)
    {
        try
        {
            using var engine = new InferenceEngine(backend);
            Protocol.Log($"{backend}: {engine.ComputeBackend.GetType().Name} OK");
            return true;
        }
        catch (Exception ex)
        {
            Protocol.Log($"{backend} backend failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
