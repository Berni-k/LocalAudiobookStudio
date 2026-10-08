namespace LocalAudiobookStudio.Workers.Manifest;

/// <summary>
/// One shared Python environment from <c>workers/environments.json</c>. Workers name it with "envGroup"; it
/// decides the Python version, the torch build and the versions forced over upstream pins, so the workers
/// in it share one set of packages (and uv hardlinks the same files into every environment that uses them).
/// </summary>
public sealed class EnvironmentDefinition
{
    /// <summary>The folder name under <c>envs/</c>: torch&lt;version&gt;-&lt;cuda&gt;-&lt;library line&gt;, or "basic".</summary>
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Python { get; set; } = "3.12";
    public string? TorchIndexUrl { get; set; }

    /// <summary>Requirements installed with every worker in the environment (the torch build).</summary>
    public List<string> Packages { get; set; } = new();

    /// <summary>uv overrides: each replaces every other requirement on its package, upstream pins included.</summary>
    public List<string> Overrides { get; set; } = new();
}

/// <summary>Contents of <c>workers/environments.json</c>.</summary>
public sealed class EnvironmentsFile
{
    public List<EnvironmentDefinition> Environments { get; set; } = new();
}
